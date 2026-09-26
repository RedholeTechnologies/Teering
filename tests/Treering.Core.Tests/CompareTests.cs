using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// The map's Compare: between two snapshots, which lines are new, which went, and which were
/// there all along. If this is wrong, the page paints a dependency as new that has been there
/// for months, or never shows the one that just went - and a review of "what changed this week"
/// starts from a false picture.
/// </summary>
public sealed class CompareTests : IDisposable
{
    private readonly string _scipPath = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}.scip");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _scipPath, _dbPath })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static Occurrence At(string type, int line, bool definition)
    {
        var occurrence = new Occurrence
        {
            Symbol = $"scip-dotnet nuget Shop 1.0.0.0 Shop/{type}#",
            SymbolRoles = definition ? (int)SymbolRole.Definition : 0,
        };
#pragma warning disable CS0612, CS0618
        occurrence.Range.Add(line);
        occurrence.Range.Add(0);
        occurrence.Range.Add(3);
#pragma warning restore CS0612, CS0618
        return occurrence;
    }

    private void Snapshot(int ord, Dictionary<string, string[]> uses)
    {
        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///shop" } };
        foreach (var (type, targets) in uses)
        {
            var document = new Document { RelativePath = type + ".cs", Language = "C#" };
            document.Occurrences.Add(At(type, 0, definition: true));
            var line = 1;
            foreach (var target in targets) document.Occurrences.Add(At(target, line++, definition: false));
            index.Documents.Add(document);
        }

        using (var stream = File.Create(_scipPath)) index.WriteTo(stream);
        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord, indexer: "test");
    }

    /// <summary>Last week Page used Repo and Money; this week it uses Repo and Invoice.</summary>
    private SqliteConnection TwoWeeks()
    {
        Snapshot(0, new() { ["Page"] = ["Repo", "Money"], ["Repo"] = [], ["Money"] = [], ["Invoice"] = [] });
        Snapshot(1, new() { ["Page"] = ["Repo", "Invoice"], ["Repo"] = [], ["Money"] = [], ["Invoice"] = [] });
        return GraphDb.Open(_dbPath);
    }

    private static Dictionary<string, EdgeState> States(SubgraphResult result)
    {
        var name = result.Nodes.ToDictionary(node => node.Id, node => node.Display);
        return result.Edges
            .Where(edge => edge.Kind == EdgeKind.Reference)
            .ToDictionary(edge => $"{name[edge.From]}->{name[edge.To]}", edge => edge.State);
    }

    private static SubgraphResult Map(SqliteConnection db, int? compareFrom) => Subgraph.Extract(db, new SubgraphRequest
    {
        Seeds = Subgraph.AllAt(db, Granularity.Type),
        Granularity = Granularity.Type,
        WholeLevel = true,
        AtOrd = 1,
        CompareFrom = compareFrom,
    });

    [Fact]
    public void A_line_that_appeared_since_the_earlier_snapshot_is_marked_new()
    {
        using var db = TwoWeeks();
        Assert.Equal(EdgeState.Born, States(Map(db, compareFrom: 0))["Page->Invoice"]);
    }

    [Fact]
    public void A_line_that_went_since_the_earlier_snapshot_is_shown_and_marked_gone()
    {
        // Normally a line that no longer exists is not drawn. When comparing it must be,
        // or "what came apart" could never be seen.
        using var db = TwoWeeks();
        Assert.Equal(EdgeState.Died, States(Map(db, compareFrom: 0))["Page->Money"]);
    }

    [Fact]
    public void A_line_that_was_there_both_times_is_neither_new_nor_gone()
    {
        using var db = TwoWeeks();
        Assert.Equal(EdgeState.Same, States(Map(db, compareFrom: 0))["Page->Repo"]);
    }

    [Fact]
    public void Without_a_comparison_a_line_that_went_is_not_drawn_and_nothing_is_marked()
    {
        using var db = TwoWeeks();
        var states = States(Map(db, compareFrom: null));

        Assert.False(states.ContainsKey("Page->Money"));
        Assert.All(states.Values, state => Assert.Equal(EdgeState.Same, state));
    }

    private static HashSet<string> Drawn(SqliteConnection db, int at, int? compareFrom = null) =>
        Subgraph.Extract(db, new SubgraphRequest
        {
            // Whoever picks the seeds does not know the time: they hand over everything that ever was.
            Seeds = Subgraph.AllAt(db, Granularity.Type),
            Granularity = Granularity.Type,
            WholeLevel = true,
            AtOrd = at,
            CompareFrom = compareFrom,
        }).Nodes.Select(node => node.Display).ToHashSet();

    /// <summary>Last week there was Money; this week its file is gone.</summary>
    private SqliteConnection MoneyGone()
    {
        Snapshot(0, new() { ["Page"] = ["Repo", "Money"], ["Repo"] = [], ["Money"] = [] });
        Snapshot(1, new() { ["Page"] = ["Repo"], ["Repo"] = [] });
        return GraphDb.Open(_dbPath);
    }

    [Fact]
    public void The_map_leaves_out_what_is_gone()
    {
        using var db = MoneyGone();

        Assert.DoesNotContain("Money", Drawn(db, at: 1));
        Assert.Contains("Page", Drawn(db, at: 1));
    }

    [Fact]
    public void The_map_of_an_earlier_snapshot_still_shows_what_was_there_then()
    {
        using var db = MoneyGone();

        Assert.Contains("Money", Drawn(db, at: 0));
    }

    [Fact]
    public void Comparing_shows_what_went_so_its_gone_lines_have_both_ends()
    {
        using var db = MoneyGone();

        Assert.Contains("Money", Drawn(db, at: 1, compareFrom: 0));
    }

    [Fact]
    public void The_map_of_an_earlier_snapshot_leaves_out_what_came_later()
    {
        Snapshot(0, new() { ["Page"] = ["Repo"], ["Repo"] = [] });
        Snapshot(1, new() { ["Page"] = ["Repo", "Invoice"], ["Repo"] = [], ["Invoice"] = [] });
        using var db = GraphDb.Open(_dbPath);

        Assert.DoesNotContain("Invoice", Drawn(db, at: 0));
        Assert.Contains("Invoice", Drawn(db, at: 1));
    }

    [Fact]
    public void Looking_inward_finds_who_uses_a_thing_and_not_what_it_uses()
    {
        Snapshot(0, new() { ["Page"] = ["Repo"], ["Repo"] = ["Money"], ["Money"] = [] });
        using var db = GraphDb.Open(_dbPath);
        var repo = Subgraph.AllAt(db, Granularity.Type)
            .Single(id => Subgraph.Extract(db, new SubgraphRequest { Seeds = [id], Depth = 0 }).Nodes.Single().Display == "Repo");

        var inward = Subgraph.Extract(db, new SubgraphRequest { Seeds = [repo], Direction = Direction.In, Depth = 1 });

        var names = inward.Nodes.Select(node => node.Display).ToHashSet();
        Assert.Contains("Page", names);
        Assert.DoesNotContain("Money", names);
    }
}
