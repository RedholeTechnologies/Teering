using System.Diagnostics;

namespace Treering.Core;

/// <summary>
/// scip-python 을 부르는 법. 0.6.6 은 그대로는 Windows 에서 뜨지 않고, pip 를 못 찾으면 멈춘다.
/// 색인기를 고치지 않고 부르는 법만 바꿔서 둘 다 메운다.
/// </summary>
public static class PythonIndexing
{
    // scip-python 은 new RegExp(path.sep, "g") 를 만든다. Windows 의 "\" 는 그것만으로는 패턴이 아니라
    // 시작하자마자 죽는다. 딱 그 패턴 하나만 이스케이프하고, 다른 정규식은 건드리지 않는다.
    private const string SeparatorShim = """
        // Loaded by Treering before scip-python on Windows. scip-python 0.6.6 builds
        // `new RegExp(path.sep, "g")`, and "\" alone is not a valid pattern. Escape exactly that one.
        const Native = RegExp;
        const fix = pattern => (pattern === "\\" ? "\\\\" : pattern);
        globalThis.RegExp = new Proxy(Native, {
          construct: (target, [pattern, flags], newTarget) => Reflect.construct(target, [fix(pattern), flags], newTarget),
          apply: (target, self, [pattern, flags]) => target(fix(pattern), flags),
        });
        """;

    /// <summary>
    /// Windows 에서는 scip-python 을 node 로 직접 띄우고, 그 앞에 위의 고침을 싣는다.
    /// <c>index</c> 보다 먼저 불러야 한다 — node 의 인자가 앞에 온다.
    /// </summary>
    public static void UseLauncher(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows()) return;

        var shim = Executables.Find("scip-python");
        var node = Executables.Find("node");
        if (!Path.IsPathRooted(shim) || !Path.IsPathRooted(node)) return;   // 없으면 원래 길이 "설치하라" 고 말한다

        var entry = Path.Combine(Path.GetDirectoryName(shim)!, "node_modules", "@sourcegraph", "scip-python", "index.js");
        if (!File.Exists(entry)) return;

        start.FileName = node;
        start.ArgumentList.Add("--require");
        start.ArgumentList.Add(WriteTool("scip-python-windows.cjs", SeparatorShim));
        start.ArgumentList.Add(entry);
    }

    /// <summary>
    /// scip-python 은 pip 로 설치된 패키지를 읽어 남의 심볼에 패키지 이름을 붙인다. 그 pip 를 찾아 준다 —
    /// 저장소의 가상 환경이 먼저, 없으면 pip 가 딸린 아무 Python. 어디에도 없으면 빈 패키지 목록을 준다:
    /// 남의 패키지 이름을 모를 뿐, 우리 코드의 그래프는 같다.
    /// </summary>
    public static void UseEnvironment(ProcessStartInfo start, string directory)
    {
        var prepend = PipDirectories(directory);
        if (prepend is null)
        {
            start.ArgumentList.Add("--environment");
            start.ArgumentList.Add(WriteTool("no-packages.json", "[]"));
            return;
        }

        if (prepend.Count == 0) return;   // PATH 에 이미 있다
        var path = start.Environment.TryGetValue("PATH", out var current) && current is not null
            ? current
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        start.Environment["PATH"] = string.Join(Path.PathSeparator, prepend.Append(path));
    }

    /// <summary>
    /// PATH 앞에 둘 디렉터리. 빈 목록은 "이미 PATH 에 있다", <c>null</c> 은 "pip 가 없다".
    /// Python 자신도 함께 둔다 — scip-python 은 환경을 물을 때 <c>python</c> 도 부른다.
    /// </summary>
    public static IReadOnlyList<string>? PipDirectories(string directory)
    {
        var windows = OperatingSystem.IsWindows();
        var bin = windows ? "Scripts" : "bin";
        var pip = windows ? "pip.exe" : "pip";

        // 저장소의 가상 환경 — 그 프로젝트가 실제로 쓰는 패키지가 거기 있다.
        foreach (var venv in new[] { ".venv", "venv", "env" })
        {
            var scripts = Path.Combine(directory, venv, bin);
            if (File.Exists(Path.Combine(scripts, pip))) return [scripts];
        }

        if (Path.IsPathRooted(Executables.Find("pip3")) || Path.IsPathRooted(Executables.Find("pip"))) return [];

        // Windows 의 Python 은 Scripts 가 PATH 에 없는 일이 흔하다. py 런처가 설치된 Python 을 안다.
        if (windows)
        {
            foreach (var python in InstalledPythons())
            {
                var home = Path.GetDirectoryName(python)!;
                var scripts = Path.Combine(home, "Scripts");
                if (File.Exists(Path.Combine(scripts, pip))) return [scripts, home];
            }
        }

        return null;
    }

    /// <summary>
    /// 설치된 패키지와 그 파일 목록을 뽑는다. scip-python 이 스스로 뽑는 것과 같은 모양이다.
    /// 패키지 이름을 넘기지 않고 전부 뽑는다 — scip-python 은 <c>pip list</c> 로 이름을 받아 거르지만,
    /// 그 둘은 같은 환경에서 나오므로 결과가 같다.
    /// </summary>
    private const string PackagesScript = """
        import json, sys, importlib.metadata as metadata
        packages = []
        for dist in metadata.distributions():
            name = dist.metadata["Name"]
            if not name:
                continue
            files = [str(f) for f in (dist.files or [])]
            files = [f for f in files if f.endswith((".py", ".pyi")) and not f.startswith("..") and "__pycache__" not in f]
            packages.append({"name": name, "version": dist.version, "files": files})
        json.dump(packages, sys.stdout)
        """;

    /// <summary>
    /// scip-python 이 남의 심볼에 패키지 이름을 붙이려고 모으는 «패키지 → 파일» 목록을 우리가 대신 뽑아 건넨다.
    ///
    /// 그대로 두면 Windows 에서 결과가 실행마다 다르다. 목록을 뽑는 스크립트를 <c>python3</c> 으로 돌리는데
    /// 가상 환경에는 <c>python3.exe</c> 가 없어 PATH 의 다른 것 — 흔히 Microsoft Store 의 빈 껍데기 — 이 불리고
    /// 스크립트는 실패한다. 그러면 <c>pip show -f</c> 로 넘어가는데 1분 제한이 있고, 패키지가 백 개쯤이면 바쁠 때는
    /// 넘긴다(실측 81초). 넘기면 목록이 비어 모든 라이브러리 심볼이 우리 패키지 이름을 달고, 라이브러리로 가는 선
    /// 천여 개가 스냅샷마다 끊겼다 이어졌다 한다. 그 환경의 python 으로 직접 뽑으면 1초 안팎이고 매번 같다.
    ///
    /// 뽑지 못하면 아무것도 바꾸지 않는다 — 색인기가 하던 대로 한다.
    /// </summary>
    public static void PinPackages(ProcessStartInfo start, string? python, string file)
    {
        if (python is null || start.ArgumentList.Contains("--environment")) return;
        if (!WritePackages(python, file)) return;

        start.ArgumentList.Add("--environment");
        start.ArgumentList.Add(file);
    }

    /// <summary>
    /// 그 프로젝트의 pip 와 짝인 python. <see cref="PipDirectories"/> 와 같은 순서로 고른다 —
    /// 저장소의 가상 환경, PATH 의 python, py 런처가 아는 python. 없으면 <c>null</c>.
    /// </summary>
    public static string? PythonFor(string directory)
    {
        var windows = OperatingSystem.IsWindows();
        var bin = windows ? "Scripts" : "bin";
        var exe = windows ? "python.exe" : "python";

        foreach (var venv in new[] { ".venv", "venv", "env" })
        {
            var python = Path.Combine(directory, venv, bin, exe);
            if (File.Exists(python)) return python;
        }

        var dirs = PipDirectories(directory);
        if (dirs is null) return null;
        if (dirs.Count == 0) return Executables.Find("python") is var found && Path.IsPathRooted(found) ? found : null;
        return dirs.Select(dir => Path.Combine(dir, exe)).FirstOrDefault(File.Exists);
    }

    private static readonly Lazy<IReadOnlyList<string>> Stdlib = new(() => StdlibModules(TypeshedStdlib()));

    /// <summary>
    /// scip-python 이 표준 라이브러리로 읽는 타입 정의(typeshed)의 모듈 이름들 — <c>asyncio.windows_events</c> 꼴.
    /// 색인기가 이름을 잃은 표준 라이브러리 모듈의 제자리를 찾는 데 쓴다. 못 찾으면 빈 목록.
    /// </summary>
    public static IReadOnlyList<string> StdlibModules() => Stdlib.Value;

    public static IReadOnlyList<string> StdlibModules(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.pyi", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/')[..^".pyi".Length])
            .Select(stem => stem.EndsWith("/__init__", StringComparison.Ordinal) ? stem[..^"/__init__".Length] : stem)
            .Where(stem => stem.Length > 0 && stem != "__init__")
            .Select(stem => stem.Replace('/', '.'))
            .ToList();
    }

    /// <summary>npm 전역 설치 안의 typeshed. Windows 는 실행 파일 옆, 나머지는 <c>../lib</c> 아래에 둔다.</summary>
    private static string? TypeshedStdlib()
    {
        var shim = Executables.Find("scip-python");
        if (!Path.IsPathRooted(shim)) return null;

        var bin = Path.GetDirectoryName(shim)!;
        return new[] { Path.Combine(bin, "node_modules"), Path.Combine(bin, "..", "lib", "node_modules") }
            .Select(modules => Path.Combine(modules, "@sourcegraph", "scip-python", "dist", "typeshed-fallback", "stdlib"))
            .FirstOrDefault(Directory.Exists);
    }

    /// <summary><see cref="PinPackages"/> 가 쓴 목록을 도로 읽는다. 없거나 깨졌으면 null.</summary>
    public static List<(string Name, string Version, IEnumerable<string> Files)>? ReadPackages(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.EnumerateArray()
                .Select(package => (
                    package.GetProperty("name").GetString() ?? string.Empty,
                    package.GetProperty("version").GetString() ?? string.Empty,
                    (IEnumerable<string>)package.GetProperty("files").EnumerateArray().Select(path => path.GetString() ?? string.Empty).ToList()))
                .Where(package => package.Item1.Length > 0)
                .ToList();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            return null;
        }
    }

    private static bool WritePackages(string python, string file)
    {
        try
        {
            var start = new ProcessStartInfo(python)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(PackagesScript);

            using var process = Process.Start(start);
            if (process is null) return false;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return false;
            }

            if (process.ExitCode != 0) return false;
            var json = output.Result;
            using (var document = System.Text.Json.JsonDocument.Parse(json))
            {
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
            }

            File.WriteAllText(file, json);
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or System.Text.Json.JsonException or IOException)
        {
            return false;
        }
    }

    private static IEnumerable<string> InstalledPythons()
    {
        var launcher = Executables.Find("py");
        if (!Path.IsPathRooted(launcher)) return [];
        try
        {
            var start = new ProcessStartInfo(launcher, "-0p") { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start);
            if (process is null) return [];
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            // " -V:3.12          C:\...\Python312\python.exe" — 줄 끝의 경로만 쓴다.
            return output.Split('\n')
                .Select(line => line.Trim())
                .Select(line => line.IndexOf(":\\", StringComparison.Ordinal) is var at and > 0 ? line[(at - 1)..].Trim() : null)
                .OfType<string>()
                .Where(File.Exists)
                .ToList();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    // 도구 파일은 저장소가 아니라 Treering 의 집에 둔다. 내용이 같으면 다시 쓰지 않는다.
    private static string WriteTool(string name, string content)
    {
        var path = Path.Combine(Projects.Home, "tools", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
        return path;
    }
}
