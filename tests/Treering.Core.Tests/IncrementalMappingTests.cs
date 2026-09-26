using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 「무엇이 바뀌었나」 에서 「무엇을 다시 색인해야 하나」 로 가는 길.
/// 여기서 프로젝트를 하나라도 놓치면 그래프가 조용히 틀려진다.
/// </summary>
public sealed class IncrementalMappingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}");

    public IncrementalMappingTests()
    {
        // 폴더 이름과 어셈블리 이름이 다른 실제 배치를 흉내 낸다.
        Make("App/App.csproj", "<Project><PropertyGroup><AssemblyName>상점앱</AssemblyName></PropertyGroup></Project>");
        Make("App/ViewModels/Shell.cs", "class Shell {}");
        Make("App/Data/Repo.cs", "class Repo {}");
        Make("Core/Core.csproj", "<Project />");
        Make("Core/Money.cs", "struct Money {}");
        Make("Docs/readme.md", "not code");

        // A web app beside it, with a workspace package of its own inside.
        Make("web/package.json", "{}");
        Make("web/tsconfig.json", "{}");
        Make("web/src/app/page.tsx", "export default function Page() {}");
        Make("web/packages/ui/package.json", "{}");
        Make("web/packages/ui/button.ts", "export const Button = 1;");

        // A Python service with no tsconfig, JavaScript only in its tools folder.
        Make("svc/pyproject.toml", "[project]");
        Make("svc/app/main.py", "def main(): pass");
        Make("tools/package.json", "{}");
        Make("tools/build.js", "module.exports = {};");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 지워지지 않아도 그만이다 */ }
    }

    private void Make(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Files_map_to_the_nearest_project_above_them()
    {
        var affected = Incremental.AffectedProjects(_root, ["App/ViewModels/Shell.cs", "Core/Money.cs"]);

        Assert.Equal(2, affected.Count);
        Assert.Contains(affected, project => Path.GetFileName(project.ProjectPath) == "App.csproj");
        Assert.Contains(affected, project => Path.GetFileName(project.ProjectPath) == "Core.csproj");
    }

    [Fact]
    public void Several_files_in_one_project_index_that_project_once()
    {
        var affected = Incremental.AffectedProjects(
            _root, ["App/ViewModels/Shell.cs", "App/Data/Repo.cs"]);

        var project = Assert.Single(affected);
        Assert.Equal("App.csproj", Path.GetFileName(project.ProjectPath));
        Assert.Equal(2, project.ChangedFiles.Count);
    }

    [Fact]
    public void Mapping_uses_the_project_file_not_the_folder_name()
    {
        // 폴더는 App, 어셈블리는 상점앱이다. 폴더 이름으로 이으면 여기서 틀린다.
        var project = Assert.Single(Incremental.AffectedProjects(_root, ["App/Data/Repo.cs"]));

        Assert.EndsWith("App.csproj", project.ProjectPath);
        Assert.Contains("AssemblyName", File.ReadAllText(project.ProjectPath));
    }

    [Fact]
    public void A_file_with_no_project_above_it_is_dropped_rather_than_guessed()
    {
        Make("Loose.cs", "class Loose {}");

        // 프로젝트에 속하지 않는 파일을 아무 프로젝트에나 붙이면 엉뚱한 것을 다시 색인한다.
        Assert.Empty(Incremental.AffectedProjects(_root, ["Loose.cs"]));
    }

    [Fact]
    public void Each_language_finds_its_own_kind_of_project()
    {
        var affected = Incremental.AffectedProjects(
            _root, ["App/Data/Repo.cs", "web/src/app/page.tsx", "svc/app/main.py", "Docs/readme.md"]);

        Assert.Equal(
            [
                ("scip-dotnet", "App.csproj"),
                ("scip-python", "svc"),
                ("scip-typescript", "web"),
            ],
            affected.Select(project => (project.Indexer.Name, project.Name)).ToList());
    }

    [Fact]
    public void A_python_repo_with_no_project_file_is_one_project_as_it_was_imported()
    {
        // Import takes such a repo whole. If updates did not, every change to it would be
        // "nothing changed", and its map would stay as it was on the day it was imported.
        var repo = Path.Combine(_root, "scripts-only");
        Make("scripts-only/main.py", "def main(): pass");
        Make("scripts-only/app/orders.py", "class Order: pass");

        var project = Assert.Single(Incremental.AffectedProjects(repo, ["app/orders.py"]));

        Assert.Equal(Incremental.Python, project.Indexer);
        Assert.Equal(Path.GetFullPath(repo), project.ProjectPath);
        Assert.Equal(Path.GetFullPath(repo), Assert.Single(Import.Plan(repo)).Target);
    }

    [Fact]
    public void A_python_file_outside_every_project_file_is_left_out_when_the_repo_has_some()
    {
        // The repo has svc/pyproject.toml, so import indexed only svc/. A script beside it was
        // never in the map; updating would drag the whole repo in as a second project.
        Make("loose.py", "print(1)");

        Assert.Empty(Incremental.AffectedProjects(_root, ["loose.py"]));
    }

    [Fact]
    public void The_nearest_package_wins_in_a_workspace()
    {
        var project = Assert.Single(Incremental.AffectedProjects(_root, ["web/packages/ui/button.ts"]));

        Assert.Equal("ui", project.Name);
    }

    [Fact]
    public void Files_no_indexer_reads_are_left_out_of_what_changed()
    {
        Assert.Null(Incremental.IndexerFor("Docs/readme.md"));
        Assert.Equal(Incremental.TypeScript, Incremental.IndexerFor("tools/build.js"));
        Assert.Equal(Incremental.CSharp, Incremental.IndexerFor("Core/Money.CS"));
    }

    [Fact]
    public void Each_indexer_is_asked_the_way_it_needs()
    {
        var output = Path.Combine(_root, "out.scip");
        string Line(string file, bool restore = false) =>
            string.Join(' ', Incremental.CommandFor(
                Assert.Single(Incremental.AffectedProjects(_root, [file])), output, restore).ArgumentList);

        // C#: the project file, symbols with their package, and restore skipped unless asked for.
        var csharp = Line("Core/Money.cs");
        Assert.StartsWith("index ", csharp);
        Assert.Contains("--allow-global-symbol-definitions", csharp);
        Assert.Contains("--skip-dotnet-restore", csharp);
        Assert.DoesNotContain("--skip-dotnet-restore", Line("Core/Money.cs", restore: true));

        // TypeScript: from the package folder; a tsconfig is inferred only when there is none.
        Assert.DoesNotContain("--infer-tsconfig", Line("web/src/app/page.tsx"));
        Assert.Contains("--infer-tsconfig", Line("tools/build.js"));

        // Python: a project name, which scip-python insists on.
        Assert.Contains("--project-name svc", Line("svc/app/main.py"));

        Assert.All(new[] { csharp, Line("web/src/app/page.tsx"), Line("svc/app/main.py") },
            line => Assert.EndsWith("--output " + output, line));
    }
}
