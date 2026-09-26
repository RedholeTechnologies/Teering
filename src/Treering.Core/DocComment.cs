using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Treering.Core;

/// <summary>
/// 심볼에 붙은 문서 주석을 사람이 읽을 글로 만든다.
///
/// 색인기는 <c>documentation</c> 첫 칸에 선언을, 그다음 칸에 주석을 담는다. 모양은 언어마다 다르다 —
/// scip-dotnet 은 XML 원문(<c>&lt;member&gt;&lt;summary&gt;…</c>), scip-python 은 독스트링을
/// 들여쓰기째로, scip-typescript 는 JSDoc 본문만(<c>@param</c> 은 빠진 채) 준다.
/// <c>#</c> · <c>//</c> 로 쓴 보통 주석은 SCIP 에 들어오지 않는다.
///
/// 결과는 가벼운 표기만 쓴다 — 코드는 <c>`x`</c>, 강조는 <c>**x**</c>, 문단은 빈 줄.
/// 어느 말로 된 이름표도 넣지 않는다. 이름표는 화면이 붙인다.
/// </summary>
public static partial class DocComment
{
    /// <summary>화면 하나에 넉넉한 길이. 더 긴 주석은 파일에서 읽는 편이 낫다.</summary>
    public const int MaxLength = 4000;

    public static string? Of(IReadOnlyList<string> documentation)
    {
        if (documentation.Count == 0) return null;

        // 선언은 주석이 아니다. 대개 ```cs … ``` 로 싸여 오고, scip-python 모듈은 첫 칸에 "(module) a.b" 를 적는다.
        var start = documentation[0].TrimStart().StartsWith("(module) ", StringComparison.Ordinal) ? 1 : 0;

        var parts = new List<string>();
        for (var i = start; i < documentation.Count; i++)
        {
            var block = documentation[i];
            if (block.TrimStart().StartsWith("```", StringComparison.Ordinal)) continue;

            var text = LooksLikeXml(block) ? FromXml(block) : Dedent(block);
            if (text.Length > 0) parts.Add(text);
        }

        if (parts.Count == 0) return null;

        var joined = string.Join("\n\n", parts);
        return joined.Length > MaxLength ? joined[..MaxLength].TrimEnd() + "…" : joined;
    }

    /// <summary>첫 문단. 멤버 목록에 한 줄로 붙인다.</summary>
    public static string? Summary(string? doc)
    {
        if (string.IsNullOrWhiteSpace(doc)) return null;
        var paragraph = doc.Split("\n\n", 2)[0].Trim();
        return paragraph.Length == 0 ? null : string.Join(' ', paragraph.Split('\n', StringSplitOptions.TrimEntries));
    }

    private static bool LooksLikeXml(string block)
    {
        var trimmed = block.TrimStart();
        return trimmed.StartsWith("<member", StringComparison.Ordinal)
            || trimmed.StartsWith("<summary", StringComparison.Ordinal)
            || trimmed.StartsWith("<remarks", StringComparison.Ordinal)
            || trimmed.StartsWith("<doc", StringComparison.Ordinal);
    }

    /// <summary>
    /// 독스트링과 JSDoc. 파이썬의 <c>inspect.cleandoc</c> 와 같은 규칙이다 — 첫 줄은 따로 다듬고,
    /// 나머지는 가장 얕은 들여쓰기만큼 걷어 낸다. 안쪽 들여쓰기(<c>Args:</c> 아래)는 뜻이 있으므로 둔다.
    /// </summary>
    private static string Dedent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace("\t", "    ").Split('\n');
        var indent = lines.Skip(1)
            .Where(line => line.Trim().Length > 0)
            .Select(line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();

        var cleaned = new List<string> { lines[0].Trim() };
        cleaned.AddRange(lines.Skip(1).Select(line => (line.Length >= indent ? line[indent..] : line.TrimStart()).TrimEnd()));

        while (cleaned.Count > 0 && cleaned[0].Length == 0) cleaned.RemoveAt(0);
        while (cleaned.Count > 0 && cleaned[^1].Length == 0) cleaned.RemoveAt(cleaned.Count - 1);
        return string.Join('\n', cleaned);
    }

    /// <summary>
    /// C# 의 XML 주석. 요약 → 비고 → 매개변수 → 반환 순으로 문단을 잇는다.
    /// 소스의 줄바꿈은 편집기 폭에 맞춘 것이라 문단 안에서는 이어 붙이고, 빈 줄만 문단으로 본다.
    /// </summary>
    private static string FromXml(string block)
    {
        XElement root;
        try
        {
            root = XElement.Parse("<doc>" + block + "</doc>", LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            // 깨진 주석이라도 글은 살린다.
            return Paragraphs(WebUtilityDecode(Tags().Replace(block, " ")));
        }

        var member = root.Element("member") ?? root;
        var paragraphs = new List<string>();

        foreach (var name in new[] { "summary", "remarks" })
        {
            foreach (var element in member.Elements(name))
            {
                var text = Paragraphs(Inline(element));
                if (text.Length > 0) paragraphs.Add(text);
            }
        }

        var lines = new List<string>();
        foreach (var parameter in member.Elements("typeparam").Concat(member.Elements("param")))
        {
            var text = Paragraphs(Inline(parameter)).Replace("\n\n", " ");
            if (text.Length > 0) lines.Add($"`{parameter.Attribute("name")?.Value}` — {text}");
        }

        foreach (var element in member.Elements("returns").Concat(member.Elements("value")))
        {
            var text = Paragraphs(Inline(element)).Replace("\n\n", " ");
            if (text.Length > 0) lines.Add("→ " + text);
        }

        if (lines.Count > 0) paragraphs.Add(string.Join('\n', lines));
        return string.Join("\n\n", paragraphs);
    }

    /// <summary>요소 안의 글. 안에 든 태그는 가벼운 표기로 바꾼다.</summary>
    private static string Inline(XElement element)
    {
        var builder = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    builder.Append(text.Value);
                    break;
                case XElement child:
                    builder.Append(child.Name.LocalName switch
                    {
                        "c" => Code(child.Value),
                        "code" => "\n\n" + Code(child.Value) + "\n\n",
                        "b" or "strong" => $"**{Inline(child).Trim()}**",
                        "para" => "\n\n" + Inline(child) + "\n\n",
                        "br" => "\n",
                        "list" => "\n\n" + string.Join('\n', child.Elements("item").Select(item => "- " + Collapse(Inline(item)))) + "\n\n",
                        "paramref" or "typeparamref" => Code(child.Attribute("name")?.Value ?? string.Empty),
                        "see" or "seealso" => See(child),
                        _ => Inline(child),
                    });
                    break;
            }
        }

        return builder.ToString();
    }

    private static string See(XElement see)
    {
        if (see.Nodes().Any()) return Inline(see);
        if (see.Attribute("langword")?.Value is { } word) return Code(word);
        if (see.Attribute("cref")?.Value is { } cref) return Code(ShortName(cref));
        return see.Attribute("href")?.Value ?? string.Empty;
    }

    /// <summary>
    /// <c>F:Acme.Shop.Confidence.Inferred</c> → <c>Confidence.Inferred</c>, <c>T:Acme.Shop.Order</c> → <c>Order</c>.
    /// 멤버는 타입 이름을 달고 가야 어느 것인지 안다.
    /// </summary>
    private static string ShortName(string cref)
    {
        var kind = cref.Length > 2 && cref[1] == ':' ? cref[0] : 'T';
        var name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;

        var paren = name.IndexOf('(');
        if (paren >= 0) name = name[..paren];
        name = Arity().Replace(name, string.Empty);

        // 생성자는 타입 이름으로 부른다.
        var constructor = name.EndsWith(".#ctor", StringComparison.Ordinal);
        if (constructor) name = name[..^".#ctor".Length];

        var parts = name.Split('.');
        return kind is 'T' or 'N' || constructor || parts.Length < 2
            ? parts[^1]
            : parts[^2] + "." + parts[^1];
    }

    private static string Code(string text)
    {
        var collapsed = Collapse(text);
        return collapsed.Length == 0 ? string.Empty : "`" + collapsed + "`";
    }

    private static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    /// <summary>빈 줄로 문단을 가르고, 문단 안의 줄은 잇는다. <c>- </c> 로 시작하는 줄은 항목이라 따로 둔다.</summary>
    private static string Paragraphs(string text)
    {
        var paragraphs = new List<string>();
        foreach (var raw in BlankLine().Split(text.Replace("\r\n", "\n")))
        {
            var lines = new List<string>();
            foreach (var line in raw.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
            {
                if (lines.Count == 0 || line.StartsWith("- ", StringComparison.Ordinal) || lines[^1].StartsWith("- ", StringComparison.Ordinal))
                {
                    lines.Add(Collapse(line));
                }
                else
                {
                    lines[^1] = lines[^1] + " " + Collapse(line);
                }
            }

            if (lines.Count > 0) paragraphs.Add(string.Join('\n', lines));
        }

        return string.Join("\n\n", paragraphs);
    }

    private static string WebUtilityDecode(string text) => System.Net.WebUtility.HtmlDecode(text);

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex BlankLine();

    [GeneratedRegex(@"`+\d+")]
    private static partial Regex Arity();
}
