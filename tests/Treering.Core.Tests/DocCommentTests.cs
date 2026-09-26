using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// The panel reads a symbol's doc comment. Each indexer hands it over in its own shape: scip-dotnet
/// as raw XML, scip-python as the docstring with its indentation, scip-typescript as the JSDoc text.
/// Read wrongly, the panel shows XML tags, loses the paragraph that mattered, or prints the
/// declaration twice.
/// </summary>
public sealed class DocCommentTests : IDisposable
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

    // What scip-dotnet writes, down to the indentation.
    private const string CSharp = """
        <member name="M:Acme.Shop.Order.Total(System.Decimal)">
            <summary>
            Sum of the lines, with <paramref name="tax"/> added.
            Rounds <b>once</b>, at the end.

            Uses <see cref="T:Acme.Shop.Money"/> and <see cref="F:Acme.Shop.Rounding.Bankers"/>,
            never <c>double</c>; an empty order is <see langword="null"/>.
            </summary>
            <param name="tax">The rate, 0.1 for ten percent.</param>
            <returns>The total &lt;= the limit.</returns>
        </member>

        """;

    [Fact]
    public void The_declaration_is_not_repeated_as_the_comment()
    {
        Assert.Equal("An order.", DocComment.Of(["```cs\nclass Order\n```", "<member name=\"T:Acme.Order\"><summary>An order.</summary></member>"]));
    }

    [Fact]
    public void A_symbol_with_only_a_declaration_has_no_comment()
    {
        Assert.Null(DocComment.Of(["```python\nbuiltins.int\n```"]));
    }

    [Fact]
    public void A_csharp_summary_reads_as_paragraphs_not_as_the_editors_line_breaks()
    {
        var doc = DocComment.Of(["```cs\ndecimal Total(decimal tax)\n```", CSharp])!;

        Assert.StartsWith("Sum of the lines, with `tax` added. Rounds **once**, at the end.\n\n", doc);
    }

    [Fact]
    public void Csharp_references_read_as_short_names()
    {
        var doc = DocComment.Of(["```cs\ndecimal Total(decimal tax)\n```", CSharp])!;

        Assert.Contains("Uses `Money` and `Rounding.Bankers`, never `double`; an empty order is `null`.", doc);
    }

    [Fact]
    public void Csharp_parameters_and_return_follow_the_summary_and_escapes_are_undone()
    {
        var doc = DocComment.Of(["```cs\ndecimal Total(decimal tax)\n```", CSharp])!;

        Assert.EndsWith("\n\n`tax` — The rate, 0.1 for ten percent.\n→ The total <= the limit.", doc);
    }

    [Fact]
    public void Broken_csharp_xml_still_gives_its_text()
    {
        var doc = DocComment.Of(["```cs\nclass Order\n```", "<member><summary>An order & its lines</summary>"]);

        Assert.Equal("An order & its lines", doc);
    }

    [Fact]
    public void A_python_docstring_loses_its_indent_but_keeps_its_sections()
    {
        var docstring = "Sum of the lines, with tax.\n\n        Args:\n            tax: the rate.\n        ";

        Assert.Equal(
            "Sum of the lines, with tax.\n\nArgs:\n    tax: the rate.",
            DocComment.Of(["```python\ndef total(self, tax):\n```", docstring]));
    }

    [Fact]
    public void A_python_module_docstring_is_read()
    {
        Assert.Equal("Orders and what they cost.", DocComment.Of(["(module) shop.orders", "Orders and what they cost."]));
    }

    [Fact]
    public void A_typescript_comment_is_read_as_written()
    {
        Assert.Equal(
            "An order someone placed.\nHolds the lines and the total.",
            DocComment.Of(["```ts\nclass Order\n```", "An order someone placed.\nHolds the lines and the total."]));
    }

    [Fact]
    public void A_very_long_comment_is_cut_and_says_so()
    {
        var doc = DocComment.Of(["```ts\nclass Order\n```", new string('x', DocComment.MaxLength + 500)])!;

        Assert.Equal(DocComment.MaxLength + 1, doc.Length);
        Assert.EndsWith("…", doc);
    }

    // --- The rest of the XML doc tags: each one either reads as prose or is dropped, never shown raw. ---

    private static string? Xml(string summary) => DocComment.Of(["```cs\nclass Order\n```", $"<member name=\"T:Acme.Shop.Order\"><summary>{summary}</summary></member>"]);

    [Fact]
    public void A_code_block_stands_as_its_own_paragraph()
    {
        Assert.Equal("Use it like this:\n\n`var total = order.Total();`\n\nThen save.", Xml("Use it like this:<code>var total = order.Total();</code>Then save."));
    }

    [Fact]
    public void A_para_starts_a_new_paragraph()
    {
        Assert.Equal("One order.\n\nMany lines.", Xml("One order.<para>Many lines.</para>"));
    }

    [Fact]
    public void A_line_break_the_author_wrote_is_kept()
    {
        // The editor's own wrapping is joined; a <br/> is a break the author asked for.
        Assert.Equal("Paid\nShipped", Xml("Paid<br/>Shipped"));
    }

    [Fact]
    public void A_line_break_leaves_no_spaces_around_it()
    {
        Assert.Equal("Paid\nShipped", Xml("Paid <br/> Shipped"));
    }

    [Fact]
    public void A_line_break_at_the_end_of_a_paragraph_leaves_no_blank_line()
    {
        Assert.Equal("Paid\n\nShipped", Xml("<br/>Paid<br/><para>Shipped<br/></para>"));
    }

    [Fact]
    public void A_list_reads_as_items()
    {
        Assert.Equal("States:\n\n- Paid\n- Shipped", Xml("States:<list type=\"bullet\"><item>Paid</item><item>Shipped</item></list>"));
    }

    [Fact]
    public void An_unknown_tag_keeps_its_words()
    {
        Assert.Equal("Totals are final once paid.", Xml("Totals are <em>final</em> once <i>paid</i>."));
    }

    [Fact]
    public void A_link_reads_as_its_text_or_its_address()
    {
        Assert.Equal("See the guide.", Xml("See <see href=\"https://example.com/guide\">the guide</see>."));
        Assert.Equal("See https://example.com/guide.", Xml("See <see href=\"https://example.com/guide\"/>."));
    }

    [Fact]
    public void Reference_names_are_short_whatever_kind_they_are()
    {
        Assert.Equal("`Order.Total` builds a `Order`, a `Box` and a `Order`.",
            Xml("<see cref=\"M:Acme.Shop.Order.Total(System.Decimal)\"/> builds a <see cref=\"M:Acme.Shop.Order.#ctor\"/>, " +
                "a <see cref=\"T:Acme.Shop.Box`1\"/> and a <see cref=\"Acme.Shop.Order\"/>."));
    }

    [Fact]
    public void An_empty_code_tag_leaves_no_ticks()
    {
        Assert.Equal("Nothing here.", Xml("Nothing<c></c> here."));
    }

    [Fact]
    public void A_summary_without_its_member_wrapper_is_read_too()
    {
        Assert.Equal("An order.", DocComment.Of(["```cs\nclass Order\n```", "<summary>An order.</summary>"]));
    }

    [Fact]
    public void A_docstring_that_opens_on_a_new_line_loses_the_blank_lines_around_it()
    {
        // The most common Python style: """ on its own line, text below, """ on its own line.
        Assert.Equal("Sum of the lines.\n\nRounds once.", DocComment.Of(["```python\ndef total():\n```", "\n    Sum of the lines.\n\n    Rounds once.\n    "]));
    }

    [Fact]
    public void A_blank_comment_has_no_summary()
    {
        Assert.Null(DocComment.Summary("   \n  "));
    }

    [Fact]
    public void The_summary_is_the_first_paragraph_on_one_line()
    {
        Assert.Equal("Sum of the lines, with tax.", DocComment.Summary("Sum of the lines,\nwith tax.\n\nArgs:\n    tax: the rate."));
        Assert.Null(DocComment.Summary(null));
    }

    private static Occurrence Definition(string symbol, int line)
    {
        var occurrence = new Occurrence { Symbol = symbol, SymbolRoles = (int)SymbolRole.Definition };
#pragma warning disable CS0612, CS0618
        occurrence.Range.Add(line);
        occurrence.Range.Add(0);
        occurrence.Range.Add(3);
#pragma warning restore CS0612, CS0618
        return occurrence;
    }

    private void Load(int ord, string? typeDoc, string? methodDoc)
    {
        const string type = "scip-typescript npm shop 1.0.0 src/`order.ts`/Order#";
        const string method = "scip-typescript npm shop 1.0.0 src/`order.ts`/Order#total().";

        var document = new Document { RelativePath = "src/order.ts", Language = "typescript" };
        document.Occurrences.Add(Definition(type, 0));
        document.Occurrences.Add(Definition(method, 1));
        foreach (var (symbol, signature, doc) in new[] { (type, "class Order", typeDoc), (method, "(method) total(): number", methodDoc) })
        {
            var information = new SymbolInformation { Symbol = symbol };
            information.Documentation.Add("```ts\n" + signature + "\n```");
            if (doc is not null) information.Documentation.Add(doc);
            document.Symbols.Add(information);
        }

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///shop" } };
        index.Documents.Add(document);
        using (var stream = File.Create(_scipPath)) index.WriteTo(stream);

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord, indexer: "test");
    }

    private SymbolDetailResult OrderDetail()
    {
        using var db = GraphDb.Open(_dbPath);
        var id = Search.Find(db, "Order").Single(hit => hit.Display == "Order").Id;
        return SymbolDetail.Of(db, id)!;
    }

    [Fact]
    public void The_panel_gets_the_comment_and_each_member_its_first_paragraph()
    {
        Load(0, "An order someone placed.", "Sum of the lines.\n\nRounds once.");

        var detail = OrderDetail();

        Assert.Equal("An order someone placed.", detail.Doc);
        Assert.Equal("Sum of the lines.", detail.Members.Single(member => member.Display == "total").Summary);
    }

    [Fact]
    public void An_edited_comment_replaces_the_old_one()
    {
        Load(0, "An order someone placed.", null);
        Load(1, "An order, paid or not.", null);

        Assert.Equal("An order, paid or not.", OrderDetail().Doc);
    }

    [Fact]
    public void A_database_made_before_comments_were_kept_opens_and_takes_them()
    {
        using (var old = new SqliteConnection($"Data Source={_dbPath}"))
        {
            old.Open();
            using var command = old.CreateCommand();
            command.CommandText = "CREATE TABLE symbol (id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE, kind INTEGER NOT NULL, package_id INTEGER, display TEXT NOT NULL, container_id INTEGER, type_id INTEGER, namespace_id INTEGER, module_id INTEGER, flavor TEXT, signature TEXT, own INTEGER NOT NULL DEFAULT 0)";
            command.ExecuteNonQuery();
        }

        Load(0, "An order someone placed.", null);

        Assert.Equal("An order someone placed.", OrderDetail().Doc);
    }
}
