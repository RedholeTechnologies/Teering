using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// The legend can switch test code off. What counts as a test decides what disappears: miss a
/// test and it stays on the map; take product code for a test and switching tests off hides the
/// code the user came to see. Tests are told apart by where they live, the way each language
/// lays them out, never by a word merely appearing in a name.
/// </summary>
public sealed class TestCodeTests : IDisposable
{
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

    // --- Which files and projects are tests ---

    [Theory]
    [InlineData("tests/test_orders.py")]
    [InlineData("test_orders.py")]
    [InlineData("src/shop/orders_test.py")]
    [InlineData("conftest.py")]
    [InlineData(@"tests\unit\orders.py")]
    [InlineData("src/cart.test.ts")]
    [InlineData("src/cart.spec.tsx")]
    [InlineData("web/src/__tests__/cart.ts")]
    [InlineData("Shop.Tests/OrderTests.cs")]
    public void A_file_where_tests_live_is_a_test(string path)
    {
        Assert.True(TestCode.IsTestPath(path));
    }

    [Theory]
    [InlineData("src/shop/orders.py")]
    [InlineData("src/latest.py")]
    [InlineData("src/attestation/proof.py")]
    [InlineData("src/contest.ts")]
    [InlineData("src/spec/models.ts")]
    [InlineData("src/testing_tools.py")]
    [InlineData("Shop/OrderTests.cs")]
    public void A_file_that_only_mentions_test_in_its_name_is_not(string path)
    {
        // "spec/" is left out on purpose: it holds specifications and models as often as tests.
        // A C# file named *Tests.cs outside a test project is product code as far as its place goes.
        Assert.False(TestCode.IsTestPath(path));
    }

    [Theory]
    [InlineData("Shop.Tests", true)]
    [InlineData("Shop.Test", true)]
    [InlineData("Shop.UnitTests", true)]
    [InlineData("Shop.IntegrationTests", true)]
    [InlineData("Shop.Testing", false)]
    [InlineData("Contests", false)]
    [InlineData("Shop", false)]
    public void A_csharp_test_project_is_known_by_its_assembly_name(string assembly, bool test)
    {
        Assert.Equal(test, TestCode.IsTestAssembly(assembly));
    }

    // --- Marking what was loaded ---

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

    private static Document File(string path, params (string Symbol, bool Definition)[] occurrences)
    {
        var document = new Document { RelativePath = path, Language = "C#" };
        var line = 0;
        foreach (var (symbol, definition) in occurrences) document.Occurrences.Add(At(symbol, line++, definition));
        return document;
    }

    private void Load(params Document[] documents)
    {
        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///shop" } };
        index.Documents.AddRange(documents);
        using (var stream = System.IO.File.Create(_scipPath)) index.WriteTo(stream);
        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, 0, indexer: "test");
    }

    private bool IsTest(string keyEnd)
    {
        using var db = GraphDb.Open(_dbPath);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT test FROM symbol WHERE key LIKE '%' || $end";
        command.Parameters.AddWithValue("$end", keyEnd);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private const string Shop = "scip-dotnet nuget Shop 1.0.0.0 ";
    private const string ShopTests = "scip-dotnet nuget Shop.Tests 1.0.0.0 ";

    /// <summary>A product assembly and its test assembly; the tests use the product and a library.</summary>
    private void CSharpSolution() => Load(
        File("Order.cs", (Shop + "Shop/Order#", true)),
        File("OrderTests.cs", (ShopTests + "Shop.Tests/OrderTests#", true), (ShopTests + "Shop.Tests/OrderTests#Totals().", true),
            (Shop + "Shop/Order#", false), ("scip-dotnet nuget xunit 2.0.0 Xunit/Assert#", false)));

    [Fact]
    public void Everything_in_a_test_project_is_test_code()
    {
        CSharpSolution();

        Assert.True(IsTest("Shop.Tests/OrderTests#"));
        Assert.True(IsTest("Shop.Tests/OrderTests#Totals()."));
        Assert.True(IsTest("package Shop.Tests"));
    }

    [Fact]
    public void Product_code_used_by_tests_stays_product_code()
    {
        CSharpSolution();

        Assert.False(IsTest("Shop/Order#"));
        Assert.False(IsTest("package Shop"));
    }

    [Fact]
    public void A_library_is_never_test_code_even_when_only_tests_use_it()
    {
        CSharpSolution();

        Assert.False(IsTest("Xunit/Assert#"));
    }

    [Fact]
    public void A_folder_is_test_code_only_when_everything_in_it_is()
    {
        // tests/ holds only tests; the project holds both, so switching tests off must not take it.
        const string py = "scip-python python shop 0 ";
        Load(
            File("src/shop/orders.py", (py + "`src.shop.orders`/__init__:", true), (py + "`src.shop.orders`/Order#", true)),
            File("tests/test_orders.py", (py + "`tests.test_orders`/__init__:", true), (py + "`tests.test_orders`/test_total().", true)));

        Assert.True(IsTest("`tests.test_orders`/test_total()."));
        Assert.True(IsTest("`tests.test_orders`/"));
        Assert.True(IsTest("python shop tests/"));
        Assert.False(IsTest("`src.shop.orders`/Order#"));
        Assert.False(IsTest("python shop src/"));
        Assert.False(IsTest("package shop"));
    }

    [Fact]
    public void A_type_split_between_product_and_test_files_is_product_code()
    {
        Load(
            File("Order.cs", (Shop + "Shop/Order#", true)),
            File("tests/OrderParts.cs", (Shop + "Shop/Order#", true)));

        Assert.False(IsTest("Shop/Order#"));
    }

    [Fact]
    public void Code_moved_out_of_the_tests_is_no_longer_a_test()
    {
        Load(File("tests/Helper.cs", (Shop + "Shop/Helper#", true)));
        Assert.True(IsTest("Shop/Helper#"));

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///shop" } };
        index.Documents.Add(File("src/Helper.cs", (Shop + "Shop/Helper#", true)));
        using (var stream = System.IO.File.Create(_scipPath)) index.WriteTo(stream);
        using (var db = GraphDb.Open(_dbPath)) Loader.Load(db, _scipPath, 1, indexer: "test");

        Assert.False(IsTest("Shop/Helper#"));
    }

    [Fact]
    public void The_map_and_the_tree_say_which_nodes_are_tests()
    {
        CSharpSolution();
        using var db = GraphDb.Open(_dbPath);

        var map = Subgraph.Extract(db, new SubgraphRequest { Seeds = Subgraph.AllAt(db, Granularity.Type), Granularity = Granularity.Type, WholeLevel = true });
        Assert.True(map.Nodes.Single(node => node.Display == "OrderTests").Test);
        Assert.False(map.Nodes.Single(node => node.Display == "Order").Test);

        var modules = Tree.Children(db, null, ownOnly: true).Nodes;
        Assert.True(modules.Single(node => node.Display == "Shop.Tests").Test);
        Assert.False(modules.Single(node => node.Display == "Shop").Test);
    }

    [Fact]
    public void A_database_from_before_tests_were_marked_marks_them_when_opened()
    {
        CSharpSolution();
        using (var old = new SqliteConnection($"Data Source={_dbPath}"))
        {
            old.Open();
            using var command = old.CreateCommand();
            command.CommandText = "ALTER TABLE symbol DROP COLUMN test;";
            command.ExecuteNonQuery();
        }

        Assert.True(IsTest("Shop.Tests/OrderTests#"));
        Assert.False(IsTest("Shop/Order#"));
    }
}
