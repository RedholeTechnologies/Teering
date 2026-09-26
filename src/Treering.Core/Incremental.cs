using System.ComponentModel;
using System.Diagnostics;

namespace Treering.Core;

/// <summary>
/// 한 언어를 색인하는 방법. 무엇이 그 언어의 파일이고, 무엇이 프로젝트의 경계이며,
/// 어떤 명령으로 색인하는가 — 증분 갱신이 언어에 대해 알아야 하는 것은 이 셋이 전부다.
/// </summary>
/// <param name="Name">색인기 실행 파일 이름.</param>
/// <param name="Extensions">이 언어의 소스 파일.</param>
/// <param name="Markers">
/// 프로젝트의 경계를 알리는 파일. 소스에서 위로 올라가며 처음 만나는 것이 그 파일의 프로젝트다.
/// </param>
/// <param name="MarkerIsProject">
/// 경계 파일 자체가 프로젝트인가(<c>.csproj</c>), 그 파일이 있는 폴더가 프로젝트인가(<c>package.json</c>).
/// </param>
/// <param name="Install">색인기가 없을 때 사람에게 보여 줄 설치 명령.</param>
/// <param name="Language">사람에게 보여 줄 언어 이름.</param>
public sealed record Indexer(
    string Language,
    string Name,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Markers,
    bool MarkerIsProject,
    string Install);

/// <summary>다시 색인할 프로젝트 하나와, 그렇게 만든 바뀐 파일들.</summary>
/// <param name="ProjectPath">.csproj 파일, 또는 package.json · pyproject.toml 이 있는 폴더.</param>
public sealed record AffectedProject(Indexer Indexer, string ProjectPath, IReadOnlyList<string> ChangedFiles)
{
    /// <summary>사람에게 보여 줄 이름 — 프로젝트 파일 이름, 아니면 폴더 이름.</summary>
    public string Name => Path.GetFileName(ProjectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

/// <summary>
/// 증분 갱신의 앞단. 「무엇이 바뀌었나」 에서 「무엇을 다시 색인해야 하나」 까지.
///
/// 색인기는 언제나 전량이다. 우리가 줄일 수 있는 것은 <b>다시 색인할 프로젝트를 최소로 고르는 것</b>
/// 하나뿐이고, 그래서 성능 작업이 사실상 이 파일에 모여 있다.
///
/// 언어는 파일 확장자로 고른다. 한 리포에 C# 과 TypeScript 가 같이 있으면 각자의 프로젝트가
/// 각자의 색인기로 다시 색인된다 — 적재기는 SCIP 만 보므로 둘을 구별할 필요가 없다.
/// </summary>
public static class Incremental
{
    public static readonly Indexer CSharp = new(
        "C#", "scip-dotnet", [".cs"], ["*.csproj"], MarkerIsProject: true,
        "dotnet tool install --global scip-dotnet");

    public static readonly Indexer TypeScript = new(
        "TypeScript", "scip-typescript", [".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"], ["package.json"],
        MarkerIsProject: false,
        "npm install -g @sourcegraph/scip-typescript");

    public static readonly Indexer Python = new(
        "Python", "scip-python", [".py", ".pyi"], ["pyproject.toml", "setup.py", "setup.cfg"], MarkerIsProject: false,
        "npm install -g @sourcegraph/scip-python");

    public static readonly IReadOnlyList<Indexer> Indexers = [CSharp, TypeScript, Python];

    /// <summary>
    /// <paramref name="since"/> 이후 바뀐 파일. 비우면 작업 트리의 미커밋 변경을 본다.
    /// 어느 언어의 파일인지는 여기서 거르지 않는다 — <see cref="AffectedProjects"/> 가 고른다.
    /// </summary>
    public static List<string> ChangedFiles(string repo, string? since)
    {
        // -z: a NUL after each path, and nothing quoted or escaped. Without it git writes a name
        // with non-ASCII letters as "\354\243\274.py" and a rename as "old -> new" on one line -
        // and neither is a file. A rename counts under both names: the old path's project lost
        // what the file held.
        var arguments = since is null
            ? "status --porcelain=v1 -z --untracked-files=all"
            : $"diff --name-only -z --no-renames {since} HEAD";

        var records = Git(repo, arguments).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<string>();

        if (since is not null)
        {
            paths.AddRange(records);
        }
        else
        {
            // "XY path"; after a rename or a copy, the old path follows as a record of its own.
            for (var i = 0; i < records.Length; i++)
            {
                var record = records[i];
                if (record.Length < 4) continue;
                paths.Add(record[3..]);
                if (record[0] is 'R' or 'C' && i + 1 < records.Length) paths.Add(records[++i]);
            }
        }

        return paths
            .Select(path => path.Trim())
            .Where(path => IndexerFor(path) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>이 파일을 색인하는 언어. 모르는 파일이면 없음.</summary>
    public static Indexer? IndexerFor(string file)
    {
        var extension = Path.GetExtension(file);
        return Indexers.FirstOrDefault(indexer =>
            indexer.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 바뀐 파일이 속한 프로젝트. 파일에서 위로 올라가며 그 언어의 가장 가까운 경계를 찾는다.
    ///
    /// 폴더 이름이 아니라 실제 프로젝트 파일로 잇는 것이 중요하다 — 어셈블리 이름은
    /// 폴더와 다를 수 있다(실측: 폴더 <c>Acme.Shop.App</c>, 어셈블리 <c>상점앱</c>).
    /// 경계를 못 찾은 파일은 버린다. 아무 프로젝트에나 붙이면 엉뚱한 것을 다시 색인한다.
    /// </summary>
    public static List<AffectedProject> AffectedProjects(string repo, IEnumerable<string> changedFiles)
    {
        var byProject = new Dictionary<(Indexer, string), List<string>>();
        var root = Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 들여오기와 같은 규칙: pyproject.toml · setup.py 가 리포 어디에도 없으면 리포 전체가 파이썬
        // 프로젝트 하나다. 이 규칙이 여기만 없으면, 그렇게 들여온 리포는 갱신이 영영 «바뀐 것 없음» 이다.
        var pythonRootIsProject = new Lazy<bool>(() => !Import.HasMarker(root, Python));

        foreach (var file in changedFiles)
        {
            var indexer = IndexerFor(file);
            if (indexer is null) continue;

            var project = FindProject(root, Path.GetFullPath(Path.Combine(root, file)), indexer);
            if (project is null && indexer == Python && pythonRootIsProject.Value) project = root;
            if (project is null) continue;

            var key = (indexer, project);
            if (!byProject.TryGetValue(key, out var files)) byProject[key] = files = [];
            files.Add(file);
        }

        return byProject
            .Select(pair => new AffectedProject(pair.Key.Item1, pair.Key.Item2, pair.Value))
            .OrderBy(project => project.Indexer.Name, StringComparer.Ordinal)
            .ThenBy(project => project.ProjectPath, StringComparer.Ordinal)
            .ToList();
    }

    private static string? FindProject(string root, string file, Indexer indexer)
    {
        var directory = Path.GetDirectoryName(file);

        while (!string.IsNullOrEmpty(directory) && directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var marker in indexer.Markers)
            {
                var found = Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory, marker).FirstOrDefault()
                    : null;
                if (found is not null) return indexer.MarkerIsProject ? found : directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>
    /// 프로젝트 하나를 색인하는 명령. 실행하지 않고 만들기만 한다 — 무엇을 돌리는지 시험할 수 있게.
    /// </summary>
    /// <param name="restore">
    /// C# 만 해당한다. 끄면 <c>dotnet restore</c> 를 건너뛴다 — 실측에서 이것이 증분 시간의 대부분이었다.
    /// 작업 트리는 이미 복원돼 있는 것이 보통이고, 패키지를 새로 추가했을 때만 켜야 한다.
    /// </param>
    public static ProcessStartInfo CommandFor(AffectedProject project, string outputPath, bool restore = false)
    {
        var indexer = project.Indexer;
        var directory = indexer.MarkerIsProject ? Path.GetDirectoryName(project.ProjectPath)! : project.ProjectPath;

        var start = new ProcessStartInfo(Executables.Find(indexer.Name))
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (indexer == Python) PythonIndexing.UseLauncher(start);
        start.ArgumentList.Add("index");

        if (indexer == CSharp)
        {
            start.ArgumentList.Add(project.ProjectPath);
            // 이 플래그가 없으면 심볼에 패키지가 붙지 않아 프로젝트를 따로 색인한 순간 ID 가 갈린다.
            start.ArgumentList.Add("--allow-global-symbol-definitions");
            if (!restore) start.ArgumentList.Add("--skip-dotnet-restore");
        }
        else if (indexer == TypeScript)
        {
            start.ArgumentList.Add("--cwd");
            start.ArgumentList.Add(directory);
            // tsconfig 가 없는 JavaScript 프로젝트도 색인되게 한다.
            if (!File.Exists(Path.Combine(directory, "tsconfig.json"))) start.ArgumentList.Add("--infer-tsconfig");
            // 워크스페이스의 뿌리면 색인기에게 알린다. 모르면 뿌리의 package.json 만 보고
            // 안에 든 패키지들을 건너뛴다.
            if (File.Exists(Path.Combine(directory, "pnpm-workspace.yaml"))) start.ArgumentList.Add("--pnpm-workspaces");
            else if (DeclaresWorkspaces(Path.Combine(directory, "package.json"))) start.ArgumentList.Add("--yarn-workspaces");
            start.ArgumentList.Add("--no-progress-bar");
        }
        else if (indexer == Python)
        {
            start.ArgumentList.Add(".");
            start.ArgumentList.Add("--project-name");
            start.ArgumentList.Add(project.Name);
            // 버전은 심볼 키에 들어가지 않는다. 비워 두면 scip-python 이 git 에서 찾다가, git 밖에서는 죽는다.
            start.ArgumentList.Add("--project-version");
            start.ArgumentList.Add("0");
            PythonIndexing.UseEnvironment(start, directory);
        }

        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(outputPath);
        return start;
    }

    private static bool DeclaresWorkspaces(string packageJson)
    {
        if (!File.Exists(packageJson)) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(packageJson));
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("workspaces", out _);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 프로젝트 하나를 색인한다. 실패하면 색인기가 뱉은 말을 그대로 돌려준다.
    /// 색인기가 깔려 있지 않으면 설치 명령을 알려 준다 — 「파일을 찾을 수 없습니다」 로는
    /// 무엇을 해야 할지 모른다.
    /// </summary>
    /// <param name="line">색인기가 한 줄 뱉을 때마다 부른다 — 화면이 「지금 어디쯤」 을 보여 줄 수 있게.</param>
    public static (bool Ok, string Output, TimeSpan Elapsed) Index(
        AffectedProject project, string outputPath, bool restore = false, Action<string>? line = null)
    {
        var watch = Stopwatch.StartNew();
        var start = CommandFor(project, outputPath, restore);

        // 명령을 만드는 데서는 아무것도 돌리지 않는다. 패키지 목록은 돌릴 때 뽑는다.
        if (project.Indexer == Python)
        {
            PythonIndexing.PinPackages(start, PythonIndexing.PythonFor(project.ProjectPath), outputPath + ".packages.json");
        }

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            return (false, $"{project.Indexer.Name} is not installed. {project.Indexer.Install}", watch.Elapsed);
        }

        if (process is null) return (false, $"could not start {project.Indexer.Name}", watch.Elapsed);

        using (process)
        {
            // 둘을 따로, 줄이 나오는 대로 읽는다. 하나를 끝까지 읽는 동안 다른 쪽 버퍼가 차면
            // 색인기가 멈추고, 끝에 한꺼번에 읽으면 몇 분 동안 아무 말도 전할 수 없다.
            var output = new System.Text.StringBuilder();
            var error = new System.Text.StringBuilder();
            var gate = new object();
            void Take(System.Text.StringBuilder into, string? text)
            {
                if (text is null) return;
                lock (gate) into.AppendLine(text);
                if (line is not null && text.Trim().Length > 0) line(text);
            }
            process.OutputDataReceived += (_, e) => Take(output, e.Data);
            process.ErrorDataReceived += (_, e) => Take(error, e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            watch.Stop();
            lock (gate) return (process.ExitCode == 0, output.ToString() + error, watch.Elapsed);
        }
    }

    /// <summary>
    /// 지금 HEAD 의 커밋. 스냅샷에 적어 두면 다음 주기가 «어디서부터» 를 스스로 안다 —
    /// 주기 갱신이 자기 상태를 따로 들고 있지 않아도 되는 이유다.
    /// </summary>
    public static string? HeadSha(string repo)
    {
        var sha = Git(repo, "rev-parse HEAD").Trim();
        return sha.Length == 40 ? sha : null;
    }

    private static string Git(string repo, string arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // git writes paths as UTF-8. Read with the console's code page (949 on Korean
            // Windows), a Korean file name would come back as something else.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };

        foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("git 을 실행하지 못했습니다.");

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0 ? output : string.Empty;
    }
}

/// <summary>
/// 실행 파일을 PATH 에서 찾는다. Windows 에서 npm 이 까는 색인기는 <c>.cmd</c> 껍데기라,
/// 이름만 주면 <see cref="Process.Start(ProcessStartInfo)"/> 가 못 찾는다 — .exe 만 붙여 보기 때문이다.
/// </summary>
public static class Executables
{
    public static string Find(string name)
    {
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar)) return name;

        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : new[] { string.Empty };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), name + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return name;
    }
}
