using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 「우리가 쓴 것」 과 「가져다 쓴 것」 의 경계를 못 박는다.
/// 규칙은 이름이 아니라 <b>정의가 이 색인 안에 있는가</b> 하나다.
/// </summary>
public sealed class OwnScopeTests : IDisposable
{
    private const string Ours = "scip-dotnet nuget Demo 1.0.0.0 ";
    private const string Theirs = "scip-dotnet nuget Vendor 9.9.9.9 ";

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

    private static Occurrence At(string prefix, string descriptors, int line, bool definition)
    {
        var occurrence = new Occurrence
        {
            Symbol = prefix + descriptors,
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
    /// 우리 타입 하나가 남의 타입 하나를 부른다. 남의 것에는 정의가 없다 —
    /// 색인기가 바깥 패키지를 내보내는 모습이 실제로 이렇다.
    /// </summary>
    private void BuildFixture()
    {
        var document = new Document { RelativePath = "Hub.cs", Language = "C#" };
        document.Occurrences.Add(At(Ours, "A/Hub#", 0, definition: true));
        document.Occurrences.Add(At(Ours, "A/Hub#Run().", 1, definition: true));
        document.Occurrences.Add(At(Theirs, "V/Client#", 2, definition: false));

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        index.Documents.Add(document);

        using (var stream = File.Create(_scipPath))
        {
            index.WriteTo(stream);
        }

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord: 0, indexer: "test");
    }

    [Fact]
    public void A_python_module_the_indexer_filed_under_our_package_is_not_ours_without_a_definition()
    {
        // scip-python files an unresolved third-party module under our own package name.
        const string python = "scip-python python worker 0 ";
        var document = new Document { RelativePath = "src/shop/orders.py", Language = "python" };
        document.Occurrences.Add(At(python, "`src.shop.orders`/Order#", 0, definition: true));
        document.Occurrences.Add(At(python, "`tablekit.io.sheets`/read_sheet().", 1, definition: false));

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///worker" } };
        index.Documents.Add(document);
        using (var stream = File.Create(_scipPath)) index.WriteTo(stream);

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord: 0, indexer: "test");

        Assert.Equal(1, OwnOf(db, "Order"));
        Assert.Equal(1, OwnOf(db, "src.shop.orders"));
        Assert.Equal(1, OwnOf(db, "src.shop"));
        Assert.Equal(1, OwnOf(db, "src"));

        Assert.Equal(0, OwnOf(db, "tablekit.io.sheets"));
        Assert.Equal(0, OwnOf(db, "read_sheet"));
        Assert.Equal(0, OwnOf(db, "tablekit"));
    }

    private static int OwnOf(SqliteConnection db, string display)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT own FROM symbol WHERE display = $display LIMIT 1";
        command.Parameters.AddWithValue("$display", display);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public void What_is_defined_here_is_ours_and_what_is_only_referenced_is_not()
    {
        BuildFixture();
        using var db = GraphDb.Open(_dbPath);

        Assert.Equal(1, OwnOf(db, "Hub"));
        Assert.Equal(0, OwnOf(db, "Client"));

        // 패키지도 같이 갈린다. 모듈 지도가 이것으로 걸러진다.
        Assert.Equal(1, OwnOf(db, "Demo"));
        Assert.Equal(0, OwnOf(db, "Vendor"));
    }

    [Fact]
    public void A_member_with_no_definition_of_its_own_still_counts_as_ours()
    {
        BuildFixture();
        using var db = GraphDb.Open(_dbPath);

        // 우리 어셈블리 안에 사는 것은 전부 우리 것이다. 그러지 않으면 타입을 눌렀을 때
        // 멤버 절반이 사라진다 — 정의가 안 잡히는 멤버가 있기 때문이다.
        Assert.Equal(1, OwnOf(db, "Run"));
    }

    [Fact]
    public void Own_only_leaves_the_outside_out_of_the_map()
    {
        BuildFixture();
        using var db = GraphDb.Open(_dbPath);

        var everything = Subgraph.Extract(db, new SubgraphRequest
        {
            Seeds = Subgraph.AllAt(db, Granularity.Type),
            Granularity = Granularity.Type,
            WholeLevel = true,
        });

        var ours = Subgraph.Extract(db, new SubgraphRequest
        {
            Seeds = Subgraph.AllAt(db, Granularity.Type, ownOnly: true),
            Granularity = Granularity.Type,
            WholeLevel = true,
            OwnOnly = true,
        });

        Assert.Contains(everything.Nodes, node => node.Display == "Client");
        Assert.DoesNotContain(ours.Nodes, node => node.Display == "Client");
        Assert.Contains(ours.Nodes, node => node.Display == "Hub");

        // 밖으로 나가는 간선도 같이 사라진다. 한쪽 끝이 없는 선은 그릴 수 없다.
        Assert.Empty(ours.Edges);
    }

    [Fact]
    public void Own_only_also_holds_one_hop_out_from_a_seed()
    {
        BuildFixture();
        using var db = GraphDb.Open(_dbPath);

        using var lookup = db.CreateCommand();
        lookup.CommandText = "SELECT id FROM symbol WHERE display = 'Hub' LIMIT 1";
        var hub = (long)lookup.ExecuteScalar()!;

        var result = Subgraph.Extract(db, new SubgraphRequest
        {
            Seeds = [hub],
            Granularity = Granularity.Type,
            Direction = Direction.Out,
            Depth = 1,
            OwnOnly = true,
        });

        Assert.DoesNotContain(result.Nodes, node => node.Display == "Client");
    }
}
