using System.Text.Json;
using System.Text.RegularExpressions;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// Where imported projects are kept and how they are named. The id is the folder a project's data
/// lives in: if the same repository could get two ids, it would be imported twice and its history
/// split; if one bad record could break the list, the page and the watcher would lose every project.
/// </summary>
[Collection("TREERING_HOME")]
public sealed partial class ProjectsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"tr-home-{Guid.NewGuid():N}");
    private readonly string _repos = Path.Combine(Path.GetTempPath(), $"tr-repos-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("TREERING_HOME");

    public ProjectsTests()
    {
        Environment.SetEnvironmentVariable("TREERING_HOME", _home);
        Directory.CreateDirectory(_repos);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TREERING_HOME", _previousHome);
        foreach (var folder in new[] { _home, _repos })
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* a temp folder left behind is harmless */ }
        }
    }

    private string Repo(string name)
    {
        var path = Path.Combine(_repos, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Records the repo as imported and gives it a database file, as an import does.</summary>
    private ProjectInfo Imported(string name)
    {
        var info = Projects.Record(Repo(name), ["C#"]);
        File.WriteAllText(info.Db, string.Empty);
        return info;
    }

    [Fact]
    public void A_broken_record_does_not_hide_the_other_projects()
    {
        Imported("shop");
        var broken = Path.Combine(_home, "projects", "broken-00000000");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "project.json"), "{ \"Id\": ");

        Assert.Equal(["shop"], Projects.List().Select(project => project.Name));
    }

    [Fact]
    public void A_project_whose_database_is_gone_is_not_listed()
    {
        var info = Imported("shop");
        File.Delete(info.Db);

        Assert.Empty(Projects.List());
    }

    [Fact]
    public void The_most_recent_import_comes_first()
    {
        Imported("older");
        var newer = Imported("newer");
        var olderFile = Path.Combine(_home, "projects", Projects.IdFor(Path.Combine(_repos, "older")), "project.json");
        var older = JsonSerializer.Deserialize<ProjectInfo>(File.ReadAllText(olderFile))!;
        File.WriteAllText(olderFile, JsonSerializer.Serialize(older with { ImportedAt = newer.ImportedAt.AddDays(-1) }));

        Assert.Equal(["newer", "older"], Projects.List().Select(project => project.Name));
    }

    [Fact]
    public void Nothing_imported_yet_is_an_empty_list()
    {
        Assert.Empty(Projects.List());
    }

    [Fact]
    public void A_trailing_separator_does_not_make_another_project()
    {
        var repo = Repo("Shop");

        Assert.Equal(Projects.IdFor(repo), Projects.IdFor(repo + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Two_repositories_with_the_same_folder_name_are_two_projects()
    {
        var one = Path.Combine(_repos, "a", "shop");
        var two = Path.Combine(_repos, "b", "shop");

        Assert.NotEqual(Projects.IdFor(one), Projects.IdFor(two));
        Assert.StartsWith("shop-", Projects.IdFor(one));
    }

    [Fact]
    public void An_id_is_a_safe_folder_name_that_still_reads_as_the_repository()
    {
        Assert.Matches(SafeId(), Projects.IdFor(Path.Combine(_repos, "my shop (old)")));
        Assert.StartsWith("my-shop--old--", Projects.IdFor(Path.Combine(_repos, "my shop (old)")));
    }

    [Fact]
    public void A_repository_at_the_root_of_a_drive_still_gets_a_name()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Matches(RootId(), Projects.IdFor(root));
    }

    [Fact]
    public void A_database_lives_in_Treerings_home_never_in_the_repository()
    {
        var repo = Repo("shop");

        var db = Projects.DbFor(repo);

        Assert.StartsWith(Path.Combine(_home, "projects"), db);
        Assert.False(db.StartsWith(repo, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Projects.Record(repo, []).Db, db);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+-[0-9a-f]{8}$")]
    private static partial Regex SafeId();

    [GeneratedRegex("^repo-[0-9a-f]{8}$")]
    private static partial Regex RootId();
}
