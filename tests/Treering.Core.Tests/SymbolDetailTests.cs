using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 상세 패널의 「부르는 곳 · 부르는 대상」. 멤버를 바로 열 수 있게 되면서(검색 · 계층도)
/// 메서드 패널이 언제나 「없음」 이던 것이 드러났다 — 그 자리를 못 박는다.
/// </summary>
public sealed class SymbolDetailTests : IDisposable
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
        occurrence.Range.Add(8);
        occurrence.Range.Add(11);
#pragma warning restore CS0612, CS0618
        return occurrence;
    }

    /// <summary>
    /// Caller.Run() calls Target.Do() and its own sibling Caller.Helper().
    /// </summary>
    private void Load()
    {
        var caller = new Document { RelativePath = "Caller.cs", Language = "C#" };
        caller.Occurrences.Add(At("A/Caller#", 0, definition: true));
        caller.Occurrences.Add(At("A/Caller#Run().", 2, definition: true));
        // A parameter is declared after the method and before its body, so what the body
        // references is attributed to the parameter. It still belongs to Run().
        caller.Occurrences.Add(At("A/Caller#Run().(input)", 2, definition: true));
        caller.Occurrences.Add(At("A/Other#", 3, definition: false));
        caller.Occurrences.Add(At("A/Target#Do().", 3, definition: false));
        caller.Occurrences.Add(At("A/Caller#Helper().", 4, definition: false));
        caller.Occurrences.Add(At("A/Caller#Helper().", 6, definition: true));

        var target = new Document { RelativePath = "Target.cs", Language = "C#" };
        target.Occurrences.Add(At("A/Target#", 0, definition: true));
        target.Occurrences.Add(At("A/Target#Do().", 2, definition: true));

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        index.Documents.Add(caller);
        index.Documents.Add(target);

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

    [Fact]
    public void A_method_says_which_types_call_it()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        var detail = SymbolDetail.Of(db, Id(db, "A/Target#Do()."))!;

        Assert.Equal(["Caller"], detail.Callers.Select(item => item.Display).ToList());
    }

    [Fact]
    public void A_method_says_which_types_it_calls_leaving_out_its_own()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        var detail = SymbolDetail.Of(db, Id(db, "A/Caller#Run()."))!;

        // Helper() is in Caller too - calling your own type is not a dependency.
        Assert.Equal(["Other", "Target"], detail.Calls.Select(item => item.Display).Order().ToList());
    }

    [Fact]
    public void A_type_still_answers_at_type_level()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        var detail = SymbolDetail.Of(db, Id(db, "A/Target#"))!;

        Assert.Equal(["Caller"], detail.Callers.Select(item => item.Display).ToList());
    }
}
