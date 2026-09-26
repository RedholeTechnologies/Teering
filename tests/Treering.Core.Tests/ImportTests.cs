using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 리포를 보고 무엇을 어떤 색인기로 돌릴지 정하는 것. 실행은 하지 않는다 — 여기서 틀리면
/// 몇 분을 쓰고 엉뚱한 것을 색인하거나, 아예 빠뜨린다.
/// </summary>
[Collection("TREERING_HOME")]
public sealed class ImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}");
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"tr-home-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("TREERING_HOME");

    public ImportTests() => Environment.SetEnvironmentVariable("TREERING_HOME", _home);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TREERING_HOME", _previousHome);
        foreach (var folder in new[] { _root, _home })
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* 지워지지 않아도 그만이다 */ }
        }
    }

    private void Make(string relative, string content = "")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private List<(string Indexer, string Name)> Plan() =>
        Import.Plan(_root).Select(step => (step.Indexer.Name, step.Name)).ToList();

    [Fact]
    public void A_solution_at_the_root_is_indexed_once_for_all_its_projects()
    {
        Make("Shop.slnx");
        Make("App/App.csproj");
        Make("App/Main.cs");
        Make("Core/Core.csproj");
        Make("Core/Money.cs");

        Assert.Equal([("scip-dotnet", "Shop.slnx")], Plan());
    }

    [Fact]
    public void Without_a_solution_each_project_is_indexed()
    {
        Make("App/App.csproj");
        Make("App/Main.cs");
        Make("Core/Core.csproj");
        Make("Core/Money.cs");

        Assert.Equal([("scip-dotnet", "App.csproj"), ("scip-dotnet", "Core.csproj")], Plan());
    }

    [Fact]
    public void The_same_solution_in_two_formats_is_indexed_once()
    {
        Make("Shop.sln");
        Make("Shop.slnx");
        Make("App/App.csproj");
        Make("App/Main.cs");

        Assert.Equal([("scip-dotnet", "Shop.slnx")], Plan());
    }

    [Fact]
    public void Only_the_topmost_package_json_counts_and_dependencies_are_not_looked_at()
    {
        Make("package.json", "{}");
        Make("src/app/page.tsx");
        Make("packages/ui/package.json", "{}");
        Make("packages/ui/button.ts");
        Make("node_modules/react/package.json", "{}");
        Make("node_modules/react/index.js");

        Assert.Equal([("scip-typescript", Path.GetFileName(_root))], Plan());
    }

    [Fact]
    public void Several_languages_in_one_repository_each_get_their_own_indexer()
    {
        Make("Server/Server.slnx");
        Make("Server/Api/Api.csproj");
        Make("Server/Api/Program.cs");
        Make("web/package.json", "{}");
        Make("web/src/index.ts");
        Make("ml/pyproject.toml");
        Make("ml/train.py");

        Assert.Equal(
            [("scip-dotnet", "Server.slnx"), ("scip-typescript", "web"), ("scip-python", "ml")],
            Plan());
    }

    [Fact]
    public void Python_without_a_project_file_is_indexed_from_the_root()
    {
        Make("tool.py");

        Assert.Equal([("scip-python", Path.GetFileName(_root))], Plan());
    }

    [Fact]
    public void A_language_with_no_source_is_left_out()
    {
        // A package.json that only holds scripts is not a TypeScript project.
        Make("package.json", "{}");
        Make("App/App.csproj");
        Make("App/Main.cs");

        Assert.Equal([("scip-dotnet", "App.csproj")], Plan());
    }

    [Fact]
    public void A_workspace_root_tells_the_indexer_so()
    {
        Make("package.json", """{ "workspaces": ["packages/*"] }""");
        Make("packages/ui/button.ts");

        var step = Assert.Single(Import.Plan(_root));
        var line = string.Join(' ', Incremental.CommandFor(new AffectedProject(step.Indexer, step.Target, []), "out.scip").ArgumentList);

        Assert.Contains("--yarn-workspaces", line);
    }

    [Fact]
    public void Python_always_gets_a_project_version()
    {
        // Without one scip-python asks git, and outside a repository it dies mid-index.
        Make("pyproject.toml");
        Make("worker/main.py");

        var step = Assert.Single(Import.Plan(_root));
        var arguments = Incremental.CommandFor(new AffectedProject(step.Indexer, step.Target, []), "out.scip").ArgumentList.ToList();

        Assert.Equal("0", arguments[arguments.IndexOf("--project-version") + 1]);
    }

    [Fact]
    public void Python_uses_the_repositorys_own_virtual_environment_for_pip()
    {
        Make("pyproject.toml");
        Make("worker/main.py");
        var scripts = OperatingSystem.IsWindows() ? ".venv/Scripts/pip.exe" : ".venv/bin/pip";
        Make(scripts);

        var step = Assert.Single(Import.Plan(_root));
        var start = Incremental.CommandFor(new AffectedProject(step.Indexer, step.Target, []), "out.scip");

        Assert.StartsWith(
            Path.GetDirectoryName(Path.Combine(_root, scripts.Replace('/', Path.DirectorySeparatorChar)))! + Path.PathSeparator,
            start.Environment["PATH"]);
        Assert.DoesNotContain("--environment", start.ArgumentList);
    }

    [Fact]
    public void A_virtual_environment_is_not_mistaken_for_source()
    {
        Make("pyproject.toml");
        Make("worker/main.py");
        Make(".venv/Lib/site-packages/requests/api.py");

        Assert.Equal([("scip-python", Path.GetFileName(_root))], Plan());
    }

    [Fact]
    public void A_stage_line_is_shown_without_its_clock_stamp()
    {
        // The page counts the time itself; the indexer's own stamp would only repeat it.
        Assert.True(Import.IsStamped("(15:10:31) Evaluating python environment dependencies"));
        Assert.Equal("Evaluating python environment dependencies", Import.PhaseOf("(15:10:31) Evaluating python environment dependencies"));
    }

    [Fact]
    public void Chatter_without_a_stamp_is_not_a_stage()
    {
        // "Python script failed" is scip-python falling back, not failing. Shown as the stage,
        // it would read as an error while nothing is wrong.
        Assert.False(Import.IsStamped("Python script failed with code null: undefined"));
        Assert.False(Import.IsStamped("(a note in brackets)"));
    }

    [Fact]
    public void A_very_long_stage_line_is_cut()
    {
        Assert.Equal(118, Import.PhaseOf(new string('x', 400)).Length);
    }

    [Fact]
    public void An_imported_repository_is_remembered_by_its_path()
    {
        Make("App/App.csproj");
        Make("App/Main.cs");

        var info = Projects.Record(_root, ["C#"]);
        File.WriteAllText(info.Db, "");

        Assert.Equal(Projects.IdFor(_root), info.Id);
        Assert.Equal(info.Id, Projects.ForRepo(_root)!.Id);
        Assert.Equal(Projects.IdFor(_root + Path.DirectorySeparatorChar), info.Id);
        Assert.Contains(Projects.List(), project => project.Id == info.Id);

        // Nothing is written into the repository itself.
        Assert.False(Directory.Exists(Path.Combine(_root, ".treering")));
        Assert.StartsWith(_home, info.Db);
    }

    [Fact]
    public void Two_repositories_with_the_same_folder_name_do_not_collide()
    {
        var a = Path.Combine(_root, "one", "shop");
        var b = Path.Combine(_root, "two", "shop");

        Assert.NotEqual(Projects.IdFor(a), Projects.IdFor(b));
        Assert.StartsWith("shop-", Projects.IdFor(a));
    }
}
