using Scip;

namespace Treering.Core;

/// <summary>
/// scip-python 은 같은 폴더의 모듈을 두 가지로 부른다. <c>src/</c> 아래 패키지를 편집 모드로
/// 깔아 두면, 어디선가 import 되는 파일은 import 이름(<c>shop.orders</c>)으로,
/// 아무도 import 하지 않는 파일은 파일 경로(<c>src.shop.cli</c>)로 부른다.
/// 그대로 두면 한 폴더가 지도에 두 갈래로 갈라지고, 파일 하나가 처음 import 되는 순간
/// 그 안의 것이 전부 죽고 다른 이름으로 다시 태어난다.
///
/// 그래서 <b>파일 경로를 기준으로</b> 이름을 맞춘다 — 탐색기에서 보는 폴더와 같은 모양이다.
/// 색인 안의 정의가 증거다: 파일 <c>src/shop/orders.py</c> 가 모듈
/// <c>shop.orders</c> 를 정의하면 «<c>shop</c> 는 <c>src.</c> 아래» 를 배운다.
/// 최상위에 진짜 <c>shop/</c> 폴더가 따로 있거나 증거가 엇갈리면 배우지 않는다.
///
/// 하나 더. scip-python 은 <b>타입으로 닿은 라이브러리 클래스</b>를 우리 패키지 이름으로 적는다 —
/// 클래스의 패키지를 정할 때 그 클래스를 정의한 파일이 아니라 참조한 파일(우리 코드)을 보기 때문이다
/// (색인기 주석이 스스로 «This isn't correct» 라고 적어 두었다). 그러면 라이브러리의 모듈이 지도에서
/// 우리 프로젝트 안에 앉는다. 같은 색인에서 그 라이브러리의 다른 심볼은 제 패키지 이름을 달고 오므로,
/// 그것으로 «이 모듈은 어느 라이브러리 것» 을 배워 돌려놓는다. 이 색인이 정의한 모듈은 건드리지 않고,
/// 두 라이브러리가 겹치는 이름이면 가장 긴 접두어가 하나로 정해질 때만 옮긴다.
///
/// 마지막으로, scip-python 은 라이브러리 안의 <b>상대 import</b>(<c>from .windows_events import *</c>)를
/// 무조건 우리 것으로 보고, 모듈 이름의 앞부분까지 잃는다 — <c>asyncio.windows_events</c> 가 우리 패키지의
/// <c>windows_events</c> 가 된다. 어느 라이브러리도 그 이름을 갖지 않으면, 설치 목록과 표준 라이브러리 타입
/// 정의에서 끝부분이 그 이름과 맞는 모듈이 <b>딱 하나</b>일 때 그 모듈로 옮긴다.
/// </summary>
public sealed class PythonModuleNames
{
    private const string Scheme = "scip-python ";

    /// <summary>
    /// (패키지, import 이름의 첫 조각) → 그 앞에 붙일 경로(<c>shop</c> → <c>src.</c>).
    /// 패키지로 가르는 것은, 표준 라이브러리 이름을 가린 우리 파일이 진짜 표준 라이브러리까지
    /// 끌고 가지 않게 하려는 것이다.
    /// </summary>
    private readonly Dictionary<string, string> _roots;

    /// <summary>이 색인이 정의를 둔 패키지 — 우리 것.</summary>
    private readonly HashSet<string> _ours;

    /// <summary>우리 모듈과 폴더의 첫 조각. 이 이름으로 시작하는 모듈은 라이브러리로 옮기지 않는다.</summary>
    private readonly HashSet<string> _protected;

    /// <summary>모듈 이름(과 그 접두어) → 그것을 가진 라이브러리의 «패키지 버전». 둘 이상이면 null.</summary>
    private readonly Dictionary<string, string?> _homes;

    /// <summary>모듈 이름의 끝부분(<c>windows_events</c>, <c>core.frame</c> …) → 그렇게 끝나는 단 하나의 모듈과 그 집. 둘 이상이면 null.</summary>
    private readonly Dictionary<string, (string Module, string Home)?> _endings;

    private PythonModuleNames(
        Dictionary<string, string> roots, HashSet<string> ours, HashSet<string> @protected, Dictionary<string, string?> homes,
        Dictionary<string, (string Module, string Home)?> endings)
    {
        _roots = roots;
        _ours = ours;
        _protected = @protected;
        _homes = homes;
        _endings = endings;
    }

    public static readonly PythonModuleNames None = new([], [], [], [], []);

    /// <param name="installed">
    /// 설치된 패키지와 그 파일 목록(<see cref="PythonIndexing.PinPackages"/> 가 색인기에 건넨 것).
    /// 색인 안에 제 이름으로 한 번도 나오지 않는 라이브러리도 이것으로 집을 안다.
    /// </param>
    /// <param name="stdlib">표준 라이브러리 타입 정의의 모듈 이름들(<see cref="PythonIndexing.StdlibModules()"/>).</param>
    public static PythonModuleNames Learn(
        IEnumerable<Document> documents,
        IEnumerable<(string Name, string Version, IEnumerable<string> Files)>? installed = null,
        IEnumerable<string>? stdlib = null)
    {
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        var topFolders = new HashSet<string>(StringComparer.Ordinal);
        var ours = new HashSet<string>(StringComparer.Ordinal);
        var definedTops = new HashSet<string>(StringComparer.Ordinal);
        var references = new HashSet<(string Package, string Version, string Module)>();

        foreach (var document in documents)
        {
            if (DottedPath(document.RelativePath) is not { } dotted) continue;
            topFolders.Add(dotted.Split('.')[0]);

            foreach (var occurrence in document.Occurrences)
            {
                if (!TrySplit(occurrence.Symbol, out var head, out var module, out _)) continue;
                if ((occurrence.SymbolRoles & (int)SymbolRole.Definition) == 0)
                {
                    references.Add((PackageOf(head), VersionOf(head), module));
                    continue;
                }

                ours.Add(PackageOf(head));
                definedTops.Add(module.Split('.')[0]);
                if (module == dotted || !dotted.EndsWith("." + module, StringComparison.Ordinal)) continue;

                var key = PackageOf(head) + " " + module.Split('.')[0];
                var root = dotted[..^module.Length];
                if (roots.TryGetValue(key, out var known) && known != root) ambiguous.Add(key);
                roots[key] = root;
            }
        }

        foreach (var key in roots.Keys.ToList())
        {
            if (ambiguous.Contains(key) || topFolders.Contains(key[(key.LastIndexOf(' ') + 1)..])) roots.Remove(key);
        }

        // 라이브러리의 집. 모듈 이름과 그 접두어마다 누가 가졌나를 센다 — 색인 안의 증거와 설치 목록 둘 다.
        var homes = new Dictionary<string, string?>(StringComparer.Ordinal);
        void Claim(string package, string version, string module)
        {
            if (ours.Contains(package)) return;
            var home = package + " " + version;
            foreach (var prefix in Prefixes(module))
            {
                if (!homes.TryGetValue(prefix, out var known)) homes[prefix] = home;
                else if (known is not null && PackageOfHome(known) != package) homes[prefix] = null;
            }
        }

        // 이름을 잃은 모듈을 찾을 목록. 끝부분마다 그렇게 끝나는 모듈이 하나뿐인지 센다.
        var endings = new Dictionary<string, (string Module, string Home)?>(StringComparer.Ordinal);
        void Ending(string module, string home)
        {
            var parts = module.Split('.');
            for (var i = 0; i < parts.Length; i++)
            {
                var ending = string.Join('.', parts[i..]);
                if (!endings.TryGetValue(ending, out var known)) endings[ending] = (module, home);
                else if (known is { } found && found.Module != module) endings[ending] = null;
            }
        }

        foreach (var (package, version, module) in references) Claim(package, version, module);
        foreach (var (name, version, files) in installed ?? [])
        {
            if (ours.Contains(name)) continue;
            foreach (var file in files)
            {
                if (DottedPath(file) is not { } module) continue;
                Claim(name, version, module);
                Ending(module, name + " " + version);
            }
        }

        // 표준 라이브러리는 버전을 색인 안의 증거에서 얻는다. 증거가 없으면 이름만으로 옮기지 않는다.
        if (references.FirstOrDefault(reference => reference.Package == StdlibPackage) is { Version.Length: > 0 } seen)
        {
            foreach (var module in stdlib ?? []) Ending(module, StdlibPackage + " " + seen.Version);
        }

        definedTops.UnionWith(topFolders);
        return new PythonModuleNames(roots, ours, definedTops, homes, endings);
    }

    private const string StdlibPackage = "python-stdlib";

    /// <summary>경로 기준 이름으로, 라이브러리 것은 그 라이브러리 이름으로 바꾼 심볼. 해당하지 않으면 그대로.</summary>
    public string Canonical(string symbol) => Rehome(Renamed(symbol));

    private string Renamed(string symbol)
    {
        if (_roots.Count == 0 || !TrySplit(symbol, out var head, out var module, out var rest)) return symbol;

        var dot = module.IndexOf('.');
        var top = dot < 0 ? module : module[..dot];
        if (!_roots.TryGetValue(PackageOf(head) + " " + top, out var root)) return symbol;

        return head + SymbolParser.EscapeName(root + module) + rest;
    }

    private string Rehome(string symbol)
    {
        if (_homes.Count == 0 && _endings.Count == 0) return symbol;
        if (!TrySplit(symbol, out var head, out var module, out var rest)) return symbol;
        if (!_ours.Contains(PackageOf(head)) || _protected.Contains(module.Split('.')[0])) return symbol;

        var parts = head.Split(' ');
        foreach (var prefix in Prefixes(module).Reverse())
        {
            if (!_homes.TryGetValue(prefix, out var home)) continue;
            if (home is null) return symbol;   // 두 라이브러리가 겹친다 — 어느 쪽인지 모른다
            return $"{parts[0]} {parts[1]} {home} " + SymbolParser.EscapeName(module) + rest;
        }

        // 어느 라이브러리도 이 이름을 갖지 않는다 — 상대 import 로 앞부분을 잃은 이름일 수 있다.
        if (_endings.TryGetValue(module, out var match) && match is { } found)
        {
            return $"{parts[0]} {parts[1]} {found.Home} " + SymbolParser.EscapeName(found.Module) + rest;
        }

        return symbol;
    }

    /// <summary><c>a.b.c</c> → <c>a</c>, <c>a.b</c>, <c>a.b.c</c>. 짧은 것부터.</summary>
    private static IEnumerable<string> Prefixes(string module)
    {
        for (var dot = module.IndexOf('.'); dot > 0; dot = module.IndexOf('.', dot + 1)) yield return module[..dot];
        yield return module;
    }

    private static string PackageOf(string head) => head.Split(' ')[2];

    private static string VersionOf(string head) => head.Split(' ')[3];

    private static string PackageOfHome(string home) => home[..home.LastIndexOf(' ')];

    /// <summary><c>src\shop\orders.py</c> → <c>src.shop.orders</c>. <c>__init__</c> 은 폴더 이름이다.</summary>
    private static string? DottedPath(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        string stem;
        if (path.EndsWith(".py", StringComparison.Ordinal)) stem = path[..^3];
        else if (path.EndsWith(".pyi", StringComparison.Ordinal)) stem = path[..^4];
        else return null;

        if (stem == "__init__") return null;
        if (stem.EndsWith("/__init__", StringComparison.Ordinal)) stem = stem[..^"/__init__".Length];
        return stem.Replace('/', '.');
    }

    /// <summary>
    /// scip-python 심볼을 «앞 네 칸», 첫 descriptor 의 모듈 이름, 나머지로 가른다.
    /// 점이 든 모듈 이름은 백틱으로 싸여 있고, 안의 백틱은 두 번 쓴다.
    /// </summary>
    private static bool TrySplit(string symbol, out string head, out string module, out string rest)
    {
        head = module = rest = string.Empty;
        if (!symbol.StartsWith(Scheme, StringComparison.Ordinal)) return false;

        var parts = symbol.Split(' ', 5);
        if (parts.Length < 5) return false;

        head = symbol[..^parts[4].Length];
        var descriptors = parts[4];

        int end;
        if (descriptors.StartsWith('`'))
        {
            var name = new System.Text.StringBuilder();
            var i = 1;
            while (true)
            {
                if (i >= descriptors.Length) return false;
                if (descriptors[i] == '`')
                {
                    if (i + 1 < descriptors.Length && descriptors[i + 1] == '`') { name.Append('`'); i += 2; continue; }
                    break;
                }

                name.Append(descriptors[i++]);
            }

            module = name.ToString();
            end = i + 1;
        }
        else
        {
            end = descriptors.IndexOf('/');
            if (end <= 0) return false;
            module = descriptors[..end];
        }

        if (end >= descriptors.Length || descriptors[end] != '/') return false;
        rest = descriptors[end..];
        return true;
    }
}
