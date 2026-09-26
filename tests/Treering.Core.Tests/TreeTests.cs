using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 계층도는 담는 사슬을 나무로 읽는다. 순서, 외줄 합치기, 경로, 그리고 나무 위에 겹칠
/// 호출선이 바르게 나오는지 본다.
/// </summary>
public sealed class TreeTests : IDisposable
{
    private const string Ours = "scip-dotnet nuget Demo 1.0.0.0 ";
    private const string Theirs = "scip-dotnet nuget Vendor 9.9.9.9 ";

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
        var occurrence = new Occurrence
        {
            Symbol = symbol,
            SymbolRoles = definition ? (int)SymbolRole.Definition : 0,
        };
#pragma warning disable CS0612, CS0618
        occurrence.Range.Add(line);
        occurrence.Range.Add(0);
        occurrence.Range.Add(3);
#pragma warning restore CS0612, CS0618
        return occurrence;
    }

    /// <summary>
    /// Demo:  Acme/Shop/Cart (Add, Total) · Acme/Shop/Order · Acme/Billing/Invoice
    ///        Deep/Er/Est/Lone          - a namespace chain with one type at the end
    /// Cart calls Invoice and a Vendor type; Order calls Cart.
    /// </summary>
    private void Load()
    {
        var cart = new Document { RelativePath = "Cart.cs", Language = "C#" };
        cart.Occurrences.Add(At(Ours + "Acme/Shop/Cart#", 0, definition: true));
        cart.Occurrences.Add(At(Ours + "Acme/Shop/Cart#Add().", 1, definition: true));
        cart.Occurrences.Add(At(Ours + "Acme/Billing/Invoice#", 2, definition: false));
        cart.Occurrences.Add(At(Theirs + "V/Client#", 3, definition: false));
        cart.Occurrences.Add(At(Ours + "Acme/Shop/Cart#Total.", 4, definition: true));

        var order = new Document { RelativePath = "Order.cs", Language = "C#" };
        order.Occurrences.Add(At(Ours + "Acme/Shop/Order#", 0, definition: true));
        order.Occurrences.Add(At(Ours + "Acme/Shop/Cart#", 1, definition: false));

        var invoice = new Document { RelativePath = "Invoice.cs", Language = "C#" };
        invoice.Occurrences.Add(At(Ours + "Acme/Billing/Invoice#", 0, definition: true));

        var lone = new Document { RelativePath = "Lone.cs", Language = "C#" };
        lone.Occurrences.Add(At(Ours + "Deep/Er/Est/Lone#", 0, definition: true));

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        index.Documents.Add(cart);
        index.Documents.Add(order);
        index.Documents.Add(invoice);
        index.Documents.Add(lone);

        using (var stream = File.Create(_scipPath))
        {
            index.WriteTo(stream);
        }

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord: 0, indexer: "test");
    }

    private static long Id(SqliteConnection db, string keySuffix)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id FROM symbol WHERE key LIKE '%' || $suffix";
        command.Parameters.AddWithValue("$suffix", keySuffix);
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void Our_modules_come_first_and_theirs_can_be_left_out()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        var all = Tree.Children(db, null, ownOnly: false).Nodes.Select(node => node.Display).ToList();
        Assert.Equal(["Demo", "Vendor"], all);

        var ours = Tree.Children(db, null, ownOnly: true).Nodes.Select(node => node.Display).ToList();
        Assert.Equal(["Demo"], ours);
    }

    [Fact]
    public void A_namespace_chain_with_one_way_down_is_one_step()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        var demo = Tree.Children(db, null, ownOnly: false).Nodes.Single(node => node.Display == "Demo");
        var top = Tree.Children(db, demo.Id, ownOnly: false).Nodes;

        // Acme branches in two, so it stays as it is. Deep/Er/Est goes one way, so it is one step.
        Assert.Equal(["Acme", "Deep.Er.Est"], top.Select(node => node.Display).ToList());

        var deep = top.Single(node => node.Display == "Deep.Er.Est");
        Assert.Equal(3, deep.Merged.Count);
        Assert.Equal(Id(db, "Deep/Er/Est/"), deep.Id);
        Assert.Equal(["Lone"], Tree.Children(db, deep.Id, ownOnly: false).Nodes.Select(node => node.Display).ToList());
    }

    [Fact]
    public void Python_modules_nest_along_their_dots()
    {
        // scip-python writes a whole module as one piece - `src.shop.orders`/ - so without
        // help every module would hang straight off the package, side by side.
        const string python = "scip-python python worker 0 ";
        var file = new Document { RelativePath = "all.py", Language = "python" };
        file.Occurrences.Add(At(python + "`src.shop.orders.invoice`/Invoice#", 0, definition: true));
        file.Occurrences.Add(At(python + "`src.shop`/Db#", 1, definition: true));
        file.Occurrences.Add(At(python + "`src.text.format`/Chart#", 2, definition: true));
        file.Occurrences.Add(At(python + "cli/run().", 3, definition: true));
        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///worker" } };
        index.Documents.Add(file);
        using (var stream = File.Create(_scipPath)) index.WriteTo(stream);

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord: 0, indexer: "test");

        var worker = Assert.Single(Tree.Children(db, null, ownOnly: false).Nodes);
        var top = Tree.Children(db, worker.Id, ownOnly: false).Nodes;
        Assert.Equal(["cli", "src"], top.Select(node => node.Display).ToList());

        // Under a parent the parent's name is dropped, and a one-way branch is still one step.
        var source = top.Single(node => node.Display == "src");
        var inSource = Tree.Children(db, source.Id, ownOnly: false).Nodes;
        Assert.Equal(["shop", "text.format"], inSource.Select(node => node.Display).ToList());

        var shop = inSource.Single(node => node.Display == "shop");
        Assert.Equal(["orders.invoice", "Db"], Tree.Children(db, shop.Id, ownOnly: false).Nodes.Select(node => node.Display).ToList());

        // The parent that no file defined gets the key scip-python would give it.
        Assert.Equal(Id(db, "`src.shop.orders`/"), Tree.Children(db, shop.Id, ownOnly: false).Nodes[0].Merged[0]);
    }

    [Fact]
    public void A_member_in_the_tree_says_what_it_is()
    {
        // The tree draws a function and a value differently. It needs to be told which is which.
        Load();
        using var db = GraphDb.Open(_dbPath);

        var members = Tree.Children(db, Id(db, "Acme/Shop/Cart#"), ownOnly: false).Nodes.ToDictionary(node => node.Display);

        Assert.Equal("method", members["Add"].Shape);
        Assert.Equal("member", members["Total"].Shape);   // no declaration in the index: not guessed
    }

    [Fact]
    public void Namespaces_and_types_carry_no_member_shape()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);
        var demo = Tree.Children(db, null, ownOnly: false).Nodes.Single(node => node.Display == "Demo");

        Assert.All(Tree.Children(db, demo.Id, ownOnly: false).Nodes, node => Assert.Null(node.Shape));
    }

    [Fact]
    public void Inside_a_type_members_follow_in_kind_order()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        var shop = Tree.Children(db, Id(db, "Acme/Shop/"), ownOnly: false).Nodes;
        Assert.Equal(["Cart", "Order"], shop.Select(node => node.Display).ToList());
        Assert.Equal(2, shop.Single(node => node.Display == "Cart").ChildCount);

        var cart = Tree.Children(db, Id(db, "Acme/Shop/Cart#"), ownOnly: false).Nodes;
        Assert.Equal([SymbolKind.Method, SymbolKind.Term], cart.Select(node => node.Kind).ToList());
    }

    [Fact]
    public void The_path_runs_from_the_module_down_to_the_thing()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        var path = Tree.Path(db, Id(db, "Acme/Shop/Cart#Add()."));

        Assert.Equal(
            [Id(db, "package Demo"), Id(db, "nuget Demo Acme/"), Id(db, "Acme/Shop/"), Id(db, "Acme/Shop/Cart#"), Id(db, "Cart#Add().")],
            path);
    }

    [Fact]
    public void Links_say_what_a_node_calls_and_how_to_find_it_in_the_tree()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        var links = Tree.Links(db, Id(db, "Acme/Shop/Cart#"));

        var invoice = Assert.Single(links, link => link.Other == Id(db, "Acme/Billing/Invoice#"));
        Assert.True(invoice.Outgoing);
        Assert.Equal(
            [Id(db, "Acme/Billing/Invoice#"), Id(db, "Acme/Billing/"), Id(db, "nuget Demo Acme/"), Id(db, "package Demo")],
            invoice.Chain);

        // Order calls Cart: that comes back as incoming.
        Assert.Contains(links, link => link.Other == Id(db, "Acme/Shop/Order#") && !link.Outgoing);

        // A member stands for its type.
        Assert.Equal(links.Count, Tree.Links(db, Id(db, "Cart#Add().")).Count);
    }

    [Fact]
    public void What_runs_inside_a_branch_is_not_a_link_out_of_it()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        // Cart -> Invoice and Order -> Cart both stay inside Acme. Only the Vendor call leaves.
        var links = Tree.Links(db, Id(db, "nuget Demo Acme/"));

        var only = Assert.Single(links);
        Assert.Equal(Id(db, "V/Client#"), only.Other);
    }
}
