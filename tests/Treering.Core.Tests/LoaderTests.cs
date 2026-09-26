using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 합성 index.scip 로 적재기 전체를 돌린다. 실제 리포 파일에 기대지 않으므로 결정적이다.
/// </summary>
public sealed class LoaderTests : IDisposable
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

    private static Occurrence Definition(string descriptors, int line, int character = 4) =>
        Occurrence(descriptors, line, character, (int)SymbolRole.Definition);

    private static Occurrence Reference(string descriptors, int line, int character = 8) =>
        Occurrence(descriptors, line, character, 0);

    private static Occurrence Occurrence(string descriptors, int line, int character, int roles)
    {
        var occurrence = new Occurrence { Symbol = descriptors.StartsWith("local ") ? descriptors : Prefix + descriptors, SymbolRoles = roles };
#pragma warning disable CS0612, CS0618
        occurrence.Range.Add(line);
        occurrence.Range.Add(character);
        occurrence.Range.Add(character + 3);
#pragma warning restore CS0612, CS0618
        return occurrence;
    }

    private LoadStats LoadFixture()
    {
        var caller = new Document { RelativePath = "Caller.cs", Language = "C#" };
        caller.Occurrences.Add(Definition("A/Caller#", 0));
        caller.Occurrences.Add(Definition("A/Caller#Run().", 2));
        // Run() 안에서 Target 과 그 메서드를 부른다.
        caller.Occurrences.Add(Reference("A/Target#", 3));
        caller.Occurrences.Add(Reference("A/Target#Do().", 4));
        // 잡음: 지역 심볼과 네임스페이스 조각. 둘 다 그래프에 올라가면 안 된다.
        caller.Occurrences.Add(Reference("local 7", 4));
        caller.Occurrences.Add(Reference("A/", 4));

        var target = new Document { RelativePath = "Target.cs", Language = "C#" };
        target.Occurrences.Add(Definition("A/Target#", 0));
        target.Occurrences.Add(Definition("A/Target#Do().", 2));

        var information = new SymbolInformation { Symbol = Prefix + "A/Target#" };
        information.Relationships.Add(new Relationship { Symbol = Prefix + "A/Base#", IsImplementation = true });
        target.Symbols.Add(information);

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        index.Documents.Add(caller);
        index.Documents.Add(target);

        using (var stream = File.Create(_scipPath))
        {
            index.WriteTo(stream);
        }

        using var db = GraphDb.Open(_dbPath);
        return Loader.Load(db, _scipPath, ord: 0, indexer: "test");
    }

    [Fact]
    public void Reader_streams_every_document_back()
    {
        LoadFixture();
        Assert.Equal(2, ScipReader.ReadDocuments(_scipPath).Count());
        Assert.Equal("file:///demo", ScipReader.ReadMetadata(_scipPath)!.ProjectRoot);
    }

    [Fact]
    public void Noise_never_becomes_an_edge_endpoint()
    {
        var stats = LoadFixture();

        // 지역 심볼 하나 + 네임스페이스 조각 하나. 둘 다 참조 간선이 되지 않는다.
        Assert.Equal(2, stats.SkippedNoise);

        using var db = GraphDb.Open(_dbPath);

        // 지역 심볼은 표에도 없다. 문서 밖에서는 뜻이 없다.
        Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM symbol WHERE kind = 1"));

        // 네임스페이스는 «있다» — 타입을 담는 그릇이라 없으면 지도를 말아 올릴 수 없다.
        Assert.True(Count(db, "SELECT COUNT(*) FROM symbol WHERE kind = 2") > 0);

        // 다만 참조·상속 간선의 끝으로는 절대 쓰이지 않는다.
        Assert.Equal(0, Count(db, """
            SELECT COUNT(*) FROM edge_life e
            JOIN symbol s ON s.id IN (e.from_id, e.to_id)
            WHERE e.kind IN (2, 3) AND s.kind = 2
            """));
    }

    [Fact]
    public void Types_keep_their_namespace_so_the_map_can_roll_up()
    {
        LoadFixture();
        using var db = GraphDb.Open(_dbPath);

        // A/Caller# 는 네임스페이스 A/ 안에 있다. 이게 비면 전체 지도가 평평해진다.
        Assert.Equal(1, Count(db, """
            SELECT COUNT(*) FROM symbol t
            JOIN symbol n ON n.id = t.namespace_id
            WHERE t.display = 'Caller' AND n.display = 'A'
            """));

        // 메서드는 자기를 담은 타입을 대표로 갖는다. 말아 올리기가 열 조회 한 번이 된다.
        Assert.Equal(1, Count(db, """
            SELECT COUNT(*) FROM symbol m
            JOIN symbol t ON t.id = m.type_id
            WHERE m.display = 'Run' AND t.display = 'Caller'
            """));
    }

    [Fact]
    public void Containment_comes_out_of_the_symbol_string()
    {
        LoadFixture();
        using var db = GraphDb.Open(_dbPath);

        // A/Caller#Run(). 의 부모는 A/Caller# 다. 색인기가 알려준 게 아니라 문자열에서 나온 것이다.
        var contains = Count(db, """
            SELECT COUNT(*) FROM edge_life e
            JOIN symbol parent ON parent.id = e.from_id
            JOIN symbol child  ON child.id  = e.to_id
            WHERE e.kind = 1 AND parent.display = 'Caller' AND child.display = 'Run'
            """);

        Assert.Equal(1, contains);
    }

    [Fact]
    public void Inheritance_is_recorded_as_exact_not_inferred()
    {
        LoadFixture();
        using var db = GraphDb.Open(_dbPath);

        var exact = Count(db, """
            SELECT COUNT(*) FROM edge_life e
            JOIN symbol f ON f.id = e.from_id
            JOIN symbol t ON t.id = e.to_id
            WHERE e.kind = 2 AND e.confidence = 0 AND f.display = 'Target' AND t.display = 'Base'
            """);

        Assert.Equal(1, exact);
    }

    [Fact]
    public void References_are_attributed_to_the_enclosing_definition_and_marked_inferred()
    {
        var stats = LoadFixture();

        // Target# 과 Target#Do(). 두 참조가 Run() 안에 있다.
        Assert.Equal(2, stats.AttributedReferences);

        using var db = GraphDb.Open(_dbPath);
        var fromRun = Count(db, """
            SELECT COUNT(*) FROM edge_life e
            JOIN symbol f ON f.id = e.from_id
            WHERE e.kind = 3 AND e.confidence = 1 AND f.display = 'Run'
            """);

        Assert.Equal(2, fromRun);
    }

    [Fact]
    public void A_reference_before_any_definition_is_left_unattributed()
    {
        // using 지시문처럼 첫 정의보다 앞선 참조는 귀속시키지 않는다. 추측해서 붙이면 안 된다.
        var document = new Document { RelativePath = "Early.cs", Language = "C#" };
        document.Occurrences.Add(Reference("A/Target#", 0, 0));
        document.Occurrences.Add(Definition("A/Holder#", 5));

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        index.Documents.Add(document);
        using (var stream = File.Create(_scipPath))
        {
            index.WriteTo(stream);
        }

        using var db = GraphDb.Open(_dbPath);
        var stats = Loader.Load(db, _scipPath, ord: 0, indexer: "test");

        Assert.Equal(1, stats.UnattributedReferences);
        Assert.Equal(0, stats.AttributedReferences);
    }

    [Fact]
    public void Everything_is_born_in_the_loaded_snapshot_and_still_alive()
    {
        LoadFixture();
        using var db = GraphDb.Open(_dbPath);

        Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM edge_life WHERE born_ord <> 0"));
        Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM edge_life WHERE died_ord IS NOT NULL"));
    }

    private static int Count(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
