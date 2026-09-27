using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;

namespace Treering.Core;

/// <summary>
/// 가져올 저장소 없이 둘러볼 수 있는 예제 프로젝트 — 작은 온라인 상점(<c>acme-shop</c>).
///
/// 처음 여는 사람에게 필요한 것은 색인기 설치가 아니라 «이게 무엇을 보여 주는가» 다. 그래서 색인기를
/// 돌리지 않고, 이 파일이 SCIP 색인을 직접 짜서 들인다. 모든 이름은 지어낸 것이다.
///
/// 스냅숏이 둘이다. 한 달 전에는 화면이 저장소를 직접 불렀고 결제가 상점 안의 카드 결제 클래스였다.
/// 지금은 결제가 인터페이스 뒤로 빠져 Billing 모듈이 구현하고, 할인이 생겼고, 주문 취소가 생겼다 —
/// 시점 비교가 보여 줄 것이 있다.
/// </summary>
public static class Sample
{
    public const string Name = "acme-shop";

    /// <summary>예제를 (다시) 만들어 들이고 프로젝트로 적는다. 이미 있으면 새로 만든다.</summary>
    public static ProjectInfo Install()
    {
        var repo = Path.Combine(Projects.Home, "samples", Name);
        Directory.CreateDirectory(repo);
        var db = Projects.DbFor(repo);
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);

        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { db, db + "-wal", db + "-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }

        var scip = Path.Combine(Path.GetTempPath(), $"treering-sample-{Guid.NewGuid():N}.scip");
        try
        {
            foreach (var (ord, now) in new[] { (0, false), (1, true) })
            {
                using (var stream = File.Create(scip)) Build(now).WriteTo(stream);
                using var connection = GraphDb.Open(db);
                Loader.Load(connection, scip, ord, indexer: "sample");
            }
        }
        finally
        {
            if (File.Exists(scip)) File.Delete(scip);
        }

        // 옛 스냅숏은 한 달 전의 것으로 적는다. 시간축에서 둘이 같은 순간으로 보이지 않게.
        using (var connection = GraphDb.Open(db))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE snapshot SET indexed_at = $then, commit_sha = 'sample-1' WHERE ord = 0;
                UPDATE snapshot SET commit_sha = 'sample-2' WHERE ord = 1;
                """;
            command.Parameters.AddWithValue("$then", DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }

        return Projects.Record(repo, ["C#"]);
    }

    private static string Type(string package, string ns, string name) =>
        $"scip-dotnet nuget {package} 1.0.0.0 {ns.Replace('.', '/')}/{name}#";

    private const string GuidType = "scip-dotnet nuget System.Runtime 10.0.0 System/Guid#";
    private const string DateTimeType = "scip-dotnet nuget System.Runtime 10.0.0 System/DateTime#";
    private const string JsonType = "scip-dotnet nuget Toolkit.Json 3.1.0 Toolkit/Json/JsonWriter#";

    /// <summary>한 시점의 상점. <paramref name="now"/> 가 아니면 한 달 전.</summary>
    private static Scip.Index Build(bool now)
    {
        var code = new Code();

        var checkoutPage = Type("Acme.Web", "Acme.Web.Pages", "CheckoutPage");
        var cartPage = Type("Acme.Web", "Acme.Web.Pages", "CartPage");
        var ordersApi = Type("Acme.Web", "Acme.Web.Api", "OrdersController");
        var service = Type("Acme.Shop", "Acme.Shop.Checkout", "OrderService");
        var flow = Type("Acme.Shop", "Acme.Shop.Checkout", "CheckoutFlow");
        var gateway = Type("Acme.Shop", "Acme.Shop.Checkout", "IPaymentGateway");
        var cardPayment = Type("Acme.Shop", "Acme.Shop.Checkout", "CardPayment");
        var order = Type("Acme.Shop", "Acme.Shop.Orders", "Order");
        var cart = Type("Acme.Shop", "Acme.Shop.Orders", "Cart");
        var line = Type("Acme.Shop", "Acme.Shop.Orders", "OrderLine");
        var orderStorePort = Type("Acme.Shop", "Acme.Shop.Orders", "IOrderStore");
        var priceRule = Type("Acme.Shop", "Acme.Shop.Pricing", "PriceRule");
        var discount = Type("Acme.Shop", "Acme.Shop.Pricing", "Discount");
        var cardGateway = Type("Acme.Billing", "Acme.Billing", "CardGateway");
        var invoice = Type("Acme.Billing", "Acme.Billing", "Invoice");
        var receipt = Type("Acme.Billing", "Acme.Billing", "Receipt");
        var orderStore = Type("Acme.Data", "Acme.Data", "OrderStore");
        var invoiceStore = Type("Acme.Data", "Acme.Data", "InvoiceStore");
        var tests = Type("Acme.Shop.Tests", "Acme.Shop.Tests", "OrderServiceTests");

        // ---- Acme.Web ------------------------------------------------------------------
        var page = code.File("src/Acme.Web/Pages/CheckoutPage.cs")
            .Def(checkoutPage, 3, "public class CheckoutPage", "The page a buyer pays on.")
            .Def(checkoutPage + "cart.", 6, "private Cart cart").Use(cart, 6)
            .Def(checkoutPage + "OnPost().", 12, "public Task<IActionResult> OnPost()", "Places the order in the cart and shows the receipt.")
            .Use(service + "Place().", 15).Use(order, 16).Use(cart, 17);
        if (!now) page.Use(orderStore + "Save().", 30);

        code.File("src/Acme.Web/Pages/CartPage.cs")
            .Def(cartPage, 3, "public class CartPage", "Shows what is in the cart and what it costs.")
            .Def(cartPage + "OnGet().", 9, "public void OnGet()").Use(cart, 11).Use(priceRule + "Apply().", 12);

        code.File("src/Acme.Web/Api/OrdersController.cs")
            .Def(ordersApi, 5, "public class OrdersController", "Orders over HTTP, for the mobile app.")
            .Def(ordersApi + "Get().", 11, "public ActionResult<Order> Get(Guid id)").Use(GuidType, 11).Use(orderStorePort + "Find().", 13).Use(order, 14)
            .Def(ordersApi + "Post().", 22, "public ActionResult<Order> Post(Cart cart)").Use(cart, 22).Use(service + "Place().", 24).Use(JsonType, 26);

        // ---- Acme.Shop -----------------------------------------------------------------
        var orderService = code.File("src/Acme.Shop/Checkout/OrderService.cs")
            .Def(service, 4, "public class OrderService", "Runs one order from cart to receipt.")
            .Def(service + "gateway.", 7, now ? "private IPaymentGateway gateway" : "private CardPayment gateway")
            .Use(now ? gateway : cardPayment, 7)
            .Def(service + "store.", 8, "private IOrderStore store").Use(orderStorePort, 8)
            .Def(service + "Place().", 14, "public Order Place(Cart cart)", "Prices the cart, takes the money and keeps the order.")
            .Use(cart, 14).Use(order, 16).Use(priceRule + "Apply().", 18)
            .Use(now ? gateway + "Charge()." : cardPayment + "Charge().", 22).Use(orderStorePort + "Save().", 26);
        if (now)
        {
            orderService.Def(service + "Cancel().", 48, "public void Cancel(Guid orderId)", "Gives the money back and marks the order cancelled.")
                .Use(GuidType, 48).Use(orderStorePort + "Find().", 50).Use(gateway + "Refund().", 53);
        }

        code.File("src/Acme.Shop/Checkout/CheckoutFlow.cs")
            .Def(flow, 3, "public class CheckoutFlow", "The steps a buyer goes through, in order.")
            .Def(flow + "Next().", 10, "public Step Next(Cart cart)").Use(cart, 10).Use(service + "Place().", 17);

        if (now)
        {
            code.File("src/Acme.Shop/Checkout/IPaymentGateway.cs")
                .Def(gateway, 3, "public interface IPaymentGateway", "Hands one payment to whoever takes the money.")
                .Def(gateway + "Charge().", 6, "Receipt Charge(Order order)").Use(order, 6)
                .Def(gateway + "Refund().", 9, "void Refund(Order order)").Use(order, 9);
        }
        else
        {
            code.File("src/Acme.Shop/Checkout/CardPayment.cs")
                .Def(cardPayment, 3, "public class CardPayment", "Takes card payments.")
                .Def(cardPayment + "Charge().", 8, "public void Charge(Order order)").Use(order, 8).Use(JsonType, 15);
        }

        code.File("src/Acme.Shop/Orders/Order.cs")
            .Def(order, 3, "public record Order", "What was bought, for how much, and when.")
            .Def(order + "Id.", 5, "public Guid Id { get; }").Use(GuidType, 5)
            .Def(order + "Lines.", 6, "public IReadOnlyList<OrderLine> Lines { get; }").Use(line, 6)
            .Def(order + "PlacedAt.", 7, "public DateTime PlacedAt { get; }").Use(DateTimeType, 7)
            .Def(order + "Total.", 9, "public decimal Total { get; }");

        code.File("src/Acme.Shop/Orders/Cart.cs")
            .Def(cart, 3, "public class Cart", "What a buyer means to buy.")
            .Def(cart + "Items.", 6, "public List<OrderLine> Items { get; }").Use(line, 6)
            .Def(cart + "Add().", 10, "public void Add(OrderLine line)").Use(line, 10);

        code.File("src/Acme.Shop/Orders/OrderLine.cs")
            .Def(line, 3, "public record OrderLine", "One product, how many, at what price.")
            .Def(line + "Quantity.", 5, "public int Quantity { get; }")
            .Def(line + "Price.", 6, "public decimal Price { get; }");

        code.File("src/Acme.Shop/Orders/IOrderStore.cs")
            .Def(orderStorePort, 3, "public interface IOrderStore", "Keeps orders and finds them again.")
            .Def(orderStorePort + "Save().", 5, "void Save(Order order)").Use(order, 5)
            .Def(orderStorePort + "Find().", 6, "Order Find(Guid id)").Use(GuidType, 6).Use(order, 6);

        var rules = code.File("src/Acme.Shop/Pricing/PriceRule.cs")
            .Def(priceRule, 3, "public class PriceRule", "Works out what a cart costs.")
            .Def(priceRule + "Apply().", 8, "public decimal Apply(Cart cart)").Use(cart, 8).Use(line, 10);
        if (now) rules.Use(discount, 14);

        if (now)
        {
            code.File("src/Acme.Shop/Pricing/Discount.cs")
                .Def(discount, 3, "public record Discount", "Money off, by code or by amount.")
                .Def(discount + "Code.", 5, "public string Code { get; }");
        }

        // ---- Acme.Billing (new this month) ------------------------------------------------
        if (now)
        {
            code.File("src/Acme.Billing/CardGateway.cs")
                .Def(cardGateway, 4, "public class CardGateway", "Charges cards through the payment provider.", implements: gateway)
                .Def(cardGateway + "Charge().", 9, "public Receipt Charge(Order order)").Use(order, 9).Use(invoice, 12).Use(receipt, 16).Use(JsonType, 18)
                .Def(cardGateway + "Refund().", 30, "public void Refund(Order order)").Use(order, 30).Use(JsonType, 33);
            code.File("src/Acme.Billing/Invoice.cs")
                .Def(invoice, 3, "public record Invoice", "The bill for one order.")
                .Def(invoice + "Order.", 5, "public Order Order { get; }").Use(order, 5);
            code.File("src/Acme.Billing/Receipt.cs")
                .Def(receipt, 3, "public record Receipt", "Proof that the money arrived.")
                .Def(receipt + "PaidAt.", 5, "public DateTime PaidAt { get; }").Use(DateTimeType, 5);
        }

        // ---- Acme.Data -------------------------------------------------------------------
        code.File("src/Acme.Data/OrderStore.cs")
            .Def(orderStore, 4, "public class OrderStore", "Orders in the database.", implements: orderStorePort)
            .Def(orderStore + "Save().", 9, "public void Save(Order order)").Use(order, 9)
            .Def(orderStore + "Find().", 20, "public Order Find(Guid id)").Use(GuidType, 20).Use(order, 22);
        if (now)
        {
            code.File("src/Acme.Data/InvoiceStore.cs")
                .Def(invoiceStore, 4, "public class InvoiceStore", "Invoices in the database.")
                .Def(invoiceStore + "Save().", 8, "public void Save(Invoice invoice)").Use(invoice, 8);
        }

        // ---- Acme.Shop.Tests -------------------------------------------------------------
        var suite = code.File("tests/Acme.Shop.Tests/OrderServiceTests.cs")
            .Def(tests, 3, "public class OrderServiceTests")
            .Def(tests + "Places_an_order().", 8, "public void Places_an_order()").Use(service + "Place().", 11).Use(cart, 10).Use(order, 12);
        if (now) suite.Def(tests + "Cancels_and_refunds().", 20, "public void Cancels_and_refunds()").Use(service + "Cancel().", 23);

        return code.Index;
    }

    /// <summary>색인을 짜는 도구. 참조는 바로 앞의 정의에 붙으므로 줄 순서대로 적는다.</summary>
    private sealed class Code
    {
        public Scip.Index Index { get; } = new() { Metadata = new Metadata { ProjectRoot = "file:///acme-shop" } };

        public Source File(string path)
        {
            var document = new Document { RelativePath = path, Language = "C#" };
            Index.Documents.Add(document);
            return new Source(document);
        }
    }

    private sealed class Source(Document document)
    {
        public Source Def(string symbol, int line, string declaration, string? doc = null, string? implements = null)
        {
            document.Occurrences.Add(At(symbol, line, definition: true));
            var information = new SymbolInformation { Symbol = symbol };
            information.Documentation.Add("```cs\n" + declaration + "\n```");
            if (doc is not null) information.Documentation.Add(doc);
            if (implements is not null) information.Relationships.Add(new Relationship { Symbol = implements, IsImplementation = true });
            document.Symbols.Add(information);
            return this;
        }

        public Source Use(string symbol, int line)
        {
            document.Occurrences.Add(At(symbol, line, definition: false));
            return this;
        }

        private static Occurrence At(string symbol, int line, bool definition)
        {
            var occurrence = new Occurrence { Symbol = symbol, SymbolRoles = definition ? (int)SymbolRole.Definition : 0 };
#pragma warning disable CS0612, CS0618
            occurrence.Range.Add(line);
            occurrence.Range.Add(4);
            occurrence.Range.Add(12);
#pragma warning restore CS0612, CS0618
            return occurrence;
        }
    }
}
