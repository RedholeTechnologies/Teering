using Microsoft.Data.Sqlite;

namespace Treering.Core;

/// <summary>
/// 계층도의 한 칸.
/// </summary>
/// <param name="Merged">
/// 외줄로 이어진 네임스페이스를 한 칸으로 합쳤을 때, 합쳐진 것들의 id. 경로를 따라 펼칠 때
/// 중간 네임스페이스가 이 칸 안에 들어 있는지 알아보는 데 쓴다.
/// </param>
/// <param name="Shape">
/// 멤버일 때만: <c>method</c> · <c>property</c> · <c>field</c> · <c>event</c> · <c>member</c>.
/// 패널이 멤버를 묶는 규칙(<see cref="MemberInfo.Shape"/>)과 같다.
/// </param>
public sealed record TreeNode(
    long Id,
    string Display,
    SymbolKind Kind,
    string? Flavor,
    bool Own,
    string? Module,
    int ChildCount,
    IReadOnlyList<long> Merged,
    string? Shape = null);

public sealed record TreeChildren(IReadOnlyList<TreeNode> Nodes, int More);

/// <summary>한 패키지를 나눈 무리 하나. 그 아래 네임스페이스는 전부 이 무리에 든다.</summary>
public sealed record TreeGroup(long Id, string Display, IReadOnlyList<long> Namespaces);

/// <summary>한 노드가 이어진 타입 하나와, 그 타입에서 모듈까지 올라가는 사슬.</summary>
public sealed record TreeLink(long Other, int Weight, bool Outgoing, IReadOnlyList<long> Chain);

/// <summary>
/// 담는 사슬을 나무로 읽는다 — 모듈 → 네임스페이스 → 타입 → 멤버.
///
/// 지도가 «누가 누구를 부르나» 라면 이것은 «무엇이 무엇 안에 있나» 다. 둘을 겹치려고
/// <see cref="Links"/> 가 있다: 한 노드가 부르고 불리는 타입과, 그 타입의 조상 사슬을 돌려주면
/// 화면이 지금 펼쳐져 보이는 가장 가까운 조상에 선을 긋는다.
/// </summary>
public static class Tree
{
    private const int ChildLimit = 400;

    // 사람이 나무에서 찾는 것만. 매개변수·지역 변수는 멤버의 일부다.
    private const string Kinds = "2, 3, 4, 5";

    public static TreeChildren Children(SqliteConnection db, long? parentId, bool ownOnly)
    {
        var rows = parentId is { } id ? ChildrenOf(db, id, ownOnly) : Modules(db, ownOnly);

        // Python 의 모듈은 이름에 경로가 다 들어 있다(src.shop.orders). 부모 밑에서는
        // 부모 이름을 떼고 보여 준다 — 나무가 이미 그 경로다.
        var above = parentId is { } parent ? DisplayOf(db, parent) : null;

        var nodes = new List<TreeNode>();
        foreach (var row in rows.Take(ChildLimit))
        {
            var node = row.Kind == SymbolKind.Namespace ? Compact(db, row, ownOnly) : row;
            if (above is { Length: > 0 } && node.Display.StartsWith(above + ".", StringComparison.Ordinal))
            {
                node = node with { Display = node.Display[(above.Length + 1)..] };
            }
            nodes.Add(node);
        }

        return new TreeChildren(nodes, Math.Max(0, rows.Count - ChildLimit));
    }

    /// <summary>모듈에서 이 심볼까지 내려오는 id. 검색한 것까지 가지를 펼칠 때 쓴다.</summary>
    public static List<long> Path(SqliteConnection db, long id)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE up(id, container_id, depth) AS (
                SELECT id, container_id, 0 FROM symbol WHERE id = $id
                UNION ALL
                SELECT s.id, s.container_id, u.depth + 1
                FROM symbol s JOIN up u ON s.id = u.container_id
                WHERE u.depth < 64
            )
            SELECT id FROM up ORDER BY depth DESC
            """;
        command.Parameters.AddWithValue("$id", id);

        var path = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) path.Add(reader.GetInt64(0));
        return path;
    }

    /// <summary>
    /// 이 노드 «안» 의 타입들이 바깥의 어느 타입을 부르고, 어느 타입에게 불리는가.
    /// 멤버면 그 멤버를 가진 타입으로 본다 — 호출은 타입 단위로 말아 둔 것을 쓴다.
    /// </summary>
    public static List<TreeLink> Links(SqliteConnection db, long id, int limit = 300)
    {
        using (var mark = db.CreateCommand())
        {
            mark.CommandText = """
                DROP TABLE IF EXISTS temp.mine;
                CREATE TEMP TABLE mine(id INTEGER PRIMARY KEY);

                WITH RECURSIVE
                    start(id) AS (
                        SELECT CASE WHEN kind IN (4, 5) THEN type_id ELSE id END FROM symbol WHERE id = $id
                    ),
                    sub(id, kind) AS (
                        SELECT s.id, s.kind FROM symbol s JOIN start ON s.id = start.id
                        UNION
                        SELECT s.id, s.kind FROM symbol s JOIN sub ON s.container_id = sub.id
                        WHERE s.kind IN (2, 3)
                    )
                INSERT OR IGNORE INTO mine(id) SELECT id FROM sub WHERE kind = 3;
                """;
            mark.Parameters.AddWithValue("$id", id);
            mark.ExecuteNonQuery();
        }

        var links = new List<(long Other, int Weight, bool Outgoing)>();
        using (var command = db.CreateCommand())
        {
            command.CommandText = $"""
                SELECT other, SUM(weight) AS weight, outgoing FROM (
                    SELECT r.to_id AS other, r.weight, 1 AS outgoing
                    FROM edge_roll r
                    WHERE r.level = {(int)Granularity.Type} AND r.died_ord IS NULL AND r.kind IN (2, 3)
                      AND r.from_id IN (SELECT id FROM mine) AND r.to_id NOT IN (SELECT id FROM mine)
                    UNION ALL
                    SELECT r.from_id, r.weight, 0
                    FROM edge_roll r
                    WHERE r.level = {(int)Granularity.Type} AND r.died_ord IS NULL AND r.kind IN (2, 3)
                      AND r.to_id IN (SELECT id FROM mine) AND r.from_id NOT IN (SELECT id FROM mine)
                )
                GROUP BY other, outgoing
                ORDER BY weight DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read()) links.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2) == 1));
        }

        var chains = Chains(db, links.Select(link => link.Other).Distinct().ToList());
        return links
            .Select(link => new TreeLink(link.Other, link.Weight, link.Outgoing, chains.GetValueOrDefault(link.Other, [])))
            .ToList();
    }

    /// <summary>타입마다 자기부터 모듈까지 올라가는 사슬.</summary>
    private static Dictionary<long, List<long>> Chains(SqliteConnection db, List<long> starts)
    {
        var chains = new Dictionary<long, List<long>>();
        if (starts.Count == 0) return chains;

        using var command = db.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE up(start, id, depth) AS (
                SELECT value, value, 0 FROM json_each($starts)
                UNION ALL
                SELECT u.start, s.container_id, u.depth + 1
                FROM up u JOIN symbol s ON s.id = u.id
                WHERE s.container_id IS NOT NULL AND u.depth < 64
            )
            SELECT start, id FROM up ORDER BY start, depth
            """;
        command.Parameters.AddWithValue("$starts", "[" + string.Join(',', starts) + "]");

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var start = reader.GetInt64(0);
            if (!chains.TryGetValue(start, out var chain)) chains[start] = chain = [];
            chain.Add(reader.GetInt64(1));
        }

        return chains;
    }

    /// <summary>
    /// 모듈은 우리 것 먼저, 그 안에서 큰 것부터. 화면이 모듈 색을 이 순서로 나눠 주므로
    /// 어느 화면을 먼저 열든 같은 모듈이 같은 색이다.
    /// </summary>
    private static List<TreeNode> Modules(SqliteConnection db, bool ownOnly)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, s.display, s.kind, s.flavor, s.own, s.display,
                   NULL,
                   (SELECT COUNT(*) FROM symbol c
                    WHERE c.container_id = s.id AND c.kind IN ({Kinds}) AND c.display <> '<invalid-global-code>'
                      AND EXISTS (SELECT 1 FROM symbol_life l WHERE l.symbol_id = c.id AND l.died_ord IS NULL))
            FROM symbol s
            WHERE s.kind = 10
              AND EXISTS (SELECT 1 FROM symbol_life l WHERE l.symbol_id = s.id AND l.died_ord IS NULL)
              {(ownOnly ? "AND s.own = 1" : string.Empty)}
            ORDER BY s.own DESC,
                     (SELECT COUNT(*) FROM symbol m WHERE m.module_id = s.id) DESC,
                     s.display
            """;
        return Read(command);
    }

    private static List<TreeNode> ChildrenOf(SqliteConnection db, long parentId, bool ownOnly)
    {
        using var command = db.CreateCommand();
        // 네임스페이스 먼저, 그다음 타입, 멤버는 메서드 다음 필드·프로퍼티. 같은 종류 안에서는 이름순.
        command.CommandText = $"""
            SELECT s.id, s.display, s.kind, s.flavor, s.own, p.name, s.signature,
                   (SELECT COUNT(*) FROM symbol c
                    WHERE c.container_id = s.id AND c.kind IN ({Kinds}) AND c.display <> '<invalid-global-code>'
                      AND EXISTS (SELECT 1 FROM symbol_life l WHERE l.symbol_id = c.id AND l.died_ord IS NULL))
            FROM symbol s
            LEFT JOIN package p ON p.id = s.package_id
            WHERE s.container_id = $parent AND s.kind IN ({Kinds})
              AND s.display <> '<invalid-global-code>'
              AND EXISTS (SELECT 1 FROM symbol_life l WHERE l.symbol_id = s.id AND l.died_ord IS NULL)
              {(ownOnly ? "AND s.own = 1" : string.Empty)}
            ORDER BY CASE s.kind WHEN 2 THEN 0 WHEN 3 THEN 1 WHEN 4 THEN 2 ELSE 3 END, s.display
            """;
        command.Parameters.AddWithValue("$parent", parentId);
        return Read(command);
    }

    /// <summary>
    /// 네임스페이스가 외줄로 이어지면 한 칸으로 합친다. <c>Acme</c> →
    /// <c>Acme.Shop</c> → <c>App</c> 을 세 번 펼쳐야 겨우 무언가 나오는 나무는 쓸모가 없다.
    /// 합친 칸의 id 는 가장 깊은 것이다 — 펼치면 그 아래가 나온다.
    /// </summary>
    private static TreeNode Compact(SqliteConnection db, TreeNode node, bool ownOnly)
    {
        var separator = SeparatorFor(db, node.Id);
        var current = node;
        var names = new List<string> { node.Display };
        var merged = new List<long> { node.Id };

        for (var guard = 0; guard < 32 && current.ChildCount == 1; guard++)
        {
            var only = ChildrenOf(db, current.Id, ownOnly);
            if (only.Count != 1 || only[0].Kind != SymbolKind.Namespace) break;
            current = only[0];
            names.Add(current.Display);
            merged.Add(current.Id);
        }

        return current with { Display = names.Aggregate("", (joined, next) => JoinName(joined, next, separator)), Merged = merged };
    }

    /// <summary>
    /// 두 층의 이름을 잇는다. 아래 이름이 위 이름을 이미 품고 있으면(Python 의 <c>src</c> 밑
    /// <c>src.shop</c>) 아래 이름이 곧 합친 이름이다.
    /// </summary>
    private static string JoinName(string above, string below, string separator) =>
        above.Length == 0 ? below
        : below.StartsWith(above + ".", StringComparison.Ordinal) ? below
        : above + separator + below;

    private static string? DisplayOf(SqliteConnection db, long id)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT display FROM symbol WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// 이어 붙일 때 쓰는 구분자. C# 과 Python 은 점으로 잇고(<c>src.shop</c>), TypeScript 의
    /// «네임스페이스» 는 폴더라 빗금으로 잇는다 — <c>src.lib</c> 는 아무도 그렇게 부르지 않는다.
    /// </summary>
    private static string SeparatorFor(SqliteConnection db, long id)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT key FROM symbol WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var key = command.ExecuteScalar() as string ?? string.Empty;
        return key.StartsWith("scip-typescript", StringComparison.Ordinal) ? "/" : ".";
    }

    /// <summary>
    /// 패키지 하나를 볼 만한 무리로 나눈다. 모듈이 여럿인 리포는 모듈이 곧 무리지만,
    /// TypeScript 앱처럼 패키지가 하나뿐이면 첫 화면이 거품 하나가 된다.
    ///
    /// 패키지 바로 아래에서 시작해, 한 가지가 전체의 대부분(70% 넘게)을 차지하면 그 가지를
    /// 그 아래 가지들로 바꾼다. Next.js 프로젝트면 <c>next.config.ts</c> · <c>vitest.config.ts</c> ·
    /// <c>src</c> 에서, <c>src</c> 가 거의 전부라 <c>src/app</c> · <c>src/lib</c> … 로 내려간다.
    /// 무게는 그 가지 아래 든 타입의 수다.
    /// </summary>
    public static List<TreeGroup> Groups(SqliteConnection db, long moduleId)
    {
        var parent = new Dictionary<long, long?>();
        var name = new Dictionary<long, string>();
        var weight = new Dictionary<long, int>();

        using (var command = db.CreateCommand())
        {
            command.CommandText = """
                SELECT s.id, s.container_id, s.display,
                       (SELECT COUNT(*) FROM symbol t WHERE t.namespace_id = s.id AND t.kind = 3)
                FROM symbol s
                WHERE s.kind = 2 AND s.module_id = $module
                  AND EXISTS (SELECT 1 FROM symbol_life l WHERE l.symbol_id = s.id AND l.died_ord IS NULL)
                """;
            command.Parameters.AddWithValue("$module", moduleId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                parent[id] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                name[id] = reader.GetString(2);
                weight[id] = reader.GetInt32(3);
            }
        }

        var children = parent
            .Where(pair => pair.Value is { } p && (p == moduleId || parent.ContainsKey(p)))
            .GroupBy(pair => pair.Value!.Value)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).OrderBy(id => name[id]).ToList());

        var total = new Dictionary<long, int>();
        int Total(long id)
        {
            if (total.TryGetValue(id, out var known)) return known;
            var sum = weight.GetValueOrDefault(id) + children.GetValueOrDefault(id, []).Sum(Total);
            total[id] = sum;
            return sum;
        }

        // The package symbol is ours ("package x"); a namespace key carries the indexer's scheme.
        var separator = parent.Count > 0 ? SeparatorFor(db, parent.Keys.First()) : ".";

        // A branch that goes one way down is one step, as in the tree.
        (long Id, string Display) Step(long id, string prefix)
        {
            var label = JoinName(prefix, name[id], separator);
            while (weight.GetValueOrDefault(id) == 0 && children.GetValueOrDefault(id, []) is [var only])
            {
                id = only;
                label = JoinName(label, name[id], separator);
            }
            return (id, label);
        }

        var groups = children.GetValueOrDefault(moduleId, []).Select(id => Step(id, "")).ToList();
        for (var round = 0; round < 6; round++)
        {
            var all = groups.Sum(group => Total(group.Id));
            if (all == 0) break;

            var heaviest = groups.MaxBy(group => Total(group.Id));
            var below = children.GetValueOrDefault(heaviest.Id, []);
            if (groups.Count > 1 && Total(heaviest.Id) * 10 <= all * 7) break;
            if (below.Count < 2) break;

            groups.Remove(heaviest);
            groups.AddRange(below.Select(id => Step(id, heaviest.Display)));
        }

        // Every namespace belongs to the nearest group above it (or is one).
        var groupOf = new Dictionary<long, long>();
        var ids = groups.Select(group => group.Id).ToHashSet();
        foreach (var id in parent.Keys)
        {
            for (long? at = id; at is { } here; at = parent.GetValueOrDefault(here))
            {
                if (ids.Contains(here)) { groupOf[id] = here; break; }
            }
        }

        return groups
            .Where(group => Total(group.Id) > 0)
            .OrderByDescending(group => Total(group.Id))
            .Select(group => new TreeGroup(
                group.Id, group.Display,
                groupOf.Where(pair => pair.Value == group.Id).Select(pair => pair.Key).ToList()))
            .ToList();
    }

    private static List<TreeNode> Read(SqliteCommand command)
    {
        var nodes = new List<TreeNode>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var display = reader.GetString(1);
            var kind = (SymbolKind)reader.GetInt32(2);
            var signature = reader.IsDBNull(6) ? null : reader.GetString(6);
            nodes.Add(new TreeNode(
                id,
                display,
                kind,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(7),
                [id],
                // A member says what it is - method, property, field, event - by the same rule the
                // panel uses, so the tree can draw a function and a constant differently.
                kind is SymbolKind.Method or SymbolKind.Term ? new MemberInfo(id, display, kind, signature).Shape : null));
        }

        return nodes;
    }
}
