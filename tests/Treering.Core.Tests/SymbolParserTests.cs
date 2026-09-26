using Treering.Core;

namespace Treering.Core.Tests;

public class SymbolParserTests
{
    // 아래 심볼 문자열은 실제 index.scip 에서 모양을 따 왔고, 이름은 지어낸 것이다.
    private const string Prefix = "scip-dotnet nuget 상점앱 1.0.0.0 ";

    [Theory]
    [InlineData("App/", SymbolKind.Namespace)]
    [InlineData("App/App#", SymbolKind.Type)]
    [InlineData("App/App#_db.", SymbolKind.Term)]
    [InlineData("App/App#IsCorruptLocalDb().", SymbolKind.Method)]
    [InlineData("App/App#IsCorruptLocalDb().(ex)", SymbolKind.Parameter)]
    [InlineData("App/App#`.ctor`().", SymbolKind.Method)]
    public void Kind_comes_from_the_descriptor_suffix(string descriptors, SymbolKind expected)
    {
        Assert.Equal(expected, SymbolParser.Parse(Prefix + descriptors).Kind);
    }

    [Fact]
    public void Method_is_not_mistaken_for_a_term()
    {
        // `().` 는 `.` 로도 끝난다. 순서를 틀리면 모든 메서드가 필드가 된다.
        var method = SymbolParser.Parse(Prefix + "Linq/Enumerable#FirstOrDefault().");
        Assert.Equal(SymbolKind.Method, method.Kind);
    }

    [Fact]
    public void Local_symbols_are_recognised()
    {
        var local = SymbolParser.Parse("local 42");
        Assert.True(local.IsLocal);
        Assert.False(local.HasPackage);
    }

    [Fact]
    public void Version_is_kept_off_the_key()
    {
        // 같은 심볼이 버전만 다르면 같은 키여야 한다. 아니면 릴리스 한 번에 전 심볼이 죽는다.
        var before = SymbolParser.Parse("scip-dotnet nuget 상점앱 1.0.0.0 App/App#");
        var after = SymbolParser.Parse("scip-dotnet nuget 상점앱 1.1.0.0 App/App#");

        Assert.Equal(before.Key, after.Key);
        Assert.NotEqual(before.Version, after.Version);
        Assert.DoesNotContain("1.0.0.0", before.Key);
    }

    [Fact]
    public void Package_is_read_and_empty_packages_are_flagged()
    {
        var ours = SymbolParser.Parse(Prefix + "App/App#");
        Assert.Equal("상점앱", ours.Package);
        Assert.True(ours.HasPackage);

        // --allow-global-symbol-definitions 를 켜도 네임스페이스는 패키지가 비어 있다.
        var bare = SymbolParser.Parse("scip-dotnet nuget . . System/");
        Assert.False(bare.HasPackage);
    }

    [Theory]
    [InlineData("App/App#_db.", "App/App#")]
    [InlineData("App/App#", "App/")]
    [InlineData("App/App#IsCorruptLocalDb().", "App/App#")]
    [InlineData("App/App#IsCorruptLocalDb().(ex)", "App/App#IsCorruptLocalDb().")]
    [InlineData("Data/OrderRepository#Save().", "Data/OrderRepository#")]
    public void Parent_is_one_descriptor_shorter(string descriptors, string expected)
    {
        Assert.Equal(expected, SymbolParser.ParentDescriptors(descriptors));
    }

    [Fact]
    public void Top_level_namespace_has_no_parent()
    {
        Assert.Null(SymbolParser.ParentDescriptors("App/"));
    }

    [Theory]
    [InlineData("App/App#", "App")]
    [InlineData("App/App#_db.", "_db")]
    [InlineData("App/App#IsCorruptLocalDb().", "IsCorruptLocalDb")]
    [InlineData("Linq/Enumerable#FirstOrDefault(+2).", "FirstOrDefault")]
    [InlineData("App/App#IsCorruptLocalDb().(ex)", "ex")]
    [InlineData("App/App#`.ctor`().", ".ctor")]
    public void Display_drops_the_terminator(string descriptors, string expected)
    {
        Assert.Equal(expected, SymbolParser.Display(descriptors));
    }

    [Fact]
    public void A_type_parameter_is_its_own_kind_named_inside_its_brackets_and_belongs_to_its_type()
    {
        var parameter = SymbolParser.Parse(Prefix + "App/Box#[T]");

        Assert.Equal(SymbolKind.TypeParameter, parameter.Kind);
        Assert.Equal("T", SymbolParser.Display(parameter.Descriptors));
        Assert.Equal("App/Box#", SymbolParser.ParentDescriptors(parameter.Descriptors));
    }

    [Fact]
    public void A_type_in_the_global_namespace_hangs_off_the_namespace_with_no_name()
    {
        // Real: scip-dotnet writes the global namespace as `` - an escaped empty name. Top-level
        // statements' Program, and types declared without a namespace, all live there.
        Assert.Equal("``/", SymbolParser.ParentDescriptors("``/TodoItem#"));
        Assert.Equal("", SymbolParser.Display("``/"));
        Assert.Equal("TodoItem", SymbolParser.Display("``/TodoItem#"));
        Assert.Equal(".ctor", SymbolParser.Display("``/TodoItem#`.ctor`()."));
    }

    [Fact]
    public void A_symbol_too_short_to_be_one_is_unknown_rather_than_a_crash()
    {
        Assert.Equal(SymbolKind.Unknown, SymbolParser.Parse("garbage").Kind);
        Assert.Equal(SymbolKind.Unknown, SymbolParser.Parse("").Kind);
    }

    [Theory]
    // What the compiler wrote on the declaration line decides the shape a type is drawn with.
    [InlineData("```cs\npublic sealed class App\n```", "class")]
    [InlineData("```cs\npublic interface IOrderRepository\n```", "interface")]
    [InlineData("```cs\npublic enum EntryKind : byte\n```", "enum")]
    [InlineData("```cs\npublic sealed record Statement(int Id)\n```", "record")]
    [InlineData("```cs\npublic readonly struct Money\n```", "struct")]
    [InlineData("```cs\npublic delegate void Changed(object sender)\n```", "delegate")]
    [InlineData("```python\nclass Order:\n```", "class")]
    public void The_flavour_of_a_type_is_the_keyword_its_declaration_starts_with(string documentation, string expected)
    {
        Assert.Equal(expected, SymbolParser.FlavorOf([documentation]));
    }

    [Fact]
    public void A_keyword_in_a_constraint_does_not_change_what_the_type_is()
    {
        // `where T : struct` names a struct, but Box is a class. The first keyword wins.
        Assert.Equal("class", SymbolParser.FlavorOf(["```cs\npublic sealed class Box<T> where T : struct\n```"]));
    }

    [Fact]
    public void A_type_without_documentation_has_no_flavour()
    {
        // Someone else's type: the index carries no declaration, and guessing from the name is out.
        Assert.Null(SymbolParser.FlavorOf([]));
        Assert.Null(SymbolParser.FlavorOf(["```cs\n```"]));
    }

    [Fact]
    public void The_signature_is_the_declaration_line_without_its_fences()
    {
        Assert.Equal(
            "public static string App.User { get; private set; }",
            SymbolParser.SignatureOf(["```cs\npublic static string App.User { get; private set; }\n```"]));
    }

    [Fact]
    public void The_signature_stops_where_the_xml_comment_begins()
    {
        // After the declaration come the XML docs. A panel showing <summary> as the signature is wrong.
        Assert.Null(SymbolParser.SignatureOf(["```cs\n```", "<summary>Keeps the orders.</summary>"]));
    }

    [Fact]
    public void A_very_long_signature_is_cut_to_three_hundred_characters()
    {
        var line = "public void Run(" + new string('x', 400) + ")";
        Assert.Equal(300, SymbolParser.SignatureOf(["```cs\n" + line + "\n```"])!.Length);
    }
}
