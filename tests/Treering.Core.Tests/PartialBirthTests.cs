using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// A partial update records births and deaths only for what it owns: what its files define. A
/// library symbol our code starts using is owned by no one, so when it first appeared in a
/// partial update it got lines but no life - and without a life it is in no tree, map or search.
/// The watcher runs only partial updates, so every library call added after import went missing.
/// </summary>
public sealed class PartialBirthTests : IDisposable
{
    private const string Ours = "scip-dotnet nuget Demo 1.0.0.0 ";
    private const string Clock = "scip-dotnet nuget Vendor 2.0.0.0 Vendor/Clock#";

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

    private static Occurrence At(string symbol, int line, bool definition)
    {
        var occurrence = new Occurrence { Symbol = symbol, SymbolRoles = definition ? (int)SymbolRole.Definition : 0 };
#pragma warning disable CS0612, CS0618
        occurrence.Range.Add(line);
        occurrence.Range.Add(0);
        occurrence.Range.Add(3);
#pragma warning restore CS0612, CS0618
        return occurrence;
    }

    /// <summary>
    /// Left.cs defines Left.Run(), which uses the given symbols. A use written "Stop>symbol" comes
    /// from a second method, Left.Stop(), instead.
    /// </summary>
    private void Load(int ord, bool partial, params string[] uses)
    {
        var document = new Document { RelativePath = "Left.cs", Language = "C#" };
        document.Occurrences.Add(At(Ours + "A/Left#", 0, definition: true));
        document.Occurrences.Add(At(Ours + "A/Left#Run().", 1, definition: true));
        var line = 2;
        foreach (var use in uses.Where(use => !use.StartsWith("Stop>"))) document.Occurrences.Add(At(use, line++, definition: false));
        document.Occurrences.Add(At(Ours + "A/Left#Stop().", 100, definition: true));
        line = 101;
        foreach (var use in uses.Where(use => use.StartsWith("Stop>"))) document.Occurrences.Add(At(use["Stop>".Length..], line++, definition: false));

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        index.Documents.Add(document);
        using (var stream = File.Create(_scipPath)) index.WriteTo(stream);

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord, indexer: "test", partial);
    }

    private List<(int Born, int? Died)> Lives(string keyEnd)
    {
        using var db = GraphDb.Open(_dbPath);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT l.born_ord, l.died_ord FROM symbol s JOIN symbol_life l ON l.symbol_id = s.id
            WHERE s.key LIKE '%' || $end ORDER BY l.born_ord
            """;
        command.Parameters.AddWithValue("$end", keyEnd);
        var lives = new List<(int, int?)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) lives.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1)));
        return lives;
    }

    [Fact]
    public void A_library_symbol_first_used_in_an_update_comes_alive_then()
    {
        Load(0, partial: false);

        Load(1, partial: true, Clock);

        Assert.Equal([(1, (int?)null)], Lives("Vendor/Clock#"));
    }

    [Fact]
    public void Once_alive_it_is_not_born_again_on_every_update()
    {
        Load(0, partial: false);
        Load(1, partial: true, Clock);
        Load(2, partial: true, Clock);

        Assert.Single(Lives("Vendor/Clock#"));
    }

    [Fact]
    public void It_can_be_found_and_hangs_in_its_namespace()
    {
        Load(0, partial: false);
        Load(1, partial: true, Clock);

        using var db = GraphDb.Open(_dbPath);
        Assert.Contains(Search.Find(db, "Clock"), hit => hit.Display == "Clock");

        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM edge_life e JOIN symbol t ON t.id = e.to_id
            WHERE e.kind = $contains AND t.key LIKE '%Vendor/Clock#' AND e.died_ord IS NULL
            """;
        command.Parameters.AddWithValue("$contains", (int)EdgeKind.Contains);
        Assert.Equal(1L, command.ExecuteScalar());
    }

    /// <summary>Makes the database look as one written before the fix: Clock used from ord 1, with no life.</summary>
    private void AsWrittenBeforeTheFix()
    {
        using var db = new SqliteConnection($"Data Source={_dbPath}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            DELETE FROM symbol_life WHERE symbol_id = (SELECT id FROM symbol WHERE key LIKE '%Vendor/Clock#');
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void A_database_from_before_gives_a_used_symbol_its_life_from_its_first_use()
    {
        Load(0, partial: false);
        Load(1, partial: true, Clock);
        Load(2, partial: true, Clock);
        AsWrittenBeforeTheFix();

        using (GraphDb.Open(_dbPath)) { }

        Assert.Equal([(1, (int?)null)], Lives("Vendor/Clock#"));
    }

    [Fact]
    public void The_repair_dates_a_symbol_from_its_first_use_not_its_latest()
    {
        Load(0, partial: false);
        Load(1, partial: true, Clock);
        Load(2, partial: true, Clock, "Stop>" + Clock);
        AsWrittenBeforeTheFix();

        using (GraphDb.Open(_dbPath)) { }

        Assert.Equal([(1, (int?)null)], Lives("Vendor/Clock#"));
    }

    [Fact]
    public void A_database_from_before_leaves_a_symbol_nothing_uses_any_more_alone()
    {
        // Its only line is gone; nothing says it is still there.
        Load(0, partial: false, Clock);
        Load(1, partial: false);
        AsWrittenBeforeTheFix();

        using (GraphDb.Open(_dbPath)) { }

        Assert.Empty(Lives("Vendor/Clock#"));
    }

    [Fact]
    public void The_repair_runs_once()
    {
        Load(0, partial: false);
        Load(1, partial: true, Clock);
        AsWrittenBeforeTheFix();
        using (GraphDb.Open(_dbPath)) { }

        // Delete it again after the repair: a second open must not repair twice.
        using (var db = new SqliteConnection($"Data Source={_dbPath}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM symbol_life WHERE symbol_id = (SELECT id FROM symbol WHERE key LIKE '%Vendor/Clock#')";
            command.ExecuteNonQuery();
        }

        using (GraphDb.Open(_dbPath)) { }

        Assert.Empty(Lives("Vendor/Clock#"));
    }

    [Fact]
    public void An_update_that_stops_using_it_does_not_kill_it()
    {
        // Another project may still use it, and this update only speaks for its own files.
        Load(0, partial: false);
        Load(1, partial: true, Clock);

        Load(2, partial: true);

        Assert.Equal([(1, (int?)null)], Lives("Vendor/Clock#"));
    }
}
