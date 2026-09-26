using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 증분 갱신의 계약. 프로젝트 하나만 다시 색인해 갈아끼울 때
/// <b>손대지 않은 코드가 사라진 것으로 기록되면 안 된다.</b>
/// </summary>
public sealed class IncrementalTests : IDisposable
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

    /// <summary>한 파일에 타입 하나와, 그 타입이 부르는 대상들.</summary>
    private static Document Source(string name, string type, params string[] calls)
    {
        var document = new Document { RelativePath = name, Language = "C#" };
        document.Occurrences.Add(At($"A/{type}#", 0, true));
        document.Occurrences.Add(At($"A/{type}#Run().", 1, true));

        var line = 2;
        foreach (var call in calls) document.Occurrences.Add(At($"A/{call}#", line++, false));

        return document;
    }

    private LoadStats Write(int ord, bool partial, params Document[] documents)
    {
        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        index.Documents.AddRange(documents);

        using (var stream = File.Create(_scipPath))
        {
            index.WriteTo(stream);
        }

        using var db = GraphDb.Open(_dbPath);
        return Loader.Load(db, _scipPath, ord, indexer: "test", partial);
    }

    private static Document[] WholeRepo() =>
    [
        Source("Left.cs", "Left", "Shared"),
        Source("Right.cs", "Right", "Shared"),
        Source("Shared.cs", "Shared"),
    ];

    [Fact]
    public void Patching_one_file_leaves_the_rest_alone()
    {
        Write(0, partial: false, WholeRepo());

        // Left.cs 만 다시 색인했다. Right.cs 는 손대지 않았다.
        var patch = Write(1, partial: true, Source("Left.cs", "Left", "Shared"));

        Assert.Equal(0, patch.DiedSymbols);
        Assert.Equal(0, patch.DiedEdges);
        Assert.Equal(0, patch.BornSymbols);
        Assert.Equal(0, patch.BornEdges);

        using var db = GraphDb.Open(_dbPath);
        // Left.Run 과 Right.Run 은 이름이 같다. 담는 타입으로 갈라야 한다.
        Assert.Equal(1, Count(db, """
            SELECT COUNT(*) FROM edge_life e
            JOIN symbol f ON f.id = e.from_id
            JOIN symbol c ON c.id = f.container_id
            JOIN symbol t ON t.id = e.to_id
            WHERE e.kind = 3 AND e.died_ord IS NULL
              AND c.display = 'Left' AND f.display = 'Run' AND t.display = 'Shared'
            """));
    }

    [Fact]
    public void A_dependency_dropped_inside_the_patched_file_does_die()
    {
        Write(0, partial: false, WholeRepo());

        // Left 가 더 이상 Shared 를 부르지 않는다.
        var patch = Write(1, partial: true, Source("Left.cs", "Left"));

        Assert.True(patch.DiedEdges > 0, "범위 안에서 끊긴 의존이 기록되지 않았다");

        using var db = GraphDb.Open(_dbPath);

        // Right 가 부르던 것은 그대로 살아 있어야 한다.
        var stillAlive = Count(db, """
            SELECT COUNT(*) FROM edge_life e
            JOIN symbol f ON f.id = e.from_id
            JOIN symbol c ON c.id = f.container_id
            JOIN symbol t ON t.id = e.to_id
            WHERE e.kind = 3 AND e.died_ord IS NULL AND c.display = 'Right' AND t.display = 'Shared'
            """);

        Assert.Equal(1, stillAlive);
    }

    [Fact]
    public void A_type_deleted_from_the_patched_file_dies()
    {
        Write(0, partial: false, WholeRepo());

        // Left.cs 가 비었다 — 파일은 있지만 타입이 없다.
        var empty = new Document { RelativePath = "Left.cs", Language = "C#" };
        var patch = Write(1, partial: true, empty);

        Assert.True(patch.DiedSymbols >= 2, $"Left 와 그 메서드가 죽지 않았다: {patch.DiedSymbols}");

        using var db = GraphDb.Open(_dbPath);
        Assert.Equal(1, Count(db, """
            SELECT COUNT(*) FROM symbol_life l
            JOIN symbol s ON s.id = l.symbol_id
            WHERE s.display = 'Left' AND l.died_ord = 1
            """));

        // Right 는 멀쩡하다.
        Assert.Equal(0, Count(db, """
            SELECT COUNT(*) FROM symbol_life l
            JOIN symbol s ON s.id = l.symbol_id
            WHERE s.display = 'Right' AND l.died_ord IS NOT NULL
            """));
    }

    [Fact]
    public void External_symbols_outside_the_patch_are_not_reborn_every_time()
    {
        Write(0, partial: false, WholeRepo());
        Write(1, partial: true, Source("Left.cs", "Left", "Shared"));
        Write(2, partial: true, Source("Left.cs", "Left", "Shared"));

        using var db = GraphDb.Open(_dbPath);

        // 범위 밖 심볼에 생애 행이 겹쳐 쌓이면, 부분 적재가 반복될수록 DB 가 부푼다.
        // 실측에서 외부 BCL 심볼 183개가 이렇게 샜다.
        var duplicated = Count(db, """
            SELECT COUNT(*) FROM (
                SELECT symbol_id FROM symbol_life GROUP BY symbol_id HAVING COUNT(*) > 1
            )
            """);

        Assert.Equal(0, duplicated);
    }

    private static int Count(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
