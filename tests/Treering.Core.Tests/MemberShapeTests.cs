using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// The side panel groups a type's members as properties, events, fields and methods. SCIP ends
/// properties and fields alike with <c>.</c>, so the group comes from the declaration the compiler
/// wrote. Get it wrong and a property is listed as a field, or an event as a nested type.
/// </summary>
public sealed class MemberShapeTests
{
    private static string ShapeOf(SymbolKind kind, string? signature) =>
        new MemberInfo(1, "x", kind, signature).Shape;

    [Fact]
    public void A_declaration_with_a_getter_is_a_property()
    {
        Assert.Equal("property", ShapeOf(SymbolKind.Term, "public static string App.User { get; private set; }"));
    }

    [Fact]
    public void A_declaration_without_a_getter_is_a_field()
    {
        Assert.Equal("field", ShapeOf(SymbolKind.Term, "private ShopDbContext? App._db"));
    }

    [Fact]
    public void An_event_is_an_event_with_or_without_an_access_modifier()
    {
        // Interface members carry no modifier, so the declaration starts with "event".
        Assert.Equal("event", ShapeOf(SymbolKind.Term, "public event EventHandler Changed"));
        Assert.Equal("event", ShapeOf(SymbolKind.Term, "event EventHandler Changed"));
    }

    [Fact]
    public void An_event_is_not_mistaken_for_a_type_even_when_its_symbol_looks_like_one()
    {
        Assert.Equal("event", ShapeOf(SymbolKind.Type, "public event Action Saved"));
    }

    [Fact]
    public void A_name_that_merely_contains_event_is_not_an_event()
    {
        Assert.Equal("property", ShapeOf(SymbolKind.Term, "public EventLog App.Log { get; }"));
        Assert.Equal("field", ShapeOf(SymbolKind.Term, "private int App.eventCount"));
    }

    [Fact]
    public void A_method_is_a_method()
    {
        Assert.Equal("method", ShapeOf(SymbolKind.Method, "public void App.Run()"));
    }

    [Fact]
    public void A_member_the_index_did_not_describe_is_just_a_member()
    {
        // No declaration: someone else's member. Calling it a field would be a guess.
        Assert.Equal("member", ShapeOf(SymbolKind.Term, null));
    }
}
