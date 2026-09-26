using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// scip-dotnet 이 C# 12 기본 생성자 클래스의 멤버를 <c>&lt;invalid-global-code&gt;</c> 라는
/// 가짜 타입 밑으로 보내는 경우. 되붙이기가 <b>심볼만이 아니라 그래프까지</b> 고치는지 본다.
///
/// 되붙이기가 주인만 바꾸고 간선을 두던 동안에는 가짜 타입이 멤버 56개를 담고 있다고
/// 그려지고, 진짜 클래스에서는 자기 멤버로 가는 담는 간선이 0개였다.
/// </summary>
public sealed class PlaceholderTests : IDisposable
{
    private const string Prefix = "scip-dotnet nuget Demo 1.0.0.0 ";
    private const string Placeholder = "`<invalid-global-code>`#";

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
        occurrence.Range.Add(4);
        occurrence.Range.Add(7);
#pragma warning restore CS0612, CS0618
        return occurrence;
    }

    /// <summary>
    /// <c>Repo.cs</c> — 진짜 클래스 뒤에 가짜 타입 밑으로 간 멤버와 그 매개변수가 온다.
    /// 멤버 안에서 <c>Other</c> 를 부른다.
    ///
    /// <c>Loose.cs</c> — 앞선 타입이 하나도 없는 파일. 여기 멤버는 주인을 찾을 수 없다.
    /// </summary>
    private void Load()
    {
        var repo = new Document { RelativePath = "Repo.cs", Language = "C#" };
        repo.Occurrences.Add(At("A/Repo#", 0, definition: true));
        repo.Occurrences.Add(At("A/" + Placeholder + "Fetch().", 2, definition: true));
        repo.Occurrences.Add(At("A/" + Placeholder + "Fetch().(id)", 2, definition: true));
        repo.Occurrences.Add(At("A/Other#", 3, definition: false));

        var loose = new Document { RelativePath = "Loose.cs", Language = "C#" };
        loose.Occurrences.Add(At("B/" + Placeholder + "Stray().", 0, definition: true));

        var other = new Document { RelativePath = "Other.cs", Language = "C#" };
        other.Occurrences.Add(At("A/Other#", 0, definition: true));

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        index.Documents.Add(repo);
        index.Documents.Add(loose);
        index.Documents.Add(other);

        using (var stream = File.Create(_scipPath))
        {
            index.WriteTo(stream);
        }

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord: 0, indexer: "test");
    }

    private static long Id(SqliteConnection db, string keySuffix)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id FROM symbol WHERE key LIKE '%' || $suffix";
        command.Parameters.AddWithValue("$suffix", keySuffix);
        return (long)command.ExecuteScalar()!;
    }

    private static long? Column(SqliteConnection db, long id, string column)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT {column} FROM symbol WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is long value ? value : null;
    }

    private static int LiveContains(SqliteConnection db, long from, long to)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM edge_life
            WHERE kind = 1 AND died_ord IS NULL AND from_id = $from AND to_id = $to
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int Placeholders(SqliteConnection db, string ns)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbol WHERE key LIKE '%' || $key";
        command.Parameters.AddWithValue("$key", ns + "/" + Placeholder);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public void A_stray_member_goes_home_to_the_type_above_it()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        var repo = Id(db, "A/Repo#");
        var fetch = Id(db, "Fetch().");

        Assert.Equal(repo, Column(db, fetch, "container_id"));
        Assert.Equal(repo, Column(db, fetch, "type_id"));
    }

    [Fact]
    public void The_graph_moves_with_it_not_just_the_symbol()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        var repo = Id(db, "A/Repo#");
        var fetch = Id(db, "Fetch().");

        // 진짜 주인이 담고 있다고 그래프도 말해야 한다.
        Assert.Equal(1, LiveContains(db, repo, fetch));

        using var stale = db.CreateCommand();
        stale.CommandText = """
            SELECT COUNT(*) FROM edge_life
            WHERE kind = 1 AND died_ord IS NULL
              AND from_id <> (SELECT container_id FROM symbol WHERE id = edge_life.to_id)
            """;
        Assert.Equal(0, Convert.ToInt32(stale.ExecuteScalar()));
    }

    [Fact]
    public void What_hangs_below_the_member_rolls_up_to_the_new_owner_too()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        // 매개변수는 되붙이기가 직접 건드리지 않는다. 그래도 가짜 타입으로 말려 올라가면
        // 타입 층 지도에서 그 참조가 가짜 타입에서 나가는 선으로 그려진다.
        Assert.Equal(Id(db, "A/Repo#"), Column(db, Id(db, "Fetch().(id)"), "type_id"));
    }

    [Fact]
    public void An_emptied_placeholder_leaves_the_graph()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        Assert.Equal(0, Placeholders(db, "A"));

        var map = Subgraph.Extract(db, new SubgraphRequest
        {
            Seeds = Subgraph.AllAt(db, Granularity.Type),
            Granularity = Granularity.Type,
            WholeLevel = true,
        });

        // 멤버 안의 참조는 진짜 주인에게서 나간다.
        var repo = Id(db, "A/Repo#");
        var other = Id(db, "A/Other#");
        Assert.Contains(map.Edges, edge => edge.From == repo && edge.To == other);
    }

    [Fact]
    public void A_placeholder_that_still_holds_something_stays()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        // 앞선 타입이 없는 파일의 멤버는 갈 곳이 없다. 가짜 타입까지 지우면
        // 있는 코드를 없다고 말하게 된다.
        Assert.Equal(1, Placeholders(db, "B"));
        Assert.Equal(Id(db, "B/" + Placeholder), Column(db, Id(db, "Stray()."), "container_id"));
    }
}
