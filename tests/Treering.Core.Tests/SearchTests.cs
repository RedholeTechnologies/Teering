using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 검색의 순서가 곧 쓸모다. 첫 줄이 찾던 것이어야 한다.
/// </summary>
public sealed class SearchTests : IDisposable
{
    private const string Ours = "scip-dotnet nuget Demo 1.0.0.0 ";
    private const string Theirs = "scip-dotnet nuget Vendor 9.9.9.9 ";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}.db");
    private readonly List<string> _scips = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _scips.Append(_dbPath))
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

    private void Load(int ord, bool withReorder = true)
    {
        var document = new Document { RelativePath = "Shop.cs", Language = "C#" };
        var line = 0;

        // A method named like a type, seen first so it is stored first. Without a rule that
        // puts types ahead, it would come out ahead - the two tie on every other key.
        document.Occurrences.Add(At(Ours + "A/Book#Order().", line++, definition: true));

        foreach (var type in new[] { "A/Order#", "A/OrderBook#", "A/My_Thing#", "A/MyXThing#", "A/Book#" })
        {
            document.Occurrences.Add(At(Ours + type, line++, definition: true));
        }

        if (withReorder) document.Occurrences.Add(At(Ours + "A/Reorder#", line++, definition: true));

        // Someone else's types, seen only as references.
        document.Occurrences.Add(At(Theirs + "V/Order#", line++, definition: false));
        document.Occurrences.Add(At(Theirs + "V/OrderedList#", line++, definition: false));

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        index.Documents.Add(document);

        var scip = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}.scip");
        _scips.Add(scip);
        using (var stream = File.Create(scip))
        {
            index.WriteTo(stream);
        }

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, scip, ord, indexer: "test");
    }

    private List<SearchHit> Find(string query)
    {
        using var db = GraphDb.Open(_dbPath);
        return Search.Find(db, query);
    }

    [Fact]
    public void Exact_first_then_ours_before_theirs_then_prefix_before_anywhere()
    {
        Load(0);

        var hits = Find("order")
            .Select(hit => (hit.Display, hit.Own, hit.Kind))
            .ToList();

        Assert.Equal(
            [
                ("Order", true, SymbolKind.Type),
                ("Order", true, SymbolKind.Method),
                ("Order", false, SymbolKind.Type),
                ("OrderBook", true, SymbolKind.Type),
                ("Reorder", true, SymbolKind.Type),
                ("OrderedList", false, SymbolKind.Type),
            ],
            hits);
    }

    [Fact]
    public void A_hit_says_what_holds_it()
    {
        Load(0);

        var type = Find("OrderBook").First();
        Assert.Equal("A", type.Namespace);
        Assert.Equal("Demo", type.Module);
        Assert.NotNull(type.NamespaceId);
        Assert.NotNull(type.ModuleId);

        // A member is found through the type that has it - that is where the map can take you.
        var method = Find("Order").First(hit => hit.Kind == SymbolKind.Method);
        Assert.Equal("Book", method.Type);
        Assert.NotNull(method.TypeId);
    }

    [Fact]
    public void An_underscore_is_a_letter_not_a_wildcard()
    {
        Load(0);

        var names = Find("my_thing").Select(hit => hit.Display).ToList();

        Assert.Equal(["My_Thing"], names);
    }

    [Fact]
    public void What_is_gone_is_not_found()
    {
        Load(0);
        Load(1, withReorder: false);

        Assert.DoesNotContain(Find("reorder"), hit => hit.Display == "Reorder");
    }

    [Fact]
    public void Nothing_typed_finds_nothing()
    {
        Load(0);

        Assert.Empty(Find("   "));
    }
}
