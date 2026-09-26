using Microsoft.Data.Sqlite;

namespace Treering.Core;

/// <summary>
/// 검색 결과 한 줄. 화면이 «그게 있는 자리» 로 데려가려면 담고 있는 것들을 알아야 하므로
/// 모듈·네임스페이스·타입을 함께 돌려준다.
/// </summary>
public sealed record SearchHit(
    long Id,
    string Display,
    SymbolKind Kind,
    string? Flavor,
    bool Own,
    long? ModuleId,
    string? Module,
    long? NamespaceId,
    string? Namespace,
    long? TypeId,
    string? Type);

/// <summary>
/// 이름으로 찾는다. 지금 살아 있는 것만, 사람이 찾을 만한 종류만 — 모듈·네임스페이스·타입·
/// 메서드·필드/프로퍼티. 매개변수나 지역 변수는 이름이 겹쳐서 목록만 덮는다.
///
/// 순서가 곧 쓸모다. 정확히 맞는 것이 맨 위이고, 그다음부터는 <b>우리 코드 먼저</b> —
/// 이 지도의 주인공은 우리 코드라, <c>DbContext</c> 를 쳤을 때 라이브러리의
/// <c>DbContextOptionsBuilder</c> 가 우리 <c>ShopDbContext</c> 를 밀어내면 안 된다.
/// 그 안에서 앞부분이 맞는 것 → 가운데 든 것, 타입 먼저, 짧은 이름 먼저.
/// </summary>
public static class Search
{
    public static List<SearchHit> Find(SqliteConnection db, string query, int limit = 20)
    {
        var text = query.Trim();
        if (text.Length == 0) return [];

        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.display, s.kind, s.flavor, s.own,
                   s.module_id, m.display,
                   s.namespace_id, n.display,
                   s.type_id, t.display
            FROM symbol s
            LEFT JOIN symbol m ON m.id = s.module_id
            LEFT JOIN symbol n ON n.id = s.namespace_id
            LEFT JOIN symbol t ON t.id = s.type_id
            WHERE s.kind IN (2, 3, 4, 5, 10)
              AND s.display LIKE $contains ESCAPE '\'
              AND s.display <> '<invalid-global-code>'
              AND EXISTS (SELECT 1 FROM symbol_life l WHERE l.symbol_id = s.id AND l.died_ord IS NULL)
            ORDER BY
                CASE WHEN s.display = $exact COLLATE NOCASE THEN 0 ELSE 1 END,
                s.own DESC,
                CASE WHEN s.display LIKE $prefix ESCAPE '\' THEN 0 ELSE 1 END,
                CASE s.kind WHEN 3 THEN 0 WHEN 2 THEN 1 WHEN 10 THEN 2 ELSE 3 END,
                LENGTH(s.display),
                s.display
            LIMIT $limit
            """;

        var escaped = Escape(text);
        command.Parameters.AddWithValue("$exact", text);
        command.Parameters.AddWithValue("$prefix", escaped + "%");
        command.Parameters.AddWithValue("$contains", "%" + escaped + "%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));

        var hits = new List<SearchHit>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new SearchHit(
                reader.GetInt64(0),
                reader.GetString(1),
                (SymbolKind)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return hits;
    }

    /// <summary>
    /// 사람이 친 <c>%</c> · <c>_</c> 는 글자다. 그대로 두면 <c>my_repo</c> 가
    /// «my 다음 아무 글자 하나 다음 repo» 를 찾는다.
    /// </summary>
    private static string Escape(string text) =>
        text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
