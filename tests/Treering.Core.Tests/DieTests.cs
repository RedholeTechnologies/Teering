using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 칩 평면도에서 영역의 넓이는 코드의 양이다. 색인에 파일 길이가 없어서 정의가 적힌 줄로 어림한다.
/// 어림이 틀리면 작은 모듈이 칩의 절반을 차지하고, 무거운 곳이 어디인지를 거꾸로 말한다.
/// </summary>
public sealed class DieTests : IDisposable
{
    private const string Shop = "scip-dotnet nuget Shop 1.0.0.0 ";
    private const string Data = "scip-dotnet nuget Data 1.0.0.0 ";

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

    /// <summary>한 파일. (심볼, 정의된 줄, 정의인가)</summary>
    private static Document File(string path, params (string Symbol, int Line, bool Definition)[] occurrences)
    {
        var document = new Document { RelativePath = path, Language = "C#" };
        foreach (var (symbol, line, definition) in occurrences) document.Occurrences.Add(At(symbol, line, definition));
        return document;
    }

    /// <summary>
    /// Shop.Checkout 의 OrderService 는 31줄짜리 파일 하나, Shop.Orders 의 Order 는 15줄짜리 파일 하나.
    /// Data 는 한 줄. Mixed.cs 는 20줄에 A 가 한 번, B 가 두 번 정의돼 있다. OrderService 는 Order 와
    /// Store 를 쓴다 — 설계도에 선이 있어야 상자가 남는다.
    /// </summary>
    private DieResult Load(long? parent = null)
    {
        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///shop" } };
        index.Documents.Add(File("Checkout/OrderService.cs",
            (Shop + "Shop/Checkout/OrderService#", 0, true),
            (Shop + "Shop/Checkout/OrderService#Place().", 30, true),
            (Shop + "Shop/Orders/Order#", 31, false),
            (Data + "Data/Store#", 32, false)));
        index.Documents.Add(File("Orders/Order.cs",
            (Shop + "Shop/Orders/Order#", 0, true),
            (Shop + "Shop/Orders/Order#Total().", 14, true)));
        index.Documents.Add(File("Store.cs", (Data + "Data/Store#", 0, true)));
        index.Documents.Add(File("Checkout/Cards/CardPay.cs", (Shop + "Shop/Checkout/Cards/CardPay#", 8, true)));
        index.Documents.Add(File("Mixed.cs",
            (Shop + "Shop/Orders/A#", 0, true),
            (Shop + "Shop/Orders/B#", 5, true),
            (Shop + "Shop/Orders/B#Run().", 19, true)));

        using (var stream = System.IO.File.Create(_scipPath)) index.WriteTo(stream);
        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, 0, indexer: "test");
        return Die.Of(db, parent is null ? null : parent)!;
    }

    private long Id(string keyEnd)
    {
        using var db = GraphDb.Open(_dbPath);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id FROM symbol WHERE key LIKE '%' || $end ORDER BY length(key) LIMIT 1";
        command.Parameters.AddWithValue("$end", keyEnd);
        return (long)command.ExecuteScalar()!;
    }

    private DieResult Inside(string keyEnd)
    {
        using var db = GraphDb.Open(_dbPath);
        return Die.Of(db, Id(keyEnd))!;
    }

    [Fact]
    public void A_module_is_as_big_as_the_files_its_code_is_written_in()
    {
        var top = Load();

        var shop = top.Sheet.Boxes.Single(box => box.Display == "Shop");
        var data = top.Sheet.Boxes.Single(box => box.Display == "Data");

        // A file runs to its last definition: OrderService.cs 31 lines (the references after it do not
        // count), Order.cs 15, Mixed.cs 20.
        Assert.Equal(31 + 15 + 20 + 9, top.Lines[shop.Id], precision: 6);
        Assert.Equal(1, top.Lines[data.Id], precision: 6);
    }

    [Fact]
    public void A_module_holds_its_namespaces_as_blocks_the_biggest_first()
    {
        var top = Load();
        var shop = top.Sheet.Boxes.Single(box => box.Display == "Shop");

        var blocks = top.Blocks[shop.Id];

        Assert.Equal(["Shop.Checkout", "Shop.Orders"], blocks.Select(block => block.Display));
        Assert.Equal(31 + 9, blocks[0].Lines, precision: 6);
        Assert.Equal(15 + 20, blocks[1].Lines, precision: 6);
    }

    [Fact]
    public void Types_written_in_one_file_share_it_by_how_much_of_it_they_define()
    {
        Load();

        var orders = Inside("Shop/Orders/");

        // Mixed.cs is 20 lines; A is one definition of three in it, B two of three.
        Assert.Equal(15, orders.Lines[Id("Shop/Orders/Order#")], precision: 6);
        Assert.Equal(20.0 / 3, orders.Lines[Id("Shop/Orders/A#")], precision: 6);
        Assert.Equal(40.0 / 3, orders.Lines[Id("Shop/Orders/B#")], precision: 6);
    }

    [Fact]
    public void A_namespace_counts_everything_under_it()
    {
        Load();

        var inside = Inside("package Shop");
        var checkout = inside.Sheet.Boxes.Single(box => box.Display == "Shop.Checkout");

        // Its own OrderService (31 lines) and the Cards namespace inside it (9).
        Assert.Equal(31 + 9, inside.Lines[checkout.Id], precision: 6);
    }
}
