using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// Some symbols under our package are defined nowhere: the indexer wrote a library class under our
/// name, or old code imported one of our modules by a name no file has any more. A partial update
/// only retires what the re-indexed files defined, so these outlived every use of them - on the
/// map they sat inside our project for good. A Python index always covers the whole project, so
/// there "not seen this time" means "gone".
/// </summary>
public sealed class ReferenceOnlySymbolsTests : IDisposable
{
    private const string Ours = "scip-python python shop 0 ";

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

    /// <summary>src/shop/cli.py defines main(), which uses the given symbols.</summary>
    private void Load(int ord, bool partial, string tool, string prefix, params string[] uses)
    {
        var document = new Document { RelativePath = "src/shop/cli.py", Language = "python" };
        document.Occurrences.Add(At(prefix + "`src.shop.cli`/main().", 0, definition: true));
        var line = 1;
        foreach (var use in uses) document.Occurrences.Add(At(use, line++, definition: false));

        var index = new Scip.Index
        {
            Metadata = new Metadata { ProjectRoot = "file:///shop", ToolInfo = new ToolInfo { Name = tool, Version = "1" } },
        };
        index.Documents.Add(document);
        using (var stream = File.Create(_scipPath)) index.WriteTo(stream);

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord, indexer: tool, partial);
    }

    private bool Alive(string keyEnd)
    {
        using var db = GraphDb.Open(_dbPath);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM symbol s JOIN symbol_life l ON l.symbol_id = s.id
            WHERE s.key LIKE '%' || $end AND l.died_ord IS NULL
            """;
        command.Parameters.AddWithValue("$end", keyEnd);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    [Fact]
    public void A_name_nothing_uses_any_more_leaves_the_map()
    {
        Load(0, partial: false, "scip-python", Ours, Ours + "helpers/tidy().");

        Load(1, partial: true, "scip-python", Ours);

        Assert.False(Alive("helpers/tidy()."));
    }

    [Fact]
    public void A_name_still_used_stays()
    {
        Load(0, partial: false, "scip-python", Ours, Ours + "helpers/tidy().");

        Load(1, partial: true, "scip-python", Ours, Ours + "helpers/tidy().");

        Assert.True(Alive("helpers/tidy()."));
    }

    [Fact]
    public void Our_folders_that_hold_no_definition_themselves_stay()
    {
        // src/ and src/shop/ have no __init__.py - nothing defines them, yet they hold our code.
        Load(0, partial: false, "scip-python", Ours);

        Load(1, partial: true, "scip-python", Ours);

        Assert.True(Alive("`src.shop`/"));
        Assert.True(Alive("`src.shop.cli`/main()."));
    }

    [Fact]
    public void Code_defined_in_a_file_this_index_did_not_cover_stays()
    {
        // Only names defined nowhere are swept. One with a definition belongs to that file,
        // and only a load that covers the file (or finds it deleted) may retire it.
        var orders = new Document { RelativePath = "src/shop/orders.py", Language = "python" };
        orders.Occurrences.Add(At(Ours + "`src.shop.orders`/Order#", 0, definition: true));
        var index = new Scip.Index
        {
            Metadata = new Metadata { ProjectRoot = "file:///shop", ToolInfo = new ToolInfo { Name = "scip-python", Version = "1" } },
        };
        index.Documents.Add(orders);
        using (var stream = File.Create(_scipPath)) index.WriteTo(stream);
        using (var db = GraphDb.Open(_dbPath)) Loader.Load(db, _scipPath, 0, indexer: "scip-python");

        Load(1, partial: true, "scip-python", Ours);

        Assert.True(Alive("`src.shop.orders`/Order#"));
    }

    [Fact]
    public void A_library_name_another_project_still_uses_stays()
    {
        // Two Python projects in one repo, one database. Ours stops using a library name; the
        // other still does. Only names under our own package are ours to sweep.
        const string stdlib = "scip-python python python-stdlib 3.11 ";
        var billing = new Document { RelativePath = "billing/run.py", Language = "python" };
        billing.Occurrences.Add(At("scip-python python billing 0 `billing.run`/main().", 0, definition: true));
        billing.Occurrences.Add(At(stdlib + "datetime/datetime#", 1, definition: false));
        var shop = new Document { RelativePath = "src/shop/cli.py", Language = "python" };
        shop.Occurrences.Add(At(Ours + "`src.shop.cli`/main().", 0, definition: true));
        shop.Occurrences.Add(At(stdlib + "datetime/datetime#", 1, definition: false));
        var index = new Scip.Index
        {
            Metadata = new Metadata { ProjectRoot = "file:///repo", ToolInfo = new ToolInfo { Name = "scip-python", Version = "1" } },
        };
        index.Documents.Add(billing);
        index.Documents.Add(shop);
        using (var stream = File.Create(_scipPath)) index.WriteTo(stream);
        using (var db = GraphDb.Open(_dbPath)) Loader.Load(db, _scipPath, 0, indexer: "scip-python");

        Load(1, partial: true, "scip-python", Ours);

        Assert.True(Alive("datetime/datetime#"));
    }

    [Fact]
    public void An_index_that_may_cover_only_part_of_a_package_leaves_them_alone()
    {
        // Other indexers can load one project of several that share a package name; a name
        // unseen in this one may still be used by the other.
        const string csharp = "scip-dotnet nuget shop 1.0 ";
        Load(0, partial: false, "scip-dotnet", csharp, csharp + "Helpers/Tidy().");

        Load(1, partial: true, "scip-dotnet", csharp);

        Assert.True(Alive("Helpers/Tidy()."));
    }
}
