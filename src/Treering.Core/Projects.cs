using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Treering.Core;

/// <summary>들여온 프로젝트 하나.</summary>
public sealed record ProjectInfo(
    string Id,
    string Name,
    string Repo,
    string Db,
    DateTimeOffset ImportedAt,
    IReadOnlyList<string> Languages);

/// <summary>
/// 들여온 프로젝트를 한곳에 모은다 — 사용자 폴더 아래 <c>Treering/projects/&lt;id&gt;/</c>.
///
/// 리포 안에 DB 를 만들지 않는다. 남의 리포에 파일을 흘리면 커밋에 섞이거나 .gitignore 를
/// 고쳐야 한다. 같은 리포는 언제나 같은 id 가 되도록 경로에서 id 를 만든다.
/// <c>TREERING_HOME</c> 으로 자리를 바꿀 수 있다(시험이 쓴다).
/// </summary>
public static class Projects
{
    public static string Home =>
        Environment.GetEnvironmentVariable("TREERING_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Treering");

    private static string Root => Path.Combine(Home, "projects");

    /// <summary>리포 경로로 정해지는 id. 폴더 이름에 경로의 짧은 지문을 붙인다 — 이름이 같은 두 리포를 가른다.</summary>
    public static string IdFor(string repo)
    {
        var full = Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var key = OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
        var name = new string(Path.GetFileName(full).Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-').ToArray());
        return $"{(name.Length > 0 ? name : "repo")}-{hash}";
    }

    /// <summary>이 리포의 DB 자리. 들여온 적이 없어도 자리는 정해진다.</summary>
    public static string DbFor(string repo) => Path.Combine(Root, IdFor(repo), "graph.db");

    /// <summary>들여온 뒤에 적어 둔다. 목록과 화면이 이것을 읽는다.</summary>
    public static ProjectInfo Record(string repo, IEnumerable<string> languages)
    {
        var id = IdFor(repo);
        var folder = Path.Combine(Root, id);
        Directory.CreateDirectory(folder);

        var info = new ProjectInfo(
            id,
            Path.GetFileName(Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Path.GetFullPath(repo),
            Path.Combine(folder, "graph.db"),
            DateTimeOffset.UtcNow,
            languages.Distinct().ToList());

        File.WriteAllText(Path.Combine(folder, "project.json"), JsonSerializer.Serialize(info));
        return info;
    }

    /// <summary>들여온 것들, 최근 것부터. DB 가 지워진 것은 빼고 본다.</summary>
    public static List<ProjectInfo> List()
    {
        if (!Directory.Exists(Root)) return [];

        var found = new List<ProjectInfo>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            var file = Path.Combine(folder, "project.json");
            if (!File.Exists(file)) continue;
            try
            {
                var info = JsonSerializer.Deserialize<ProjectInfo>(File.ReadAllText(file));
                if (info is not null && File.Exists(info.Db)) found.Add(info);
            }
            catch (JsonException) { /* a broken record is not a project */ }
        }

        return found.OrderByDescending(info => info.ImportedAt).ToList();
    }

    /// <summary>
    /// id 는 폴더 이름이라 폴더처럼 비교한다. Windows 에서는 대소문자가 달라도 같은 폴더이고,
    /// <see cref="IdFor"/> 의 앞부분은 적힌 대로의 이름이라 <c>D:\Shop</c> 과 <c>d:\shop</c> 이
    /// <c>Shop-…</c> · <c>shop-…</c> 로 갈린다 — 그대로 비교하면 들여온 프로젝트를 못 찾는다.
    /// </summary>
    public static ProjectInfo? Find(string id) => List().FirstOrDefault(info => string.Equals(info.Id, id, IdComparison));

    private static StringComparison IdComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>이 리포를 들여온 적이 있으면 그 기록.</summary>
    public static ProjectInfo? ForRepo(string repo) => Find(IdFor(repo));
}
