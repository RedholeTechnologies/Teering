using Microsoft.Data.Sqlite;

namespace Treering.Core;

/// <summary>어느 층에서 볼 것인가. 미리 계산해 둔 열 하나로 바뀐다.</summary>
public enum Granularity
{
    Module,
    Namespace,
    Type,
    Symbol,
}

public enum Direction
{
    Out,
    In,
    Both,
}

public sealed record SubgraphRequest
{
    public required IReadOnlyList<long> Seeds { get; init; }
    public Granularity Granularity { get; init; } = Granularity.Type;
    public Direction Direction { get; init; } = Direction.Both;
    public int Depth { get; init; } = 2;
    public IReadOnlyList<EdgeKind> Kinds { get; init; } = [EdgeKind.Reference, EdgeKind.Inherit];

    /// <summary>
    /// 노드 상한. 렌더러의 한계가 아니라 <b>읽을 수 있음의 한계</b>다.
    /// WebGL 은 10만 개도 그리지만 사람은 그걸 못 읽고, 질의도 공짜가 아니다.
    /// </summary>
    public int NodeBudget { get; init; } = 2000;

    /// <summary>한 노드에서 뻗어 나갈 간선 상한. 허브가 화면을 덮는 것을 막는다.</summary>
    public int DegreeCap { get; init; } = 50;

    /// <summary>
    /// 간선 상한. 노드만 묶어서는 페이로드가 안 잡힌다 — 실측에서 노드 2,000개에
    /// 간선이 22,045개 딸려 와 JSON 이 1.7MB 가 됐다(예산 1MB).
    /// 간선 하나가 대략 80바이트이므로 8,000개면 1MB 안에 든다.
    /// </summary>
    public int EdgeBudget { get; init; } = 8000;

    /// <summary>어느 스냅샷의 그래프인가. 비우면 마지막.</summary>
    public int? AtOrd { get; init; }

    /// <summary>
    /// 비교 기준 스냅샷. 주면 그 사이에 <b>새로 생긴 것과 끊어진 것</b> 을 함께 돌려준다.
    /// 끊어진 간선은 보통 질의에서 빠지지만, 비교할 때는 «사라졌다» 를 보여 줘야 하므로 넣는다.
    /// </summary>
    public int? CompareFrom { get; init; }

    /// <summary>
    /// 남의 코드를 빼고 볼 것인가. 시스템·NuGet 타입은 지도의 절반을 차지하면서
    /// 우리가 바꿀 수 있는 것은 하나도 없다. 끄면 우리가 쓴 것만 남는다.
    /// </summary>
    public bool OwnOnly { get; init; }

    /// <summary>
    /// 씨앗이 이미 그 층 «전부» 인가 — 전체 지도가 이 경우다.
    /// 그러면 넓혀도 새로 나올 노드가 없으므로 홉 질의를 건너뛴다.
    /// 실측에서 네임스페이스 전체 지도가 465ms 였고 대부분이 이 헛수고였다.
    /// </summary>
    public bool WholeLevel { get; init; }
}

/// <param name="Own">우리가 쓴 코드인가. 화면이 남의 코드를 배경으로 물릴 때 쓴다.</param>
/// <param name="ModuleId">
/// 담고 있는 모듈의 심볼. 화면이 네임스페이스를 모듈별로 묶거나, 남의 네임스페이스를
/// 모듈 하나로 접어 올릴 때 쓴다 — 이름만으로는 그 모듈로 들어갈 수 없다.
/// </param>
public sealed record SubgraphNode(
    long Id, string Display, SymbolKind Kind, string? Module, string? Flavor,
    bool Own = false, long? ModuleId = null);

public enum EdgeState
{
    /// <summary>비교 구간 이전부터 있었다.</summary>
    Same,

    /// <summary>비교 구간에 새로 생겼다.</summary>
    Born,

    /// <summary>비교 구간에 끊어졌다.</summary>
    Died,
}

public sealed record SubgraphEdge(long From, long To, EdgeKind Kind, int Weight, EdgeState State)
{
    /// <summary>참조 간선의 «누가» 는 위치 추론이다. 숨기지 않고 그대로 내보낸다.</summary>
    public bool Inferred => Kind == EdgeKind.Reference;
}

/// <summary>예산에 걸려 잘린 자리. 숨기지 않고 돌려준다.</summary>
public sealed record Truncation(long NodeId, string Display, int Hidden, string Reason);

public sealed record SubgraphResult(
    IReadOnlyList<SubgraphNode> Nodes,
    IReadOnlyList<SubgraphEdge> Edges,
    IReadOnlyList<Truncation> Truncated,
    bool BudgetExhausted,
    int DepthReached);

/// <summary>
/// UI·CLI·MCP 가 쓰는 유일한 원시연산.
///
/// 전체 그래프를 메모리에 올리지 않는다. 씨앗에서 한 홉씩 넓히되 홉마다 SQL 에 상한을 걸어,
/// 1만 개를 읽어 와서 자르는 게 아니라 애초에 예산만큼만 읽는다.
///
/// 기획서는 이것을 재귀 CTE 한 방으로 그렸지만 홉 단위로 나눴다. 이유는 둘이다 —
/// 노드마다 차수 상한을 따로 걸 수 있고, <b>어디가 잘렸는지</b> 를 돌려줄 수 있다.
/// 재귀 CTE 안에서는 둘 다 안 된다. 깊이는 보통 3 이하라 왕복 비용은 무시할 만하다.
/// </summary>
public static class Subgraph
{
    public static SubgraphResult Extract(SqliteConnection db, SubgraphRequest request)
    {
        var column = ColumnFor(request.Granularity);
        var kinds = string.Join(",", request.Kinds.Select(kind => (int)kind));
        var at = request.AtOrd ?? LatestOrd(db);
        var from = request.CompareFrom ?? at;

        var rolled = Roll(db, request.Seeds, column, at, from);
        var truncated = new List<Truncation>();
        var budgetExhausted = false;
        var depthReached = 0;

        // 씨앗 자체가 예산을 넘을 수 있다 — 큰 리포의 타입 층 전체 지도가 그렇다.
        // 넓히기 전에 여기서 자르지 않으면 예산이 아무 뜻도 없게 된다.
        if (rolled.Count > request.NodeBudget)
        {
            var dropped = rolled.Count - request.NodeBudget;
            rolled = Heaviest(db, request.Granularity, kinds, rolled, request.NodeBudget, at, from);
            budgetExhausted = true;
            truncated.Add(new Truncation(0, "(seeds)", dropped, "node-budget"));
        }

        var visited = new HashSet<long>(rolled);

        var frontier = rolled;
        var maxDepth = request.WholeLevel ? 0 : request.Depth;

        for (var depth = 1; depth <= maxDepth && frontier.Count > 0 && !budgetExhausted; depth++)
        {
            var next = new List<long>();

            foreach (var (source, target, hiddenDegree) in Step(db, kinds, frontier, request, at, from))
            {
                if (hiddenDegree > 0)
                {
                    truncated.Add(new Truncation(source, string.Empty, hiddenDegree, "degree-cap"));
                }

                if (visited.Contains(target)) continue;

                if (visited.Count >= request.NodeBudget)
                {
                    budgetExhausted = true;
                    break;
                }

                visited.Add(target);
                next.Add(target);
            }

            depthReached = depth;
            frontier = next;
        }

        var edges = FetchEdges(db, request.Granularity, kinds, visited, at, from);

        // 무거운 것부터 남긴다. 얇은 간선 수천 개보다 굵은 간선 수백 개가 더 많은 것을 말한다.
        if (edges.Count > request.EdgeBudget)
        {
            var dropped = edges.Count - request.EdgeBudget;
            edges = edges.OrderByDescending(edge => edge.Weight).Take(request.EdgeBudget).ToList();
            budgetExhausted = true;
            truncated.Add(new Truncation(0, "(edges)", dropped, "edge-budget"));
        }

        var nodes = FetchNodes(db, visited);
        var byId = nodes.ToDictionary(node => node.Id, node => node.Display);

        return new SubgraphResult(
            nodes,
            edges,
            // 같은 노드가 여러 홉에서 잘릴 수 있으므로 합쳐서 한 줄로 만든다.
            truncated
                .GroupBy(item => (item.NodeId, item.Reason))
                .Select(group => new Truncation(
                    group.Key.NodeId,
                    // 예산에 걸린 것은 특정 노드가 아니다. 그때는 만들 때 붙인 이름을 그대로 쓴다.
                    group.Key.NodeId == 0
                        ? group.First().Display
                        : byId.GetValueOrDefault(group.Key.NodeId, "?"),
                    group.Max(item => item.Hidden),
                    group.Key.Reason))
                .OrderByDescending(item => item.Hidden)
                .ToList(),
            budgetExhausted,
            depthReached);
    }

    /// <summary>
    /// 어떤 심볼을 누르고 더 세밀한 층으로 들어갈 때의 씨앗.
    ///
    /// 누른 것의 «대표» 가 아니라 «그 안에 든 것» 이다. 패키지를 누르면 그 패키지의
    /// 네임스페이스들이, 네임스페이스를 누르면 그 안의 타입들이 씨앗이 된다.
    /// 대표로 잘못 풀면 패키지에는 네임스페이스 대표가 없으므로 결과가 통째로 빈다.
    /// </summary>
    public static List<long> SeedsUnder(
        SqliteConnection db, long symbolId, Granularity target, bool ownOnly = false)
    {
        var targetColumn = ColumnFor(target);

        using var kindCommand = db.CreateCommand();
        kindCommand.CommandText = "SELECT kind FROM symbol WHERE id = $id";
        kindCommand.Parameters.AddWithValue("$id", symbolId);

        var kind = kindCommand.ExecuteScalar() is long value ? (SymbolKind)value : SymbolKind.Unknown;
        var ownColumn = kind switch
        {
            SymbolKind.Package => "module_id",
            SymbolKind.Namespace => "namespace_id",
            SymbolKind.Type => "type_id",
            _ => "id",
        };

        using var command = db.CreateCommand();
        // 네임스페이스는 안에 네임스페이스를 품는다. TypeScript 는 폴더 안에 파일, 파일 안에 타입이라
        // 폴더에 «바로» 든 타입만 세면 폴더로 들어갈 때마다 빈 화면이 나온다.
        // 그래서 네임스페이스로 들어갈 때는 그 아래 네임스페이스를 전부 함께 본다.
        command.CommandText = kind == SymbolKind.Namespace
            ? $"""
              WITH RECURSIVE under(id) AS (
                  SELECT $id
                  UNION
                  SELECT s.id FROM symbol s JOIN under ON s.container_id = under.id WHERE s.kind = 2
              )
              SELECT DISTINCT {targetColumn} FROM symbol
              WHERE namespace_id IN (SELECT id FROM under) AND {targetColumn} IS NOT NULL
              {(ownOnly ? "AND own = 1" : string.Empty)}
              """
            : $"SELECT DISTINCT {targetColumn} FROM symbol " +
              $"WHERE {ownColumn} = $id AND {targetColumn} IS NOT NULL" +
              (ownOnly ? " AND own = 1" : string.Empty);
        command.Parameters.AddWithValue("$id", symbolId);

        var seeds = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) seeds.Add(reader.GetInt64(0));
        return seeds;
    }

    /// <summary>
    /// 씨앗을 안 주면 그 층 전체가 씨앗이다 — 그게 «전체 지도» 다.
    /// 이렇게 얻은 씨앗으로 질의할 때는 <see cref="SubgraphRequest.WholeLevel"/> 를 켜라.
    /// </summary>
    public static List<long> AllAt(SqliteConnection db, Granularity granularity, bool ownOnly = false)
    {
        var column = ColumnFor(granularity);

        using var command = db.CreateCommand();
        command.CommandText =
            $"SELECT DISTINCT {column} FROM symbol WHERE {column} IS NOT NULL" +
            (ownOnly ? " AND own = 1" : string.Empty);

        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    /// <summary>
    /// id 목록을 <c>json_each</c> 에 넘길 배열 문자열로 만든다.
    /// 직렬화기를 쓸 이유가 없다 — 정수뿐이라 직접 잇는 편이 빠르고,
    /// NativeAOT 에서 리플렉션 기반 직렬화가 걸리는 문제도 피한다.
    /// </summary>
    private static string Ids(IEnumerable<long> ids) => "[" + string.Join(',', ids) + "]";

    /// <summary>
    /// 층마다 간선을 어디서 읽는가. 심볼 층은 원본 간선 그대로, 그 위 층은 적재 때 말아 둔
    /// <c>edge_roll</c> 에서 읽는다. 두 테이블은 열 모양이 같아서 아래 질의가 그대로 통한다 —
    /// 양 끝이 이미 그 층의 노드라 symbol 을 한 번도 찾아가지 않는다.
    /// </summary>
    private static (string Table, string Level) EdgesAt(Granularity granularity) =>
        granularity == Granularity.Symbol
            ? ("edge_life", "1 = 1")
            : ("edge_roll", $"e.level = {(int)granularity}");

    private static string ColumnFor(Granularity granularity) => granularity switch
    {
        Granularity.Module => "module_id",
        Granularity.Namespace => "namespace_id",
        Granularity.Type => "type_id",
        Granularity.Symbol => "id",
        _ => throw new ArgumentOutOfRangeException(nameof(granularity)),
    };

    /// <summary>
    /// 예산을 넘는 씨앗 중 «무거운 것» 만 남긴다. 아무거나 자르면 지도가 뜻을 잃으므로
    /// 붙은 간선의 무게 합이 큰 것부터 고른다 — 화면에서 먼저 보여야 할 것들이다.
    /// </summary>
    private static List<long> Heaviest(
        SqliteConnection db, Granularity granularity, string kinds,
        List<long> candidates, int keep, int at, int from)
    {
        var (table, level) = EdgesAt(granularity);

        using var command = db.CreateCommand();
        // 양 끝을 따로 세어 합친다. 나가는 쪽만 세면 불리기만 하는 노드가 무게 0 으로
        // 잘려 나간다 — 잎사귀부터 사라지는 지도가 된다.
        command.CommandText = $"""
            WITH touched AS (
                SELECT e.from_id AS id, e.weight
                FROM {table} e
                WHERE {level} AND {Window(from != at)} AND e.kind IN ({kinds})
                  AND e.from_id IN (SELECT value FROM json_each($ids))
                UNION ALL
                SELECT e.to_id AS id, e.weight
                FROM {table} e
                WHERE {level} AND {Window(from != at)} AND e.kind IN ({kinds})
                  AND e.to_id IN (SELECT value FROM json_each($ids))
            )
            SELECT id FROM touched GROUP BY id ORDER BY SUM(weight) DESC LIMIT $keep
            """;
        command.Parameters.AddWithValue("$ids", Ids(candidates));
        command.Parameters.AddWithValue("$keep", keep);
        command.Parameters.AddWithValue("$at", at);
        command.Parameters.AddWithValue("$from", from);

        var kept = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) kept.Add(reader.GetInt64(0));

        // 간선이 하나도 없는 노드는 위 질의에 안 잡힌다. 모자라면 앞에서부터 채운다.
        foreach (var id in candidates)
        {
            if (kept.Count >= keep) break;
            if (!kept.Contains(id)) kept.Add(id);
        }

        return kept;
    }

    /// <summary>씨앗을 요청한 층의 대표로 바꾼다. 미리 계산해 둔 열이라 사슬을 오를 필요가 없다.</summary>
    /// <summary>
    /// 씨앗을 그 층의 노드로 말아 올린다. <b>그때 살아 있던 것만</b> 남긴다 — 씨앗을 고르는 쪽은
    /// 시간을 모르고 한 번이라도 있었던 것을 다 준다. 걸러 내지 않으면 옮기거나 지운 폴더가
    /// 지도에 그대로 남는다. 비교할 때는 기준 시점에 살아 있던 것도 둔다 — 사라진 선의 끝이다.
    /// </summary>
    private static List<long> Roll(SqliteConnection db, IReadOnlyList<long> seeds, string column, int at, int from)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT s.{column} FROM symbol s
            WHERE s.id IN (SELECT value FROM json_each($seeds)) AND s.{column} IS NOT NULL
              AND EXISTS (
                  SELECT 1 FROM symbol_life l
                  WHERE l.symbol_id = s.{column}
                    AND ((l.born_ord <= $at AND (l.died_ord IS NULL OR l.died_ord > $at))
                      OR (l.born_ord <= $from AND (l.died_ord IS NULL OR l.died_ord > $from))))
            """;
        command.Parameters.AddWithValue("$seeds", Ids(seeds));
        command.Parameters.AddWithValue("$at", at);
        command.Parameters.AddWithValue("$from", from);

        var rolled = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rolled.Add(reader.GetInt64(0));
        return rolled;
    }

    /// <summary>
    /// 한 홉. 프런티어에서 나가는(또는 들어오는) 이웃을 가중치 순으로 차수 상한만큼만 가져온다.
    /// 상한에 걸려 빠진 개수도 같이 돌려받는다 — 그게 「+312곳 더」 가 된다.
    /// </summary>
    /// <summary>가장 마지막 스냅샷. 시간축을 안 쓰면 언제나 이것이다.</summary>
    private static int LatestOrd(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(ord), 0) FROM snapshot";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// 구간 저장이라 «그때의 그래프» 가 부등호 두 개로 나온다.
    /// 비교할 때는 그 사이에 끊어진 간선도 넣는다 — 사라진 것을 보여 줘야 하기 때문이다.
    /// </summary>
    private static string Window(bool comparing) => comparing
        ? "e.born_ord <= $at AND (e.died_ord IS NULL OR e.died_ord > $from)"
        : "e.born_ord <= $at AND (e.died_ord IS NULL OR e.died_ord > $at)";

    private static List<(long Source, long Target, int Hidden)> Step(
        SqliteConnection db, string kinds, List<long> frontier,
        SubgraphRequest request, int at, int from)
    {
        var results = new List<(long, long, int)>();
        var (table, level) = EdgesAt(request.Granularity);

        foreach (var outgoing in DirectionsOf(request.Direction))
        {
            var (near, far) = outgoing ? ("from_id", "to_id") : ("to_id", "from_id");
            var mine = request.OwnOnly
                ? $"AND EXISTS (SELECT 1 FROM symbol o WHERE o.id = e.{far} AND o.own = 1)"
                : string.Empty;

            using var command = db.CreateCommand();
            command.CommandText = $"""
                WITH rolled AS (
                    SELECT e.{near} AS source, e.{far} AS target, SUM(e.weight) AS weight
                    FROM {table} e
                    WHERE {level} AND {Window(from != at)}
                      AND e.kind IN ({kinds})
                      AND e.{near} IN (SELECT value FROM json_each($frontier))
                      AND e.{far} <> e.{near}
                      {mine}
                    GROUP BY source, target
                ),
                ranked AS (
                    SELECT source, target, weight,
                           ROW_NUMBER() OVER (PARTITION BY source ORDER BY weight DESC, target) AS rank,
                           COUNT(*) OVER (PARTITION BY source) AS degree
                    FROM rolled
                )
                SELECT source, target, degree
                FROM ranked
                WHERE rank <= $cap
                ORDER BY weight DESC
                """;
            command.Parameters.AddWithValue("$frontier", Ids(frontier));
            command.Parameters.AddWithValue("$cap", request.DegreeCap);
            command.Parameters.AddWithValue("$at", at);
            command.Parameters.AddWithValue("$from", from);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var degree = reader.GetInt32(2);
                results.Add((reader.GetInt64(0), reader.GetInt64(1), Math.Max(0, degree - request.DegreeCap)));
            }
        }

        return results;
    }

    private static IEnumerable<bool> DirectionsOf(Direction direction) => direction switch
    {
        Direction.Out => [true],
        Direction.In => [false],
        _ => [true, false],
    };

    private static List<SubgraphNode> FetchNodes(SqliteConnection db, HashSet<long> ids)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.display, s.kind, p.name, s.flavor, s.own, s.module_id
            FROM symbol s
            LEFT JOIN package p ON p.id = s.package_id
            WHERE s.id IN (SELECT value FROM json_each($ids))
            """;
        command.Parameters.AddWithValue("$ids", Ids(ids));

        var nodes = new List<SubgraphNode>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            nodes.Add(new SubgraphNode(
                reader.GetInt64(0),
                reader.GetString(1),
                (SymbolKind)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5) == 1,
                reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        }

        return nodes;
    }

    /// <summary>뽑힌 노드들 «사이» 의 간선만. 밖으로 나가는 것은 이미 예산에서 잘린 것이다.</summary>
    private static List<SubgraphEdge> FetchEdges(
        SqliteConnection db, Granularity granularity, string kinds, HashSet<long> ids, int at, int from)
    {
        var comparing = from != at;
        var (table, level) = EdgesAt(granularity);

        using var command = db.CreateCommand();
        // 말아 올린 간선 하나에 원본이 여럿 말려 들어간다. 그중 하나가 죽었다고
        // 의존이 끊긴 것이 아니다. 「그때 있었나 / 지금 있나」 로 판단한다 — TimeAxis 와 같은 정의다.
        // 같은 간선이 구간 여러 개로 나뉘어 있을 수 있다(끊겼다가 다시 이어진 것).
        // 그래서 여기서도 묶는다.
        command.CommandText = $"""
            SELECT e.from_id, e.to_id, e.kind,
                   SUM(CASE WHEN e.born_ord <= $at AND (e.died_ord IS NULL OR e.died_ord > $at)
                            THEN e.weight ELSE 0 END),
                   MAX(CASE WHEN e.born_ord <= $from AND (e.died_ord IS NULL OR e.died_ord > $from)
                            THEN 1 ELSE 0 END),
                   MAX(CASE WHEN e.born_ord <= $at AND (e.died_ord IS NULL OR e.died_ord > $at)
                            THEN 1 ELSE 0 END)
            FROM {table} e
            WHERE {level} AND {Window(comparing)}
              AND e.kind IN ({kinds})
              AND e.from_id IN (SELECT value FROM json_each($ids))
              -- 앞의 + 는 이 열로는 색인을 찾지 말라는 뜻이다. 양쪽 다 찾게 두면 planner 가
              -- from 목록 × to 목록을 곱해서 찌른다 — 타입 2,000개면 800만 번, 3.6초였다.
              -- from 으로만 찾고 to 는 걸러 내면 47ms 다.
              AND +e.to_id IN (SELECT value FROM json_each($ids))
              AND e.from_id <> e.to_id
            GROUP BY 1, 2, 3
            """;
        command.Parameters.AddWithValue("$ids", Ids(ids));
        command.Parameters.AddWithValue("$at", at);
        command.Parameters.AddWithValue("$from", from);

        var edges = new List<SubgraphEdge>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var wasThere = reader.GetInt32(4) == 1;
            var isThere = reader.GetInt32(5) == 1;
            var state = !comparing ? EdgeState.Same
                : !wasThere && isThere ? EdgeState.Born
                : wasThere && !isThere ? EdgeState.Died
                : EdgeState.Same;

            edges.Add(new SubgraphEdge(
                reader.GetInt64(0), reader.GetInt64(1), (EdgeKind)reader.GetInt32(2),
                reader.GetInt32(3), state));
        }

        return edges;
    }
}
