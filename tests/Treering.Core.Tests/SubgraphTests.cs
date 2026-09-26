using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// §0 원시연산의 계약을 못 박는다. 예산을 넘지 않는 것, 허브를 접는 것,
/// 그리고 잘린 자리를 <b>숨기지 않고 돌려주는 것</b>.
/// </summary>
public sealed class SubgraphTests : IDisposable
{
    private const string Prefix = "scip-dotnet nuget Demo 1.0.0.0 ";

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

    private static Occurrence At(string descriptors, int line, bool definition)
    {
        var occurrence = new Occurrence
        {
            Symbol = Prefix + descriptors,
            SymbolRoles = definition ? (int)SymbolRole.Definition : 0,
        };
#pragma warning disable CS0612, CS0618
        occurrence.Range.Add(line);
        occurrence.Range.Add(0);
        occurrence.Range.Add(3);
#pragma warning restore CS0612, CS0618
        return occurrence;
    }

    /// <summary>
    /// Hub 타입 하나가 target 타입 <paramref name="fanOut"/> 개를 부른다.
    /// 모든 타입은 네임스페이스 A/ 안, 패키지 Demo 안에 있다.
    /// </summary>
    private void BuildFixture(int fanOut)
    {
        var hub = new Document { RelativePath = "Hub.cs", Language = "C#" };
        hub.Occurrences.Add(At("A/Hub#", 0, definition: true));
        hub.Occurrences.Add(At("A/Hub#Run().", 1, definition: true));

        for (var i = 0; i < fanOut; i++)
        {
            hub.Occurrences.Add(At($"A/T{i}#", 2 + i, definition: false));
        }

        var targets = new Document { RelativePath = "Targets.cs", Language = "C#" };
        for (var i = 0; i < fanOut; i++)
        {
            targets.Occurrences.Add(At($"A/T{i}#", i, definition: true));
        }

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        index.Documents.Add(hub);
        index.Documents.Add(targets);

        using (var stream = File.Create(_scipPath))
        {
            index.WriteTo(stream);
        }

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord: 0, indexer: "test");
    }

    private long SeedId(SqliteConnection db, string display)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id FROM symbol WHERE display = $display LIMIT 1";
        command.Parameters.AddWithValue("$display", display);
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void Degree_cap_folds_a_hub_and_says_how_much_it_hid()
    {
        BuildFixture(fanOut: 40);
        using var db = GraphDb.Open(_dbPath);

        var result = Subgraph.Extract(db, new SubgraphRequest
        {
            Seeds = [SeedId(db, "Hub")],
            Granularity = Granularity.Type,
            Direction = Direction.Out,
            Depth = 1,
            DegreeCap = 10,
        });

        // 씨앗 하나 + 상한만큼의 이웃. 40개를 다 가져오지 않는다.
        Assert.Equal(11, result.Nodes.Count);

        // 그리고 잘린 사실을 숨기지 않는다.
        var truncation = Assert.Single(result.Truncated);
        Assert.Equal("Hub", truncation.Display);
        Assert.Equal(30, truncation.Hidden);
    }

    [Fact]
    public void Node_budget_stops_expansion()
    {
        BuildFixture(fanOut: 40);
        using var db = GraphDb.Open(_dbPath);

        var result = Subgraph.Extract(db, new SubgraphRequest
        {
            Seeds = [SeedId(db, "Hub")],
            Granularity = Granularity.Type,
            Direction = Direction.Out,
            Depth = 1,
            DegreeCap = 100,
            NodeBudget = 5,
        });

        Assert.True(result.BudgetExhausted);
        Assert.True(result.Nodes.Count <= 5, $"예산 5 를 넘었다: {result.Nodes.Count}");
    }

    [Fact]
    public void The_seed_set_itself_is_held_to_the_budget()
    {
        BuildFixture(fanOut: 40);
        using var db = GraphDb.Open(_dbPath);

        // 전체 지도는 씨앗이 곧 결과다. 넓힐 것이 없다고 예산을 건너뛰면
        // 큰 리포에서 수천 개가 그대로 나간다 — 실측에서 3,606개가 그렇게 샜다.
        var result = Subgraph.Extract(db, new SubgraphRequest
        {
            Seeds = Subgraph.AllAt(db, Granularity.Type),
            Granularity = Granularity.Type,
            WholeLevel = true,
            NodeBudget = 8,
        });

        Assert.True(result.Nodes.Count <= 8, $"예산 8 을 넘었다: {result.Nodes.Count}");
        Assert.True(result.BudgetExhausted);
        Assert.Contains(result.Truncated, item => item.Reason == "node-budget");
    }

    [Fact]
    public void Edges_are_held_to_a_budget_of_their_own()
    {
        BuildFixture(fanOut: 40);
        using var db = GraphDb.Open(_dbPath);

        // 노드만 묶어서는 페이로드가 안 잡힌다. 노드 2,000개에 간선 22,045개가
        // 딸려 와 JSON 이 1.7MB 가 된 적이 있다.
        var result = Subgraph.Extract(db, new SubgraphRequest
        {
            Seeds = [SeedId(db, "Hub")],
            Granularity = Granularity.Type,
            Direction = Direction.Out,
            Depth = 1,
            DegreeCap = 100,
            EdgeBudget = 5,
        });

        Assert.True(result.Edges.Count <= 5, $"간선 예산 5 를 넘었다: {result.Edges.Count}");
        Assert.Contains(result.Truncated, item => item.Reason == "edge-budget");

        // 자를 때는 무거운 것을 남긴다.
        Assert.All(result.Edges, edge => Assert.True(edge.Weight > 0));
    }

    [Fact]
    public void Module_granularity_collapses_everything_into_one_node()
    {
        BuildFixture(fanOut: 6);
        using var db = GraphDb.Open(_dbPath);

        var result = Subgraph.Extract(db, new SubgraphRequest
        {
            Seeds = [SeedId(db, "Hub")],
            Granularity = Granularity.Module,
            Depth = 2,
        });

        // 전부 같은 패키지라 모듈 층에서는 노드가 하나다. 자기 자신으로 가는 간선은 없다.
        var node = Assert.Single(result.Nodes);
        Assert.Equal("Demo", node.Display);
        Assert.Equal(SymbolKind.Package, node.Kind);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void Edges_only_connect_nodes_that_came_back()
    {
        BuildFixture(fanOut: 40);
        using var db = GraphDb.Open(_dbPath);

        var result = Subgraph.Extract(db, new SubgraphRequest
        {
            Seeds = [SeedId(db, "Hub")],
            Granularity = Granularity.Type,
            Direction = Direction.Out,
            Depth = 1,
            DegreeCap = 10,
        });

        var ids = result.Nodes.Select(node => node.Id).ToHashSet();
        Assert.All(result.Edges, edge =>
        {
            Assert.Contains(edge.From, ids);
            Assert.Contains(edge.To, ids);
        });
    }

    [Fact]
    public void Reference_edges_are_reported_as_inferred()
    {
        BuildFixture(fanOut: 3);
        using var db = GraphDb.Open(_dbPath);

        var result = Subgraph.Extract(db, new SubgraphRequest
        {
            Seeds = [SeedId(db, "Hub")],
            Granularity = Granularity.Type,
            Direction = Direction.Out,
            Depth = 1,
        });

        Assert.NotEmpty(result.Edges);
        Assert.All(result.Edges, edge => Assert.True(edge.Inferred));
    }
}
