using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// scip-python names a module in a src layout two ways: by its import name when something
/// imports it, by its file path when nothing does. Left alone, one folder splits into two
/// branches in the map ("shop" and "src.shop"), and a file's contents die and are reborn under
/// the other name the day someone first imports it. The map names modules by their folders.
/// </summary>
public sealed class PythonModuleNamesTests : IDisposable
{
    private const string Ours = "scip-python python shop 0 ";

    private readonly string _scipPath = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}.scip");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _scipPath, _dbPath })
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
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

    private static Document File(string path, params string[] definitions)
    {
        var document = new Document { RelativePath = path, Language = "python" };
        var line = 0;
        foreach (var symbol in definitions) document.Occurrences.Add(At(symbol, line++, definition: true));
        return document;
    }

    /// <summary>src/shop/orders.py is imported somewhere; src/shop/cli.py is not.</summary>
    private static Document[] SrcLayout() =>
    [
        File(@"src\shop\orders.py", Ours + "`shop.orders`/__init__:", Ours + "`shop.orders`/Order#"),
        File(@"src\shop\cli.py", Ours + "`src.shop.cli`/__init__:", Ours + "`src.shop.cli`/main()."),
    ];

    [Fact]
    public void A_module_named_by_its_import_name_is_renamed_to_its_folder()
    {
        var names = PythonModuleNames.Learn(SrcLayout());

        Assert.Equal(Ours + "`src.shop.orders`/Order#", names.Canonical(Ours + "`shop.orders`/Order#"));
    }

    [Fact]
    public void A_package_itself_is_renamed_as_well()
    {
        // "shop/" is src/shop/__init__.py - a single name, written without backticks.
        var names = PythonModuleNames.Learn(SrcLayout());

        Assert.Equal(Ours + "`src.shop`/__init__:", names.Canonical(Ours + "shop/__init__:"));
    }

    [Fact]
    public void A_package_known_only_by_its_init_file_is_renamed()
    {
        // Only "import shop" is used anywhere, so src/shop/__init__.py is the only evidence.
        var names = PythonModuleNames.Learn(
        [
            File(@"src\shop\__init__.py", Ours + "shop/__init__:"),
            File(@"src\shop\cli.py", Ours + "`src.shop.cli`/main()."),
        ]);

        Assert.Equal(Ours + "`src.shop`/VERSION.", names.Canonical(Ours + "shop/VERSION."));
    }

    [Fact]
    public void A_module_already_named_by_its_folder_is_left_as_is()
    {
        var names = PythonModuleNames.Learn(SrcLayout());

        Assert.Equal(Ours + "`src.shop.cli`/main().", names.Canonical(Ours + "`src.shop.cli`/main()."));
    }

    [Fact]
    public void A_library_of_the_same_name_is_not_renamed()
    {
        // Our src/shop is "shop"; a library called shop is someone else's shop.
        var names = PythonModuleNames.Learn(SrcLayout());
        var library = "scip-python python shop-client 2.1 `shop.orders`/Order#";

        Assert.Equal(library, names.Canonical(library));
    }

    [Fact]
    public void A_real_top_level_folder_of_that_name_leaves_every_name_alone()
    {
        // With both shop/ and src/shop/ on disk, "shop.x" may mean either. Guessing would move
        // code into the wrong folder; scip-python's own names are the lesser evil.
        var names = PythonModuleNames.Learn([.. SrcLayout(), File("shop/legacy.py", Ours + "`shop.legacy`/Old#")]);

        Assert.Equal(Ours + "`shop.orders`/Order#", names.Canonical(Ours + "`shop.orders`/Order#"));
    }

    [Fact]
    public void Evidence_pointing_at_two_folders_leaves_every_name_alone()
    {
        var names = PythonModuleNames.Learn(
        [
            File("src/shop/orders.py", Ours + "`shop.orders`/Order#"),
            File("lib/shop/cart.py", Ours + "`shop.cart`/Cart#"),
        ]);

        Assert.Equal(Ours + "`shop.orders`/Order#", names.Canonical(Ours + "`shop.orders`/Order#"));
    }

    // --- A library class reached through a type is written under our package name. ---

    private static Occurrence Use(string symbol) => At(symbol, 5, definition: false);

    /// <summary>Our code, using a library both ways: a function (named right) and a class (named as ours).</summary>
    private static Document[] UsesALibrary(params string[] more)
    {
        var cli = File(@"src\shop\cli.py", Ours + "`src.shop.cli`/main().");
        cli.Occurrences.Add(Use("scip-python python tablekit 2.0 `tablekit.io`/load()."));
        cli.Occurrences.Add(Use(Ours + "`tablekit.frame`/Frame#"));
        foreach (var symbol in more) cli.Occurrences.Add(Use(symbol));
        return [cli];
    }

    [Fact]
    public void A_library_class_written_under_our_name_goes_back_to_its_library()
    {
        var names = PythonModuleNames.Learn(UsesALibrary());

        Assert.Equal("scip-python python tablekit 2.0 `tablekit.frame`/Frame#", names.Canonical(Ours + "`tablekit.frame`/Frame#"));
    }

    [Fact]
    public void A_standard_library_class_goes_back_to_the_standard_library()
    {
        // A one-part module name is written without backticks.
        var names = PythonModuleNames.Learn(UsesALibrary("scip-python python python-stdlib 3.11 datetime/timedelta#"));

        Assert.Equal("scip-python python python-stdlib 3.11 datetime/datetime#", names.Canonical(Ours + "datetime/datetime#"));
    }

    [Fact]
    public void Our_own_modules_are_never_moved_to_a_library()
    {
        // A library that happens to ship a module under our top name does not get our code.
        var names = PythonModuleNames.Learn(
            [.. UsesALibrary("scip-python python shop-client 1.0 `shop.api`/Client#"), File(@"shop\orders.py", Ours + "`shop.orders`/Order#")]);

        Assert.Equal(Ours + "`shop.orders`/Order#", names.Canonical(Ours + "`shop.orders`/Order#"));
    }

    [Fact]
    public void Where_two_libraries_share_a_name_only_the_longer_match_decides()
    {
        var names = PythonModuleNames.Learn(UsesALibrary(
            "scip-python python cloud-auth 1.0 `cloud.auth.tokens`/fetch().",
            "scip-python python cloud-store 3.0 `cloud.store`/put()."));

        Assert.Equal("scip-python python cloud-auth 1.0 `cloud.auth.creds`/Creds#", names.Canonical(Ours + "`cloud.auth.creds`/Creds#"));
        Assert.Equal(Ours + "`cloud.queue`/Queue#", names.Canonical(Ours + "`cloud.queue`/Queue#"));
    }

    [Fact]
    public void A_symbol_the_indexer_already_filed_under_a_library_keeps_that_library()
    {
        // Only what was written under our name is suspect. A stub or add-on package that defines
        // names in the same module stays where the indexer put it.
        var names = PythonModuleNames.Learn(UsesALibrary());

        Assert.Equal(
            "scip-python python tablekit-stubs 0.1 `tablekit.frame`/Frame#",
            names.Canonical("scip-python python tablekit-stubs 0.1 `tablekit.frame`/Frame#"));
    }

    [Fact]
    public void A_module_no_library_claims_is_left_as_written()
    {
        var names = PythonModuleNames.Learn(UsesALibrary());

        Assert.Equal(Ours + "`gridlib.core`/Grid#", names.Canonical(Ours + "`gridlib.core`/Grid#"));
    }

    [Fact]
    public void A_library_the_index_never_names_is_found_in_the_installed_list()
    {
        // Only its classes are used, so every symbol of it came out under our name - there is no
        // evidence in the index. The list of what is installed says whose files those are.
        var names = PythonModuleNames.Learn(UsesALibrary(), [("gridlib", "4.0", ["gridlib/__init__.py", "gridlib/core.py", "gridlib/py.typed"])]);

        Assert.Equal("scip-python python gridlib 4.0 `gridlib.core`/Grid#", names.Canonical(Ours + "`gridlib.core`/Grid#"));
    }

    // --- A relative import inside a library loses the front of its name. ---

    private static readonly string[] Stdlib = ["asyncio", "asyncio.windows_events", "http.client", "xmlrpc.client"];
    private const string StdlibUse = "scip-python python python-stdlib 3.11 asyncio/run().";

    [Fact]
    public void A_module_that_lost_its_package_is_found_by_how_its_name_ends()
    {
        // "from .windows_events import *" in the standard library's asyncio, written as our windows_events.
        var names = PythonModuleNames.Learn(UsesALibrary(StdlibUse), stdlib: Stdlib);

        Assert.Equal(
            "scip-python python python-stdlib 3.11 `asyncio.windows_events`/__init__:",
            names.Canonical(Ours + "windows_events/__init__:"));
    }

    [Fact]
    public void An_ending_two_modules_share_moves_nothing()
    {
        var names = PythonModuleNames.Learn(UsesALibrary(StdlibUse), stdlib: Stdlib);

        Assert.Equal(Ours + "client/__init__:", names.Canonical(Ours + "client/__init__:"));
    }

    [Fact]
    public void Without_a_standard_library_version_in_the_index_nothing_moves_there()
    {
        var names = PythonModuleNames.Learn(UsesALibrary(), stdlib: Stdlib);

        Assert.Equal(Ours + "windows_events/__init__:", names.Canonical(Ours + "windows_events/__init__:"));
    }

    [Fact]
    public void A_library_module_that_lost_its_package_is_found_in_the_installed_list()
    {
        var names = PythonModuleNames.Learn(UsesALibrary(), [("gridlib", "4.0", ["gridlib/core/frame.py"])]);

        Assert.Equal("scip-python python gridlib 4.0 `gridlib.core.frame`/Frame#", names.Canonical(Ours + "`core.frame`/Frame#"));
    }

    [Fact]
    public void Our_own_installed_copy_does_not_turn_a_stray_name_into_our_code()
    {
        // The project installed into its own environment lists shop/orders.py. A name "orders"
        // left by deleted code must not become a use of our real Order.
        var names = PythonModuleNames.Learn(UsesALibrary(), [("shop", "0", ["shop/orders.py"])]);

        Assert.Equal(Ours + "orders/Order#", names.Canonical(Ours + "orders/Order#"));
    }

    [Fact]
    public void The_standard_library_list_is_read_from_the_indexers_type_stubs()
    {
        var typeshed = Path.Combine(Path.GetTempPath(), $"tr-typeshed-{Guid.NewGuid():N}");
        try
        {
            foreach (var file in new[] { "asyncio/__init__.pyi", "asyncio/windows_events.pyi", "os/path.pyi", "VERSIONS" })
            {
                var path = Path.Combine(typeshed, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllText(path, string.Empty);
            }

            Assert.Equal(["asyncio", "asyncio.windows_events", "os.path"], PythonIndexing.StdlibModules(typeshed).Order());
        }
        finally
        {
            Directory.Delete(typeshed, recursive: true);
        }
    }

    [Fact]
    public void Loaded_a_standard_library_module_that_lost_its_package_goes_back()
    {
        if (!PythonIndexing.StdlibModules().Contains("asyncio.windows_events")) return;   // scip-python not installed here

        var index = new Scip.Index
        {
            Metadata = new Metadata { ProjectRoot = "file:///shop", ToolInfo = new ToolInfo { Name = "scip-python", Version = "0.6.6" } },
        };
        index.Documents.AddRange(UsesALibrary(StdlibUse, Ours + "windows_events/__init__:"));
        using (var stream = System.IO.File.Create(_scipPath)) index.WriteTo(stream);

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, 0, indexer: "scip-python 0.6.6");

        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbol WHERE key LIKE '%windows_events%' AND key NOT LIKE 'scip-python python python-stdlib `asyncio.windows_events`%'";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Loaded_the_installed_list_beside_the_index_is_read()
    {
        var index = new Scip.Index
        {
            Metadata = new Metadata { ProjectRoot = "file:///shop", ToolInfo = new ToolInfo { Name = "scip-python", Version = "0.6.6" } },
        };
        index.Documents.AddRange(UsesALibrary(Ours + "`gridlib.core`/Grid#"));
        using (var stream = System.IO.File.Create(_scipPath)) index.WriteTo(stream);
        System.IO.File.WriteAllText(_scipPath + ".packages.json", """[{"name": "gridlib", "version": "4.0", "files": ["gridlib/core.py"]}]""");

        try
        {
            using var db = GraphDb.Open(_dbPath);
            Loader.Load(db, _scipPath, 0, indexer: "scip-python 0.6.6");

            using var command = db.CreateCommand();
            command.CommandText = "SELECT p.name FROM symbol s JOIN package p ON p.id = s.package_id WHERE s.display = 'Grid'";
            Assert.Equal("gridlib", command.ExecuteScalar());
        }
        finally
        {
            System.IO.File.Delete(_scipPath + ".packages.json");
        }
    }

    [Fact]
    public void A_missing_or_broken_installed_list_is_no_list()
    {
        var file = _scipPath + ".broken.json";
        System.IO.File.WriteAllText(file, "{ not json");
        try
        {
            Assert.Null(PythonIndexing.ReadPackages(file));
            Assert.Null(PythonIndexing.ReadPackages(_scipPath + ".absent.json"));
        }
        finally
        {
            System.IO.File.Delete(file);
        }
    }

    [Fact]
    public void Loaded_a_library_class_is_not_filed_inside_our_project()
    {
        var index = new Scip.Index
        {
            Metadata = new Metadata { ProjectRoot = "file:///shop", ToolInfo = new ToolInfo { Name = "scip-python", Version = "0.6.6" } },
        };
        index.Documents.AddRange(UsesALibrary());
        using (var stream = System.IO.File.Create(_scipPath)) index.WriteTo(stream);

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, 0, indexer: "scip-python 0.6.6");

        using var command = db.CreateCommand();
        command.CommandText = "SELECT p.name FROM symbol s JOIN package p ON p.id = s.package_id WHERE s.display = 'Frame'";
        Assert.Equal("tablekit", command.ExecuteScalar());
    }

    [Fact]
    public void Loaded_the_folder_is_one_branch_and_a_use_across_the_two_names_still_connects()
    {
        var cli = File(@"src\shop\cli.py", Ours + "`src.shop.cli`/__init__:", Ours + "`src.shop.cli`/main().");
        cli.Occurrences.Add(At(Ours + "`shop.orders`/Order#", 1, definition: false));   // main() uses Order
        var index = new Scip.Index
        {
            Metadata = new Metadata { ProjectRoot = "file:///shop", ToolInfo = new ToolInfo { Name = "scip-python", Version = "0.6.6" } },
        };
        index.Documents.Add(File(@"src\shop\orders.py", Ours + "`shop.orders`/__init__:", Ours + "`shop.orders`/Order#"));
        index.Documents.Add(cli);
        using (var stream = System.IO.File.Create(_scipPath)) index.WriteTo(stream);

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, 0, indexer: "scip-python 0.6.6");

        using var modules = db.CreateCommand();
        modules.CommandText = "SELECT key FROM symbol WHERE kind = 2 AND key LIKE $ours";
        modules.Parameters.AddWithValue("$ours", Ours.Replace(" 0 ", " ") + "%");
        var keys = new List<string>();
        using (var reader = modules.ExecuteReader()) while (reader.Read()) keys.Add(reader.GetString(0));
        Assert.Contains(keys, key => key.Contains("`src.shop.orders`"));
        Assert.DoesNotContain(keys, key => key.Contains("`shop.orders`"));

        using var uses = db.CreateCommand();
        uses.CommandText = """
            SELECT COUNT(*) FROM edge_life e
            JOIN symbol f ON f.id = e.from_id JOIN symbol t ON t.id = e.to_id
            WHERE e.kind = $reference AND f.key LIKE '%`src.shop.cli`/main().' AND t.key LIKE '%`src.shop.orders`/Order#'
            """;
        uses.Parameters.AddWithValue("$reference", (int)EdgeKind.Reference);
        Assert.Equal(1L, uses.ExecuteScalar());
    }
}
