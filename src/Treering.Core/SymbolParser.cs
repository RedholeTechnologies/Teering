namespace Treering.Core;

public enum SymbolKind
{
    Unknown,
    Local,
    Namespace,
    Type,
    Method,
    Term,
    Parameter,
    TypeParameter,
    Meta,
    Macro,

    /// <summary>
    /// 어셈블리·패키지. SCIP 심볼에는 이런 조각이 없고 우리가 만들어 넣는 뿌리다.
    /// 이게 있어야 담는 사슬이 패키지 → 네임스페이스 → 타입 → 멤버로 한 줄로 이어지고,
    /// 전체 지도를 모듈 단위로 말아 올릴 수 있다.
    /// 기존 값 뒤에 붙인다 — 앞의 숫자들은 이미 DB 에 들어가 있다.
    /// </summary>
    Package = 10,
}

/// <summary>
/// SCIP 심볼 문자열을 뜯은 결과.
/// </summary>
/// <param name="Kind">
/// SCIP 는 <c>SymbolInformation.kind</c> 를 주지 않는다(scip-dotnet 0.2.14 에서 11,481개 전부 비어 있었다).
/// 대신 descriptor 접미가 종류를 결정적으로 알려준다. 추정이 아니라 파싱이다.
/// </param>
/// <param name="Key">
/// 저장에 쓰는 안정 키. <b>버전이 빠져 있다.</b> 버전을 키에 넣으면
/// csproj 의 &lt;Version&gt; 을 올리는 순간 그 어셈블리의 모든 심볼이 죽고 새로 태어난 것으로
/// 기록되어 시간축이 무너진다. 버전은 속성으로 따로 들고 간다.
/// </param>
public readonly record struct ParsedSymbol(
    SymbolKind Kind,
    string Key,
    string Prefix,
    string Package,
    string Version,
    string Descriptors)
{
    public bool IsLocal => Kind == SymbolKind.Local;

    /// <summary>패키지가 붙지 않은 심볼. --allow-global-symbol-definitions 없이 색인하면 흔하고,
    /// 켜고 색인해도 네임스페이스는 여기 남는다.</summary>
    public bool HasPackage => Package.Length > 0 && Package != ".";
}

public static class SymbolParser
{
    public static ParsedSymbol Parse(string symbol)
    {
        // 지역 심볼은 `local 0` 꼴이고 문서 안에서만 유효하다. 그래프에 올리지 않는다.
        if (symbol.StartsWith("local ", StringComparison.Ordinal))
        {
            return new ParsedSymbol(SymbolKind.Local, symbol, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        // <scheme> <manager> <package> <version> <descriptors>
        // 앞의 네 칸은 공백이 있으면 이스케이프되므로 그냥 잘라도 된다.
        var parts = symbol.Split(' ', 5);
        if (parts.Length < 5)
        {
            return new ParsedSymbol(SymbolKind.Unknown, symbol, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var descriptors = parts[4];
        // 버전(parts[3])을 일부러 뺀다. 이유는 위 주석.
        var prefix = string.Concat(parts[0], " ", parts[1], " ", parts[2], " ");

        return new ParsedSymbol(
            KindOf(descriptors),
            prefix + descriptors,
            prefix,
            parts[2],
            parts[3],
            descriptors);
    }

    private static SymbolKind KindOf(string descriptors)
    {
        if (descriptors.Length == 0)
        {
            return SymbolKind.Unknown;
        }

        // `().` 가 `.` 보다 먼저 걸려야 메서드가 필드로 새지 않는다.
        if (descriptors.EndsWith(").", StringComparison.Ordinal)) return SymbolKind.Method;

        return descriptors[^1] switch
        {
            '/' => SymbolKind.Namespace,
            '#' => SymbolKind.Type,
            '.' => SymbolKind.Term,
            ')' => SymbolKind.Parameter,
            ']' => SymbolKind.TypeParameter,
            ':' => SymbolKind.Meta,
            '!' => SymbolKind.Macro,
            _ => SymbolKind.Unknown,
        };
    }

    /// <summary>
    /// 포함 관계(contains)는 저장할 필요가 없다. 심볼 문자열이 이미 담고 있다 —
    /// <c>App/App#_db.</c> 의 부모는 <c>App/App#</c> 이고 그 부모는 <c>App/</c> 다.
    /// 마지막 descriptor 한 조각을 떼어 부모를 만든다. 없으면 null.
    /// </summary>
    public static string? ParentDescriptors(string descriptors)
    {
        var start = LastComponentStart(descriptors);
        return start <= 0 ? null : descriptors[..start];
    }

    /// <summary>
    /// SCIP 가 이름을 쓰는 법. 식별자 문자(<c>_+-$</c>, 영문, 숫자)만 있으면 그대로, 아니면
    /// 백틱으로 감싸고 안의 백틱은 두 번 쓴다 — 색인기가 쓴 키와 글자 하나까지 같아야 한다.
    /// </summary>
    public static string EscapeName(string name)
    {
        var simple = name.Length > 0 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '+' or '-' or '$');
        return simple ? name : "`" + name.Replace("`", "``") + "`";
    }

    /// <summary>사람이 읽는 이름. 마지막 조각에서 종결 기호를 뗀 것.</summary>
    public static string Display(string descriptors)
    {
        var start = LastComponentStart(descriptors);
        if (start < 0) return descriptors;

        var last = descriptors[start..];

        // 매개변수 `(ex)` · 타입 매개변수 `[T]` 는 이름이 괄호 «안» 에 있다.
        if (last.StartsWith('(')) return last.Trim('(', ')', '`');
        if (last.StartsWith('[')) return last.Trim('[', ']', '`');

        // 백틱으로 감싼 이름은 그 안이 곧 이름이다. 안에 괄호가 있어도 구분자가 아니다.
        if (last.StartsWith('`'))
        {
            var close = last.IndexOf('`', 1);
            if (close > 0) return last[1..close];
        }

        // 메서드는 `Name().` 이거나 중복 해소가 붙은 `Name(+2).` 다. 괄호 앞에서 자른다.
        var paren = last.IndexOfAny(['(', '[']);
        if (paren >= 0) return last[..paren];

        return last.TrimEnd('/', '#', '.', ':', '!').Trim('`');
    }

    /// <summary>마지막 descriptor 조각이 시작하는 위치.</summary>
    private static int LastComponentStart(string descriptors)
    {
        if (descriptors.Length == 0) return -1;

        // 종결 기호를 떼어 이름 부분의 끝을 찾는다.
        var end = descriptors.Length - 1;

        if (descriptors[end] == '.' && end > 0 && descriptors[end - 1] == ')')
        {
            // 메서드: `Name().` 또는 `Name(+2).` — 짝이 되는 '(' 까지 되돌아간다.
            end = descriptors.LastIndexOf('(', end - 1) - 1;
        }
        else if (descriptors[end] == ')')
        {
            // 매개변수: `(name)`
            end = descriptors.LastIndexOf('(', end - 1) - 1;
        }
        else if (descriptors[end] == ']')
        {
            end = descriptors.LastIndexOf('[', end - 1) - 1;
        }
        else
        {
            end--;
        }

        if (end < 0) return 0;

        // 이름 부분의 시작 = 직전 조각의 종결 기호 바로 뒤.
        // 단 백틱으로 감싼 이름 안의 기호는 구분자가 아니다 — `.ctor` 의 점이 대표적이다.
        for (var i = end; i >= 0; i--)
        {
            if (descriptors[i] == '`')
            {
                i = OpeningBacktick(descriptors, i);
                continue;
            }

            if (descriptors[i] is '/' or '#' or '.' or ')' or ']')
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// 멤버의 선언 그대로. <c>documentation</c> 첫 줄에 컴파일러가 적어 둔 것이다 —
    /// <c>private ShopDbContext? App._db</c> · <c>public static string App.User { get; private set; }</c>
    /// 처럼 접근성과 타입이 다 들어 있다. 「프로퍼티인가 필드인가」 를 따로 추측할 필요가 없다.
    /// </summary>
    public static string? SignatureOf(IEnumerable<string> documentation)
    {
        foreach (var block in documentation)
        {
            foreach (var line in block.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("```", StringComparison.Ordinal)) continue;

                // 선언 다음에는 XML 주석이 이어진다. 거기부터는 시그니처가 아니다.
                if (trimmed.StartsWith('<')) return null;
                return trimmed.Length > 300 ? trimmed[..300] : trimmed;
            }
        }

        return null;
    }

    /// <summary>
    /// 타입이 클래스인지 인터페이스인지 열거형인지. SCIP 의 <c>kind</c> 는 비어 있지만
    /// <c>documentation</c> 첫 줄에 컴파일러가 적은 선언이 들어 있다 — <c>```cs / class App</c> 꼴.
    /// 이름 규칙으로 추측하는 것이 아니라 Roslyn 이 적어 준 것을 읽는다.
    /// 외부 패키지 타입에는 documentation 이 없으므로 null 이 돌아온다.
    /// </summary>
    public static string? FlavorOf(IEnumerable<string> documentation)
    {
        foreach (var block in documentation)
        {
            foreach (var line in block.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("```", StringComparison.Ordinal)) continue;

                foreach (var token in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    // 선언 줄의 첫 키워드를 찾는다. 수식어(public·sealed·abstract)는 건너뛴다.
                    switch (token)
                    {
                        case "interface": return "interface";
                        case "enum": return "enum";
                        case "struct": return "struct";
                        case "record": return "record";
                        case "delegate": return "delegate";
                        case "class": return "class";
                    }
                }

                // 선언이 아닌 줄(요약문 등)이 먼저 나오면 더 볼 것이 없다.
                break;
            }
        }

        return null;
    }

    /// <summary>닫는 백틱 위치에서 여는 백틱 위치를 찾는다. SCIP 에서 `` 는 백틱 한 글자를 뜻한다.</summary>
    private static int OpeningBacktick(string descriptors, int closing)
    {
        var i = closing - 1;
        while (i >= 0)
        {
            if (descriptors[i] == '`')
            {
                if (i > 0 && descriptors[i - 1] == '`')
                {
                    i -= 2;
                    continue;
                }

                return i;
            }

            i--;
        }

        return 0;
    }
}
