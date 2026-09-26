using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 「지난주 대비 무엇이 새로 엮였나」 를 모듈 단위로 물을 때의 약속. 이것이 이 도구가
/// 파는 답이다. 틀리면 모듈 A 가 B 를 여전히 쓰는데 「끊겼다」 고 하거나, 원래 쓰던 것을
/// 「새로 쓰기 시작했다」 고 한다 — 사람은 그 말을 믿고 아키텍처를 판단한다.
///
/// 모듈 A→B 하나에는 타입 사이 간선이 여럿 말려 있다. 그중 하나가 죽거나 새로 나는 것은
/// A 와 B 의 관계가 바뀐 것이 아니다. 「기준 시점엔 있었고 지금은 없다」 만이 끊김이다.
/// </summary>
public sealed class ModuleChangeTests : IDisposable
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

    // "Core.Repo" → a type Repo in our package Core. Vendor is someone else's: referenced, never defined.
    private static string Symbol(string qualified)
    {
        var dot = qualified.IndexOf('.');
        var package = qualified[..dot];
        return $"scip-dotnet nuget {package} 1.0.0.0 {package}/{qualified[(dot + 1)..]}#";
    }

    private static Occurrence At(string qualified, int line, bool definition)
    {
        var occurrence = new Occurrence
        {
            Symbol = Symbol(qualified),
            SymbolRoles = definition ? (int)SymbolRole.Definition : 0,
        };
#pragma warning disable CS0612, CS0618
        occurrence.Range.Add(line);
        occurrence.Range.Add(0);
        occurrence.Range.Add(3);
#pragma warning restore CS0612, CS0618
        return occurrence;
    }

    /// <summary>Each of our types sits in its own file and references the ones listed for it.</summary>
    private void Snapshot(int ord, Dictionary<string, string[]> uses)
    {
        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        foreach (var (type, targets) in uses)
        {
            var document = new Document { RelativePath = type + ".cs", Language = "C#" };
            document.Occurrences.Add(At(type, 0, definition: true));
            var line = 1;
            foreach (var target in targets) document.Occurrences.Add(At(target, line++, definition: false));
            index.Documents.Add(document);
        }

        using (var stream = File.Create(_scipPath))
        {
            index.WriteTo(stream);
        }

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord, indexer: "test");
    }

    private static List<(string From, string To)> Pairs(List<DependencyChange> changes) =>
        changes.Select(change => (change.From, change.To)).OrderBy(pair => pair).ToList();

    [Fact]
    public void A_module_still_uses_another_while_any_one_of_its_links_remains()
    {
        Snapshot(0, new() { ["Web.Page"] = ["Core.Repo", "Core.Money"], ["Core.Repo"] = [], ["Core.Money"] = [] });
        Snapshot(1, new() { ["Web.Page"] = ["Core.Repo"], ["Core.Repo"] = [], ["Core.Money"] = [] });

        using var db = GraphDb.Open(_dbPath);

        // One type-level link went; Web still uses Core.
        Assert.Empty(TimeAxis.Disappeared(db, 0, 1, Granularity.Module));
        Assert.Equal([("Page", "Money")], Pairs(TimeAxis.Disappeared(db, 0, 1, Granularity.Type)));
    }

    [Fact]
    public void A_module_is_reported_broken_only_when_its_last_link_goes()
    {
        Snapshot(0, new() { ["Web.Page"] = ["Core.Repo", "Core.Money"], ["Core.Repo"] = [], ["Core.Money"] = [] });
        Snapshot(1, new() { ["Web.Page"] = [], ["Core.Repo"] = [], ["Core.Money"] = [] });

        using var db = GraphDb.Open(_dbPath);

        Assert.Equal([("Web", "Core")], Pairs(TimeAxis.Disappeared(db, 0, 1, Granularity.Module)));
    }

    [Fact]
    public void Another_link_between_modules_already_wired_is_not_a_new_dependency()
    {
        Snapshot(0, new() { ["Web.Page"] = ["Core.Repo"], ["Core.Repo"] = [], ["Core.Money"] = [], ["Data.Db"] = [] });
        Snapshot(1, new() { ["Web.Page"] = ["Core.Repo", "Core.Money"], ["Core.Repo"] = ["Data.Db"], ["Core.Money"] = [], ["Data.Db"] = [] });

        using var db = GraphDb.Open(_dbPath);

        // Web→Core was already there; only Core→Data is new between modules.
        Assert.Equal([("Core", "Data")], Pairs(TimeAxis.Appeared(db, 0, 1, Granularity.Module)));
    }

    [Fact]
    public void A_link_inside_one_module_is_not_a_module_dependency()
    {
        Snapshot(0, new() { ["Core.Repo"] = [], ["Core.Money"] = [] });
        Snapshot(1, new() { ["Core.Repo"] = ["Core.Money"], ["Core.Money"] = [] });

        using var db = GraphDb.Open(_dbPath);

        Assert.Empty(TimeAxis.Appeared(db, 0, 1, Granularity.Module));
        Assert.Equal([("Repo", "Money")], Pairs(TimeAxis.Appeared(db, 0, 1, Granularity.Type)));
    }

    [Fact]
    public void Crossing_between_two_of_our_modules_is_a_boundary_crossing()
    {
        Snapshot(0, new() { ["Web.Page"] = [], ["Core.Repo"] = [] });
        Snapshot(1, new() { ["Web.Page"] = ["Core.Repo"], ["Core.Repo"] = [] });

        using var db = GraphDb.Open(_dbPath);

        var change = Assert.Single(TimeAxis.Appeared(db, 0, 1, Granularity.Type));
        Assert.True(change.BothOurs);
        Assert.True(change.CrossesBoundary);
    }

    [Fact]
    public void Starting_to_use_a_library_is_not_a_boundary_crossing()
    {
        // Measured: counting these made 916 of 1,094 "crossings" EF Core rows, useless as a watch.
        Snapshot(0, new() { ["Web.Page"] = [] });
        Snapshot(1, new() { ["Web.Page"] = ["Vendor.Client"] });

        using var db = GraphDb.Open(_dbPath);

        var change = Assert.Single(TimeAxis.Appeared(db, 0, 1, Granularity.Type));
        Assert.False(change.BothOurs);
        Assert.False(change.CrossesBoundary);
    }

    [Fact]
    public void A_link_within_one_of_our_modules_is_not_a_boundary_crossing()
    {
        Snapshot(0, new() { ["Core.Repo"] = [], ["Core.Money"] = [] });
        Snapshot(1, new() { ["Core.Repo"] = ["Core.Money"], ["Core.Money"] = [] });

        using var db = GraphDb.Open(_dbPath);

        var change = Assert.Single(TimeAxis.Appeared(db, 0, 1, Granularity.Type));
        Assert.True(change.BothOurs);
        Assert.False(change.CrossesBoundary);
    }

    [Fact]
    public void A_commit_is_written_on_its_own_snapshot_and_no_other()
    {
        // watch resumes from the commit on the last snapshot. Written on the wrong one, it re-indexes
        // work already done, or skips work that was not.
        Snapshot(0, new() { ["Core.Repo"] = [] });
        Snapshot(1, new() { ["Core.Repo"] = [] });

        using var db = GraphDb.Open(_dbPath);
        TimeAxis.StampCommit(db, 1, "abc123");

        var snapshots = TimeAxis.Snapshots(db);
        Assert.Null(snapshots.Single(s => s.Ord == 0).CommitSha);
        Assert.Equal("abc123", snapshots.Single(s => s.Ord == 1).CommitSha);
    }

    [Fact]
    public void No_commit_leaves_the_one_already_written()
    {
        // Outside a git repository there is no sha. That must not wipe the one recorded earlier.
        Snapshot(0, new() { ["Core.Repo"] = [] });

        using var db = GraphDb.Open(_dbPath);
        TimeAxis.StampCommit(db, 0, "abc123");
        TimeAxis.StampCommit(db, 0, null);

        Assert.Equal("abc123", Assert.Single(TimeAxis.Snapshots(db)).CommitSha);
    }
}
