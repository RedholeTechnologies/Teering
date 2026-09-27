using System.Text.Json;
using Microsoft.Data.Sqlite;
using Treering.Cli;
using Treering.Core;

namespace Treering.Cli.Tests;

/// <summary>
/// Which imported project an MCP tool answers from. An agent asks about one repository and acts on
/// the answer; if the choice goes wrong, it gets another repository's code back with nothing to say
/// so. When the choice fails, the tool must say what to do instead of failing the call.
/// </summary>
[Collection("TREERING_HOME")]
public sealed class McpProjectChoiceTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"tr-home-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("TREERING_HOME");

    public McpProjectChoiceTests()
    {
        Environment.SetEnvironmentVariable("TREERING_HOME", _home);
        McpTools.Use(null);
    }

    public void Dispose()
    {
        McpTools.Use(null);
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable("TREERING_HOME", _previousHome);
        try { Directory.Delete(_home, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    /// <summary>
    /// A project whose graph holds one type, named so a search tells the projects apart. The import
    /// time is written out, so which one is newest never depends on the clock.
    /// </summary>
    private ProjectInfo Imported(string id, string name, string onlyType, DateTimeOffset at)
    {
        var folder = Path.Combine(_home, "projects", id);
        Directory.CreateDirectory(folder);
        var info = new ProjectInfo(id, name, Path.Combine(_home, "repos", name), Path.Combine(folder, "graph.db"), at, ["C#"]);

        using (var db = GraphDb.Open(info.Db))
        using (var command = db.CreateCommand())
        {
            command.CommandText = "INSERT INTO symbol(key, kind, display) VALUES ($key, 3, $name)";
            command.Parameters.AddWithValue("$key", $"scip-dotnet nuget {name} 1.0 App/{onlyType}#");
            command.Parameters.AddWithValue("$name", onlyType);
            command.ExecuteNonQuery();
        }

        File.WriteAllText(Path.Combine(folder, "project.json"), JsonSerializer.Serialize(info));
        return info;
    }

    private static readonly DateTimeOffset Earlier = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Earlier.AddDays(1);

    private static IReadOnlyList<string> Found(string answer, string name = "Only")
    {
        using var json = JsonDocument.Parse(answer);
        return json.RootElement.GetProperty("symbols").EnumerateArray()
            .Select(symbol => symbol.GetProperty("name").GetString()!)
            .Where(found => found.StartsWith(name, StringComparison.Ordinal))
            .ToList();
    }

    private static string? Error(string answer)
    {
        using var json = JsonDocument.Parse(answer);
        return json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    [Fact]
    public void No_project_named_means_the_most_recently_imported()
    {
        Imported("shop-1111", "Shop", "OnlyInShop", Later);
        Imported("billing-2222", "Billing", "OnlyInBilling", Earlier);

        Assert.Equal(["OnlyInShop"], Found(McpTools.FindSymbol("Only")));
    }

    [Fact]
    public void A_project_can_be_named_by_its_id()
    {
        Imported("shop-1111", "Shop", "OnlyInShop", Later);
        Imported("billing-2222", "Billing", "OnlyInBilling", Earlier);

        Assert.Equal(["OnlyInBilling"], Found(McpTools.FindSymbol("Only", project: "billing-2222")));
    }

    [Fact]
    public void A_project_can_be_named_by_its_name_in_any_case()
    {
        Imported("shop-1111", "Shop", "OnlyInShop", Later);
        Imported("billing-2222", "Billing", "OnlyInBilling", Earlier);

        Assert.Equal(["OnlyInBilling"], Found(McpTools.FindSymbol("Only", project: "billing")));
    }

    [Fact]
    public void An_id_wins_over_another_projects_name()
    {
        // One project's name is another's id. The id is the exact handle list_projects gives out.
        Imported("shop", "Billing", "OnlyInIdShop", Later);
        Imported("billing-2222", "Shop", "OnlyInNamedShop", Earlier);

        Assert.Equal(["OnlyInIdShop"], Found(McpTools.FindSymbol("Only", project: "shop")));
    }

    [Fact]
    public void An_unknown_project_is_an_answer_that_points_to_list_projects()
    {
        Imported("shop-1111", "Shop", "OnlyInShop", Later);

        var error = Error(McpTools.FindSymbol("Only", project: "Invoice"));

        Assert.NotNull(error);
        Assert.Contains("'Invoice'", error);
        Assert.Contains("list_projects", error);
    }

    [Fact]
    public void With_nothing_imported_the_answer_says_how_to_import()
    {
        var error = Error(McpTools.FindSymbol("Only"));

        Assert.NotNull(error);
        Assert.Contains("treering sample", error);
    }

    [Fact]
    public void Started_on_one_database_every_tool_reads_that_one()
    {
        Imported("shop-1111", "Shop", "OnlyInShop", Later);
        var billing = Imported("billing-2222", "Billing", "OnlyInBilling", Earlier);

        McpTools.Use(billing.Db);

        Assert.Equal(["OnlyInBilling"], Found(McpTools.FindSymbol("Only")));
    }

    [Fact]
    public void List_projects_puts_the_most_recent_first()
    {
        Imported("billing-2222", "Billing", "OnlyInBilling", Earlier);
        Imported("shop-1111", "Shop", "OnlyInShop", Later);

        using var json = JsonDocument.Parse(McpTools.ListProjects());
        var ids = json.RootElement.EnumerateArray().Select(project => project.GetProperty("id").GetString()).ToList();

        Assert.Equal(["shop-1111", "billing-2222"], ids);
    }
}
