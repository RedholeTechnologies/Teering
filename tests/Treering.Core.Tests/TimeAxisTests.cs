using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 시간축의 계약. 스냅샷을 쌓아도 그래프를 복제하지 않고, 새로 난 것과 사라진 것만
/// 구간으로 남는다. 굵어진 간선은 죽지 않는다.
/// </summary>
public sealed class TimeAxisTests : IDisposable
{
    private const string Prefix = "scip-dotnet nuget Demo ";

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

    private static Occurrence At(string descriptors, int line, bool definition, string version = "1.0.0.0")
    {
        var occurrence = new Occurrence
        {
            Symbol = Prefix + version + " " + descriptors,
            SymbolRoles = definition ? (int)SymbolRole.Definition : 0,
        };
#pragma warning disable CS0612, CS0618
        occurrence.Range.Add(line);
        occurrence.Range.Add(0);
        occurrence.Range.Add(3);
#pragma warning restore CS0612, CS0618
        return occurrence;
    }

    /// <summary>Caller 가 <paramref name="targets"/> 를 각각 <paramref name="times"/> 번 부른다.</summary>
    private LoadStats Snapshot(int ord, string[] targets, int times = 1, string version = "1.0.0.0")
    {
        var caller = new Document { RelativePath = "Caller.cs", Language = "C#" };
        caller.Occurrences.Add(At("A/Caller#", 0, true, version));
        caller.Occurrences.Add(At("A/Caller#Run().", 1, true, version));

        var line = 2;
        foreach (var target in targets)
        {
            for (var i = 0; i < times; i++) caller.Occurrences.Add(At($"A/{target}#", line++, false, version));
        }

        var defined = new Document { RelativePath = "Targets.cs", Language = "C#" };
        var at = 0;
        foreach (var target in targets) defined.Occurrences.Add(At($"A/{target}#", at++, true, version));

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        index.Documents.Add(caller);
        index.Documents.Add(defined);

        using (var stream = File.Create(_scipPath))
        {
            index.WriteTo(stream);
        }

        using var db = GraphDb.Open(_dbPath);
        return Loader.Load(db, _scipPath, ord, indexer: "test");
    }

    [Fact]
    public void A_second_snapshot_records_only_what_changed()
    {
        Snapshot(0, ["Target", "Keeper"]);
        var second = Snapshot(1, ["Keeper", "Newcomer"]);

        // Keeper 는 양쪽에 있으므로 손대지 않는다. Target 이 사라지고 Newcomer 가 태어난다.
        Assert.Equal(1, second.DiedSymbols);
        Assert.True(second.BornSymbols >= 1, $"새 심볼이 없다: {second.BornSymbols}");

        using var db = GraphDb.Open(_dbPath);

        var appeared = TimeAxis.Appeared(db, 0, 1);
        var gone = TimeAxis.Disappeared(db, 0, 1);

        Assert.Contains(appeared, change => change.To == "Newcomer");
        Assert.Contains(gone, change => change.To == "Target");
        Assert.DoesNotContain(appeared, change => change.To == "Keeper");
        Assert.DoesNotContain(gone, change => change.To == "Keeper");
    }

    [Fact]
    public void An_edge_that_merely_gets_thicker_does_not_die()
    {
        Snapshot(0, ["Target"], times: 1);
        var second = Snapshot(1, ["Target"], times: 4);

        // 3곳에서 12곳이 된 것은 «같은 의존» 이 굵어진 것이다. 죽고 새로 나면 시간축이 거짓말을 한다.
        Assert.Equal(0, second.DiedEdges);
        Assert.Equal(0, second.BornEdges);
        Assert.True(second.WeightChanges > 0, "굵기 변화가 기록되지 않았다");

        using var db = GraphDb.Open(_dbPath);
        var growth = TimeAxis.CallersOverTime(db, "Target");

        Assert.Equal(2, growth.Count);
        Assert.True(growth[1].Weight > growth[0].Weight,
            $"굵어졌는데 이력이 줄었다: {growth[0].Weight} -> {growth[1].Weight}");
    }

    [Fact]
    public void Nothing_changes_when_the_same_index_is_loaded_twice()
    {
        Snapshot(0, ["Target", "Keeper"]);
        var again = Snapshot(1, ["Target", "Keeper"]);

        // 같은 코드를 다시 색인했을 뿐이다. 저장량이 변경량을 따라간다는 전제가 여기서 확인된다.
        Assert.Equal(0, again.BornEdges);
        Assert.Equal(0, again.DiedEdges);
        Assert.Equal(0, again.BornSymbols);
        Assert.Equal(0, again.DiedSymbols);
        Assert.Equal(0, again.WeightChanges);
    }

    [Fact]
    public void A_snapshot_that_changed_nothing_is_not_kept()
    {
        // The server re-indexes uncommitted work again after a restart. The graph comes out the
        // same, and that must not leave an empty step on the time axis.
        Snapshot(0, ["Target", "Keeper"]);
        Snapshot(1, ["Target", "Keeper"]);

        using var db = GraphDb.Open(_dbPath);
        Assert.True(TimeAxis.DropIfEmpty(db, 1));
        Assert.Equal([0], TimeAxis.Snapshots(db).Select(snapshot => snapshot.Ord).ToList());

        // Nothing may still point at the number just freed: the next snapshot takes it again.
        using var left = db.CreateCommand();
        left.CommandText = "SELECT COUNT(*) FROM symbol_version WHERE born_ord = 1 OR died_ord = 1";
        Assert.Equal(0L, left.ExecuteScalar());
    }

    [Fact]
    public void A_snapshot_that_changed_something_is_kept()
    {
        Snapshot(0, ["Target", "Keeper"]);
        Snapshot(1, ["Keeper", "Newcomer"]);

        using var db = GraphDb.Open(_dbPath);
        Assert.False(TimeAxis.DropIfEmpty(db, 1));
        Assert.Equal([0, 1], TimeAxis.Snapshots(db).Select(snapshot => snapshot.Ord).ToList());
    }

    [Fact]
    public void A_snapshot_where_only_a_weight_changed_is_kept()
    {
        // Three calls becoming twelve is the history "growth" reads. It is a change.
        Snapshot(0, ["Target"], times: 1);
        Snapshot(1, ["Target"], times: 4);

        using var db = GraphDb.Open(_dbPath);
        Assert.False(TimeAxis.DropIfEmpty(db, 1));
    }

    [Fact]
    public void Bumping_the_assembly_version_does_not_churn_the_graph()
    {
        Snapshot(0, ["Target"], version: "1.0.0.0");
        var bumped = Snapshot(1, ["Target"], version: "2.0.0.0");

        // 키에서 버전을 뺀 이유가 이것이다. 릴리스 한 번에 전 심볼이 죽으면 시간축이 무너진다.
        Assert.Equal(0, bumped.BornSymbols);
        Assert.Equal(0, bumped.DiedSymbols);
        Assert.Equal(0, bumped.BornEdges);
        Assert.Equal(0, bumped.DiedEdges);

        using var db = GraphDb.Open(_dbPath);

        // 그래도 버전이 바뀐 사실 자체는 남아야 한다.
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbol_version WHERE version = '2.0.0.0' AND born_ord = 1";
        Assert.True(Convert.ToInt32(command.ExecuteScalar()) > 0, "버전 변화가 기록되지 않았다");
    }

    [Fact]
    public void Snapshots_are_listed_in_order()
    {
        Snapshot(0, ["Target"]);
        Snapshot(1, ["Target"]);

        using var db = GraphDb.Open(_dbPath);
        var snapshots = TimeAxis.Snapshots(db);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(0, snapshots[0].Ord);
        Assert.Equal(1, snapshots[1].Ord);
    }
}
