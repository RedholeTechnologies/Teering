using Microsoft.Data.Sqlite;

namespace Treering.Core;

/// <param name="Summary">문서 주석의 첫 문단. 멤버 목록에 한 줄로 붙는다.</param>
public sealed record MemberInfo(long Id, string Display, SymbolKind Kind, string? Signature, string? Summary = null)
{
    /// <summary>
    /// 선언을 보고 무엇인지 정한다. SCIP 는 프로퍼티와 필드를 둘 다 <c>.</c> 로 끝내므로
    /// 심볼 문자열만으로는 못 가른다. 컴파일러가 적어 준 선언에는 들어 있다.
    /// </summary>
    public string Shape
    {
        get
        {
            // 선언을 먼저 본다. 이벤트는 심볼 종류만으로는 중첩 타입과 구분되지 않는다.
            if (Signature is not null)
            {
                // 접근 한정자가 없으면 선언이 event 로 시작한다 — 인터페이스 멤버가 그렇다.
                if (Signature.StartsWith("event ", StringComparison.Ordinal)
                    || Signature.Contains(" event ", StringComparison.Ordinal))
                {
                    return "event";
                }

                if (Signature.Contains("{ get", StringComparison.Ordinal)) return "property";
            }

            return Kind switch
            {
                SymbolKind.Type => "type",
                SymbolKind.Namespace => "namespace",
                SymbolKind.Package => "module",
                SymbolKind.Method => "method",
                _ => Signature is null ? "member" : "field",
            };
        }
    }
}

public sealed record RelatedType(long Id, string Display, string? Module, string? Flavor, int Weight);

public sealed record SymbolDetailResult(
    long Id,
    string Display,
    SymbolKind Kind,
    string? Flavor,
    string? Signature,
    string? Doc,
    string? Module,
    string? Namespace,
    string? File,
    int? Line,
    IReadOnlyList<MemberInfo> Members,
    IReadOnlyList<RelatedType> Injects,
    IReadOnlyList<RelatedType> InjectedInto,
    IReadOnlyList<RelatedType> Implements,
    IReadOnlyList<RelatedType> ImplementedBy,
    IReadOnlyList<RelatedType> Callers,
    IReadOnlyList<RelatedType> Calls);

/// <summary>
/// 노드 하나를 눌렀을 때 옆에 펴 보이는 것. 그래프가 «어떻게 엮였나» 를 보여준다면
/// 여기는 «그래서 이게 무엇인가» 를 보여준다.
/// </summary>
public static class SymbolDetail
{
    private const int Limit = 30;

    public static SymbolDetailResult? Of(SqliteConnection db, long id)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT s.display, s.kind, s.flavor, s.signature, p.name, n.display, f.path, d.line, s.doc
            FROM symbol s
            LEFT JOIN package p ON p.id = s.package_id
            LEFT JOIN symbol n ON n.id = s.namespace_id
            LEFT JOIN definition d ON d.symbol_id = s.id AND d.died_ord IS NULL
            LEFT JOIN file f ON f.id = d.file_id
            WHERE s.id = $id
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        var kind = (SymbolKind)reader.GetInt32(1);

        var result = new SymbolDetailResult(
            id,
            reader.GetString(0),
            kind,
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            Members(db, id),
            Injects(db, id),
            InjectedInto(db, id),
            Inherit(db, id, outgoing: true),
            Inherit(db, id, outgoing: false),
            Neighbours(db, id, kind, callers: true),
            Neighbours(db, id, kind, callers: false));

        reader.Close();
        return result;
    }

    /// <summary>담고 있는 것. 클래스면 프로퍼티·메서드·필드, 인터페이스면 메서드다.</summary>
    private static List<MemberInfo> Members(SqliteConnection db, long id)
    {
        using var command = db.CreateCommand();
        // 매개변수와 타입 매개변수는 멤버가 아니라 메서드의 일부다. 여기서는 빼고 시그니처로 본다.
        command.CommandText = """
            SELECT s.id, s.display, s.kind, s.signature, s.doc
            FROM symbol s
            JOIN symbol_life l ON l.symbol_id = s.id AND l.died_ord IS NULL
            WHERE s.container_id = $id AND s.kind IN (3, 4, 5, 10, 2)
            ORDER BY s.kind, s.display
            LIMIT 200
            """;
        command.Parameters.AddWithValue("$id", id);

        var members = new List<MemberInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            members.Add(new MemberInfo(
                reader.GetInt64(0), reader.GetString(1), (SymbolKind)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DocComment.Summary(reader.IsDBNull(4) ? null : reader.GetString(4))));
        }

        return members;
    }

    /// <summary>
    /// 생성자가 받는 타입 — 이게 「주입된 것」 이다.
    ///
    /// DI 를 따로 알아볼 필요가 없다. 생성자 안에서 참조되는 타입이 곧 그 클래스가
    /// 받아 쓰는 것이고, 매개변수 타입은 이름보다 앞서 오므로 참조가 생성자에 귀속된다.
    /// </summary>
    /// <summary>
    /// 생성자를 부르는 이름은 언어마다 다르다 — C# <c>.ctor</c>, TypeScript <c>&lt;constructor&gt;</c>,
    /// Python <c>__init__</c>, Java <c>&lt;init&gt;</c>.
    /// </summary>
    private const string Constructors = "('.ctor', '<constructor>', '__init__', '<init>')";

    /// <remarks>
    /// 생성자와 그 매개변수를 함께 본다. <c>constructor(private repo: Repo)</c> 처럼 이름이
    /// 타입보다 앞에 오는 언어에서는 <c>Repo</c> 참조가 매개변수 <c>repo</c> 에 붙는다 —
    /// 「바로 앞의 정의」 가 매개변수이기 때문이다. C# 은 타입이 먼저라 우연히 괜찮았다.
    /// </remarks>
    private static List<RelatedType> Injects(SqliteConnection db, long id) =>
        Related(db, id, $"""
            SELECT t.id, t.display, p.name, t.flavor, SUM(e.weight)
            FROM symbol ctor
            JOIN symbol src ON src.id = ctor.id OR src.container_id = ctor.id
            JOIN edge_life e ON e.from_id = src.id AND e.kind = 3 AND e.died_ord IS NULL
            JOIN symbol raw ON raw.id = e.to_id
            JOIN symbol t ON t.id = raw.type_id
            LEFT JOIN package p ON p.id = t.package_id
            WHERE ctor.container_id = $id AND ctor.display IN {Constructors} AND t.id <> $id
            GROUP BY t.id
            ORDER BY SUM(e.weight) DESC
            LIMIT $limit
            """);

    /// <summary>이 타입을 생성자에서 받아 쓰는 곳 — 「어디로 주입되는가」.</summary>
    private static List<RelatedType> InjectedInto(SqliteConnection db, long id) =>
        Related(db, id, $"""
            SELECT owner.id, owner.display, p.name, owner.flavor, SUM(e.weight)
            FROM edge_life e
            JOIN symbol src ON src.id = e.from_id
            JOIN symbol ctor ON ctor.id IN (src.id, src.container_id) AND ctor.display IN {Constructors}
            JOIN symbol owner ON owner.id = ctor.container_id
            JOIN symbol target ON target.id = e.to_id
            LEFT JOIN package p ON p.id = owner.package_id
            WHERE e.kind = 3 AND e.died_ord IS NULL
              AND target.type_id = $id AND owner.id <> $id
            GROUP BY owner.id
            ORDER BY SUM(e.weight) DESC
            LIMIT $limit
            """);

    private static List<RelatedType> Inherit(SqliteConnection db, long id, bool outgoing)
    {
        var (near, far) = outgoing ? ("from_id", "to_id") : ("to_id", "from_id");

        return Related(db, id, $"""
            SELECT t.id, t.display, p.name, t.flavor, SUM(e.weight)
            FROM edge_life e
            JOIN symbol t ON t.id = e.{far}
            LEFT JOIN package p ON p.id = t.package_id
            WHERE e.kind = 2 AND e.died_ord IS NULL AND e.{near} = $id
            GROUP BY t.id
            ORDER BY t.display
            LIMIT $limit
            """);
    }

    /// <summary>
    /// 부르는 곳 · 부르는 대상.
    ///
    /// <b>화면에 그려진 선과 같은 층에서 말아 올려야 한다.</b> 모듈 노드를 눌렀는데
    /// 타입 단위로 물으면 모듈에는 <c>type_id</c> 가 없어서 아무것도 안 나온다 —
    /// 그래프에는 선이 뻗어 있는데 패널이 「없음」 이라고 답하던 이유다.
    /// </summary>
    private static List<RelatedType> Neighbours(
        SqliteConnection db, long id, SymbolKind kind, bool callers)
    {
        var near = RollupColumn(kind);
        // 멤버는 자기 간선으로 찾고, 상대 쪽은 타입으로 말아 올린다 — 「이 메서드를 부르는
        // 타입들」 이 읽을 수 있는 답이다. 같은 타입 안에서 부르는 것은 뺀다.
        var member = kind is SymbolKind.Method or SymbolKind.Term;
        var far = member ? "type_id" : near;

        // 메서드 본문의 참조는 메서드가 아니라 마지막 매개변수에 붙는다 — 「바로 앞의 정의」 가
        // 매개변수 선언이기 때문이다. 실측으로 AddPaymentAsync 의 참조 31개 중 30개가 그랬다.
        // 그래서 멤버는 자기와 그 안에 든 것(매개변수·지역 심볼)을 함께 본다.
        var mineIs = member ? "(m.id = $id OR m.container_id = $id)" : $"m.{near} = $id";
        var (mine, theirs) = callers ? ("to_id", "from_id") : ("from_id", "to_id");

        return Related(db, id, $"""
            SELECT t.id, t.display, p.name, t.flavor, SUM(e.weight)
            FROM edge_life e
            JOIN symbol m ON m.id = e.{mine}
            JOIN symbol raw ON raw.id = e.{theirs}
            JOIN symbol t ON t.id = raw.{far}
            LEFT JOIN package p ON p.id = t.package_id
            WHERE e.kind = 3 AND e.died_ord IS NULL
              AND {mineIs} AND t.id <> $id
              AND t.id IS NOT (SELECT type_id FROM symbol WHERE id = $id)
            GROUP BY t.id
            ORDER BY SUM(e.weight) DESC
            LIMIT $limit
            """);
    }

    /// <summary>
    /// 이 심볼이 그래프에서 어느 층으로 말아 올려지는가.
    ///
    /// 멤버는 자기 자신이다. 타입 층으로 물으면 어떤 심볼의 <c>type_id</c> 도 메서드일 수
    /// 없어서, 메서드 패널의 「부르는 곳」 이 언제나 「없음」 이었다.
    /// </summary>
    private static string RollupColumn(SymbolKind kind) => kind switch
    {
        SymbolKind.Package => "module_id",
        SymbolKind.Namespace => "namespace_id",
        SymbolKind.Method or SymbolKind.Term => "id",
        _ => "type_id",
    };

    private static List<RelatedType> Related(SqliteConnection db, long id, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$limit", Limit);

        var related = new List<RelatedType>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            related.Add(new RelatedType(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4)));
        }

        return related;
    }
}
