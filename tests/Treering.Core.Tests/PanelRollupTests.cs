using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// The panel of a namespace or a module says who calls into it and what it calls, rolled up to
/// the same level. Rolled up to the wrong level, a module's panel lists the types inside it
/// calling each other, or says nothing calls it when a whole other module does.
/// </summary>
public sealed class PanelRollupTests : IDisposable
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

    private const string Core = "scip-dotnet nuget Core 1.0.0.0 ";
    private const string Web = "scip-dotnet nuget Web 1.0.0.0 ";

    /// <summary>
    /// Core: Orders/Repo calls Orders/Money (inside one namespace) and Reports/Summary (another).
    /// Web: Pages/Home calls Orders/Repo.
    /// </summary>
    private SqliteConnection Load()
    {
        var repo = new Document { RelativePath = "Repo.cs", Language = "C#" };
        repo.Occurrences.Add(At(Core + "Orders/Repo#", 0, definition: true));
        repo.Occurrences.Add(At(Core + "Orders/Money#", 1, definition: false));
        repo.Occurrences.Add(At(Core + "Reports/Summary#", 2, definition: false));

        var money = new Document { RelativePath = "Money.cs", Language = "C#" };
        money.Occurrences.Add(At(Core + "Orders/Money#", 0, definition: true));

        var summary = new Document { RelativePath = "Summary.cs", Language = "C#" };
        summary.Occurrences.Add(At(Core + "Reports/Summary#", 0, definition: true));

        var home = new Document { RelativePath = "Home.cs", Language = "C#" };
        home.Occurrences.Add(At(Web + "Pages/Home#", 0, definition: true));
        home.Occurrences.Add(At(Core + "Orders/Repo#", 1, definition: false));

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        foreach (var document in new[] { repo, money, summary, home }) index.Documents.Add(document);
        using (var stream = File.Create(_scipPath)) index.WriteTo(stream);

        var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord: 0, indexer: "test");
        return db;
    }

    // Keys are stored without the version, so the same symbol keeps its id across releases.
    private static string Key(string symbol) => string.Join(' ', symbol.Split(' ') is var p ? [p[0], p[1], p[2], .. p[4..]] : []);

    private static long Id(SqliteConnection db, string symbol, string column = "id")
    {
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT {column} FROM symbol WHERE key = $key";
        command.Parameters.AddWithValue("$key", Key(symbol));
        return (long)command.ExecuteScalar()!;
    }

    private static List<string> Names(IReadOnlyList<RelatedType> related) =>
        related.Select(item => item.Display).OrderBy(name => name).ToList();

    [Fact]
    public void A_namespace_is_called_by_the_namespaces_that_call_into_it()
    {
        using var db = Load();
        var orders = SymbolDetail.Of(db, Id(db, Core + "Orders/"))!;

        Assert.Equal(["Pages"], Names(orders.Callers));
    }

    [Fact]
    public void A_namespace_calls_other_namespaces_and_not_itself()
    {
        // Repo calling Money is inside Orders - that is not Orders calling anything.
        using var db = Load();
        var orders = SymbolDetail.Of(db, Id(db, Core + "Orders/"))!;

        Assert.Equal(["Reports"], Names(orders.Calls));
    }

    [Fact]
    public void A_module_is_called_by_other_modules_and_its_own_insides_do_not_count()
    {
        using var db = Load();
        var core = SymbolDetail.Of(db, Id(db, Core + "Orders/Repo#", "module_id"))!;

        Assert.Equal(["Web"], Names(core.Callers));
        Assert.Empty(core.Calls);
    }
}
