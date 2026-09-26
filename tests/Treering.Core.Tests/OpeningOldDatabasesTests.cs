using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// A database written by an older Treering has to open and read right, without a re-import:
/// re-importing throws the history away. Get this wrong and "our code only" shows nothing, or
/// Compare shows an empty past, and nothing says why.
/// </summary>
public sealed class OpeningOldDatabasesTests : IDisposable
{
    private const string Ours = "scip-dotnet nuget Shop 1.0.0.0 ";
    private const string Vendor = "scip-dotnet nuget Vendor 2.0.0.0 ";

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

    /// <summary>Sales/Cart uses the given types; Sales/Cart, Billing/Invoice are ours, Vendor/Clock is not.</summary>
    private void Snapshot(int ord, params string[] uses)
    {
        var cart = new Document { RelativePath = "Cart.cs", Language = "C#" };
        cart.Occurrences.Add(At(Ours + "Sales/Cart#", 0, definition: true));
        var line = 1;
        foreach (var use in uses) cart.Occurrences.Add(At(use, line++, definition: false));
        var invoice = new Document { RelativePath = "Invoice.cs", Language = "C#" };
        invoice.Occurrences.Add(At(Ours + "Billing/Invoice#", 0, definition: true));

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///shop" } };
        index.Documents.Add(cart);
        index.Documents.Add(invoice);
        using (var stream = File.Create(_scipPath)) index.WriteTo(stream);

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord, indexer: "test");
    }

    private void Raw(string sql)
    {
        using var db = new SqliteConnection($"Data Source={_dbPath}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> Rows(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "-" : reader.GetValue(i).ToString())));
        return rows;
    }

    [Fact]
    public void A_database_from_before_our_code_was_marked_marks_it_when_opened()
    {
        Snapshot(0, Vendor + "Vendor/Clock#");
        Raw("DROP INDEX IF EXISTS sym_own; ALTER TABLE symbol DROP COLUMN own;");

        using var db = GraphDb.Open(_dbPath);

        Assert.Contains(Search.Find(db, "Cart"), hit => hit.Display == "Cart" && hit.Own);
        Assert.Contains(Search.Find(db, "Clock"), hit => hit.Display == "Clock" && !hit.Own);
    }

    [Fact]
    public void A_database_from_before_lines_were_rolled_up_rolls_every_snapshot_when_opened()
    {
        Snapshot(0, Ours + "Billing/Invoice#");
        Snapshot(1, Vendor + "Vendor/Clock#");
        List<string> rolled;
        using (var db = GraphDb.Open(_dbPath)) rolled = Rows(db, "SELECT level, from_id, to_id, kind, weight, born_ord, died_ord FROM edge_roll ORDER BY 1, 2, 3, 4, 6");
        Assert.NotEmpty(rolled);
        Raw("DELETE FROM edge_roll; PRAGMA user_version = 0;");

        using (var db = GraphDb.Open(_dbPath))
        {
            // The same history, snapshot by snapshot - not only the last one.
            Assert.Equal(rolled, Rows(db, "SELECT level, from_id, to_id, kind, weight, born_ord, died_ord FROM edge_roll ORDER BY 1, 2, 3, 4, 6"));
        }
    }

    [Fact]
    public void A_current_database_is_not_rolled_again_on_every_open()
    {
        Snapshot(0, Ours + "Billing/Invoice#");
        Raw("DELETE FROM edge_roll;");

        using var db = GraphDb.Open(_dbPath);

        Assert.Empty(Rows(db, "SELECT * FROM edge_roll"));
    }
}
