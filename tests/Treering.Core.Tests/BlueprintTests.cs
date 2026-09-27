using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 설계도는 한 층을 상자와 선으로 정리해 «이 코드가 어떻게 짜여 있나» 를 읽히게 한다.
/// 틀리면 도면이 거짓말을 한다: 선의 이름표가 엉뚱한 타입을 가리키거나, 구현이 쓰임으로 보이거나,
/// 비교에서 새로 생긴 모듈이 원래 있던 것처럼 보인다. 여기 적은 것이 도면의 약속이다.
/// </summary>
public sealed class BlueprintTests : IDisposable
{
    private const string Web = "scip-dotnet nuget Web 1.0.0.0 ";
    private const string Shop = "scip-dotnet nuget Shop 1.0.0.0 ";
    private const string Billing = "scip-dotnet nuget Billing 1.0.0.0 ";
    private const string Data = "scip-dotnet nuget Data 1.0.0.0 ";
    private const string Lib = "scip-dotnet nuget Toolkit 9.0.0 ";

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

    /// <summary>
    /// 한 파일. <c>+</c> 로 시작하면 정의, 아니면 참조다. 참조는 바로 앞의 정의에 붙는다.
    /// </summary>
    private static Document File(string path, params string[] occurrences)
    {
        var document = new Document { RelativePath = path, Language = "C#" };
        var line = 0;
        foreach (var occurrence in occurrences)
        {
            var definition = occurrence.StartsWith('+');
            document.Occurrences.Add(At(definition ? occurrence[1..] : occurrence, line++, definition));
        }
        return document;
    }

    private static void Describe(Document document, string symbol, string declaration, string? doc = null, string? implements = null)
    {
        var information = new SymbolInformation { Symbol = symbol };
        information.Documentation.Add("```cs\n" + declaration + "\n```");
        if (doc is not null) information.Documentation.Add(doc);
        if (implements is not null) information.Relationships.Add(new Relationship { Symbol = implements, IsImplementation = true });
        document.Symbols.Add(information);
    }

    /// <summary>
    /// Web 이 Shop 의 Order · Cart 를 쓰고, Shop 의 OrderService 가 결제를 IPaymentGateway 로 맡기며
    /// Data 의 OrderStore 에 저장한다. <paramref name="withBilling"/> 이면 Billing 의 CardGateway 가
    /// 그 인터페이스를 구현하고, 아니면 Web 이 Data 를 직접 부른다(옛 설계).
    /// </summary>
    private void Snapshot(int ord, bool withBilling)
    {
        var page = File("CheckoutPage.cs",
            "+" + Web + "Web/CheckoutPage#",
            "+" + Web + "Web/CheckoutPage#Submit().",
            Shop + "Shop/Orders/Order#", Shop + "Shop/Orders/Cart#",
            Lib + "Toolkit/Json#");
        if (!withBilling) page.Occurrences.Add(At(Data + "Data/OrderStore#", 20, definition: false));

        var service = File("OrderService.cs",
            "+" + Shop + "Shop/Checkout/OrderService#",
            "+" + Shop + "Shop/Checkout/OrderService#gateway.",
            Shop + "Shop/Checkout/IPaymentGateway#",
            "+" + Shop + "Shop/Checkout/OrderService#store.",
            Data + "Data/OrderStore#",
            "+" + Shop + "Shop/Checkout/OrderService#Place().",
            Shop + "Shop/Orders/Order#", Shop + "Shop/Orders/Cart#", Shop + "Shop/Checkout/IPaymentGateway#Charge().",
            Data + "Data/OrderStore#Save().", Lib + "Toolkit/Json#");
        Describe(service, Shop + "Shop/Checkout/OrderService#", "public class OrderService", "Runs one order to the end.");
        Describe(service, Shop + "Shop/Checkout/OrderService#gateway.", "private IPaymentGateway gateway");
        Describe(service, Shop + "Shop/Checkout/OrderService#store.", "private OrderStore store");
        Describe(service, Shop + "Shop/Checkout/OrderService#Place().", "public Order Place(Cart cart)");

        var port = File("IPaymentGateway.cs",
            "+" + Shop + "Shop/Checkout/IPaymentGateway#",
            "+" + Shop + "Shop/Checkout/IPaymentGateway#Charge().");
        Describe(port, Shop + "Shop/Checkout/IPaymentGateway#", "public interface IPaymentGateway", "Hands one payment to someone else.");

        var orders = File("Order.cs",
            "+" + Shop + "Shop/Orders/Order#",
            "+" + Shop + "Shop/Orders/Cart#");
        Describe(orders, Shop + "Shop/Orders/Order#", "public class Order", "What was bought, and for how much.");

        var store = File("OrderStore.cs",
            "+" + Data + "Data/OrderStore#",
            "+" + Data + "Data/OrderStore#Save().");

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///shop" } };
        index.Documents.Add(page);
        index.Documents.Add(service);
        index.Documents.Add(port);
        index.Documents.Add(orders);
        index.Documents.Add(store);

        if (withBilling)
        {
            var card = File("CardGateway.cs",
                "+" + Billing + "Billing/CardGateway#",
                "+" + Billing + "Billing/CardGateway#Charge().",
                Shop + "Shop/Orders/Order#");
            Describe(card, Billing + "Billing/CardGateway#", "public class CardGateway", implements: Shop + "Shop/Checkout/IPaymentGateway#");
            index.Documents.Add(card);
        }

        using (var stream = System.IO.File.Create(_scipPath)) index.WriteTo(stream);
        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord, indexer: "test");
    }

    private BlueprintResult Sheet(string? parentKeyEnd = null, int? at = null, int? from = null, bool ownOnly = false)
    {
        using var db = GraphDb.Open(_dbPath);
        return Blueprint.Of(db, parentKeyEnd is null ? null : Id(db, parentKeyEnd), at, from, ownOnly)!;
    }

    private static long Id(SqliteConnection db, string keyEnd)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id FROM symbol WHERE key LIKE '%' || $end ORDER BY length(key) LIMIT 1";
        command.Parameters.AddWithValue("$end", keyEnd);
        return (long)command.ExecuteScalar()!;
    }

    private static BlueprintBox Box(BlueprintResult sheet, string display) => sheet.Boxes.Single(box => box.Display == display);

    private static BlueprintLink Link(BlueprintResult sheet, string from, string to, EdgeKind kind = EdgeKind.Reference) =>
        sheet.Links.Single(link => link.From == Box(sheet, from).Id && link.To == Box(sheet, to).Id && link.Kind == kind);

    // --- the top sheet -----------------------------------------------------------------

    [Fact]
    public void The_top_sheet_has_a_box_for_each_of_our_modules()
    {
        Snapshot(0, withBilling: true);

        var sheet = Sheet();

        Assert.Equal(BlueprintLevel.Modules, sheet.Level);
        Assert.Equal(["Billing", "Data", "Shop", "Web"], sheet.Boxes.Where(box => !box.Outside).Select(box => box.Display).Order());
    }

    [Fact]
    public void A_library_our_code_uses_stands_outside_and_goes_when_only_our_code_is_asked_for()
    {
        Snapshot(0, withBilling: true);

        Assert.True(Box(Sheet(), "Toolkit").Outside);
        Assert.DoesNotContain(Sheet(ownOnly: true).Boxes, box => box.Display == "Toolkit");
    }

    [Fact]
    public void A_line_is_labelled_with_what_it_uses_most()
    {
        Snapshot(0, withBilling: true);

        var labels = Link(Sheet(), "Web", "Shop").Labels;

        Assert.Equal(["Cart", "Order"], labels);
    }

    [Fact]
    public void Implementing_is_a_line_of_its_own_labelled_with_the_implementer()
    {
        Snapshot(0, withBilling: true);
        var sheet = Sheet();

        var implements = Link(sheet, "Billing", "Shop", EdgeKind.Inherit);

        Assert.Equal(["CardGateway"], implements.Labels);
    }

    [Fact]
    public void A_box_lists_first_the_types_other_boxes_use()
    {
        Snapshot(0, withBilling: true);

        var shop = Box(Sheet(), "Shop");

        // Web uses Order and Cart, and Billing uses Order too: Order is what Shop is used for most.
        Assert.Equal("Order", shop.Items[0].Display);
        Assert.Contains(shop.Items, item => item.Display == "Cart");
    }

    [Fact]
    public void A_module_without_a_doc_comment_borrows_its_face_types_and_says_whose_it_is()
    {
        Snapshot(0, withBilling: true);

        var shop = Box(Sheet(), "Shop");

        Assert.Equal("What was bought, and for how much.", shop.Role);
        Assert.Equal("Order", shop.RoleFrom);
    }

    [Fact]
    public void A_box_with_nothing_to_say_says_nothing_rather_than_something_made_up()
    {
        Snapshot(0, withBilling: true);

        var data = Box(Sheet(), "Data");

        Assert.Null(data.Role);
        Assert.Null(data.RoleFrom);
    }

    // --- inside a module -----------------------------------------------------------------

    [Fact]
    public void Inside_a_module_its_namespaces_are_the_boxes()
    {
        Snapshot(0, withBilling: true);

        var sheet = Sheet("package Shop");

        Assert.Equal(BlueprintLevel.Parts, sheet.Level);
        Assert.Equal(["Shop.Checkout", "Shop.Orders"], sheet.Boxes.Where(box => !box.Outside).Select(box => box.Display).Order());
        Assert.Equal(["Cart", "Order"], Link(sheet, "Shop.Checkout", "Shop.Orders").Labels);
    }

    [Fact]
    public void Inside_a_module_our_other_modules_stand_outside_but_libraries_do_not()
    {
        Snapshot(0, withBilling: true);

        var sheet = Sheet("package Shop");

        Assert.True(Box(sheet, "Billing").Outside);
        Assert.Equal(["CardGateway"], Link(sheet, "Billing", "Shop.Checkout", EdgeKind.Inherit).Labels);
        Assert.DoesNotContain(sheet.Boxes, box => box.Display == "Toolkit");
    }

    [Fact]
    public void An_outside_box_lists_only_what_is_tied_to_here()
    {
        Snapshot(0, withBilling: true);

        var billing = Box(Sheet("package Shop"), "Billing");

        Assert.Equal(["CardGateway"], billing.Items.Select(item => item.Display));
    }

    // --- one type ------------------------------------------------------------------------

    [Fact]
    public void A_type_sheet_lists_fields_before_methods()
    {
        Snapshot(0, withBilling: true);

        var service = Box(Sheet("Shop/Checkout/OrderService#"), "OrderService");

        Assert.Equal(["gateway", "store", "Place"], service.Items.Select(item => item.Display));
        Assert.Equal("method", service.Items[2].Shape);
        Assert.Equal("Runs one order to the end.", service.Role);
    }

    [Fact]
    public void A_type_sheets_lines_are_labelled_with_the_members_that_make_them()
    {
        Snapshot(0, withBilling: true);
        var sheet = Sheet("Shop/Checkout/OrderService#");

        Assert.Equal(["Place()", "gateway"], Link(sheet, "OrderService", "IPaymentGateway").Labels.Order(StringComparer.Ordinal));
        Assert.Equal(["Place()"], Link(sheet, "OrderService", "Order").Labels);
    }

    [Fact]
    public void A_type_sheet_shows_what_implements_it()
    {
        Snapshot(0, withBilling: true);

        var sheet = Sheet("Shop/Checkout/IPaymentGateway#");

        Assert.Contains(sheet.Links, link => link.Kind == EdgeKind.Inherit
            && link.From == Box(sheet, "CardGateway").Id && link.To == Box(sheet, "IPaymentGateway").Id);
    }

    // --- comparing -----------------------------------------------------------------------

    [Fact]
    public void Compared_with_the_old_design_a_new_module_is_new()
    {
        Snapshot(0, withBilling: false);
        Snapshot(1, withBilling: true);

        var sheet = Sheet(at: 1, from: 0);

        Assert.Equal(EdgeState.Born, Box(sheet, "Billing").State);
        Assert.Equal(EdgeState.Same, Box(sheet, "Shop").State);
        Assert.Equal(EdgeState.Born, Link(sheet, "Billing", "Shop", EdgeKind.Inherit).State);
    }

    [Fact]
    public void Compared_with_the_old_design_a_line_that_went_away_is_shown_gone()
    {
        Snapshot(0, withBilling: false);
        Snapshot(1, withBilling: true);

        var sheet = Sheet(at: 1, from: 0);

        Assert.Equal(EdgeState.Died, Link(sheet, "Web", "Data").State);
        Assert.DoesNotContain(Sheet(at: 1).Links, link => link.From == Box(sheet, "Web").Id && link.To == Box(sheet, "Data").Id);
    }
}
