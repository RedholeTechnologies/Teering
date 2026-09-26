using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Treering.Core;

namespace Treering.Cli;

/// <summary>
/// 에이전트가 파일을 뒤지기 전에 그래프에 먼저 묻게 하는 도구들.
///
/// 사람용 화면과 같은 원시연산(<see cref="Subgraph"/>) 위에 서 있고, 예산도 그대로 걸린다.
/// 1만 노드를 토해내서 컨텍스트를 태우는 대신 잘린 사실을 알려 주고 좁혀 다시 묻게 한다.
/// </summary>
[McpServerToolType]
public static class McpTools
{
    private static string _dbPath = string.Empty;

    public static void Use(string dbPath) => _dbPath = dbPath;

    private static Microsoft.Data.Sqlite.SqliteConnection Open() => GraphDb.Open(_dbPath);

    private static string Json(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });

    [McpServerTool(Name = "find_symbol")]
    [Description("Find a symbol by name. Looks at types, methods, namespaces and packages alike.")]
    public static string FindSymbol(
        [Description("The name to look for. Substring match.")] string name,
        [Description("Maximum results. Default 20.")] int limit = 20)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.display, s.kind, s.flavor, p.name, f.path, d.line
            FROM symbol s
            LEFT JOIN package p ON p.id = s.package_id
            LEFT JOIN definition d ON d.symbol_id = s.id AND d.died_ord IS NULL
            LEFT JOIN file f ON f.id = d.file_id
            WHERE s.display LIKE $name
            ORDER BY LENGTH(s.display), s.display
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$name", "%" + name + "%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));

        var found = new List<object>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            found.Add(new
            {
                id = reader.GetInt64(0),
                name = reader.GetString(1),
                kind = ((SymbolKind)reader.GetInt32(2)).ToString(),
                flavor = reader.IsDBNull(3) ? null : reader.GetString(3),
                module = reader.IsDBNull(4) ? null : reader.GetString(4),
                file = reader.IsDBNull(5) ? null : reader.GetString(5),
                line = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6),
            });
        }

        return Json(new { count = found.Count, symbols = found });
    }

    [McpServerTool(Name = "callers_of")]
    [Description("The types that call this one, rolled up to type level. "
        + "The «who» of a reference is inferred from position, so method level is unreliable.")]
    public static string CallersOf(
        [Description("Type name. Exact match.")] string type)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE
                target(id) AS (
                    SELECT id FROM symbol WHERE kind = 3 AND display = $name
                    UNION
                    SELECT s.id FROM symbol s JOIN target t ON s.container_id = t.id
                ),
                hit(from_id, weight) AS (
                    SELECT e.from_id, e.weight FROM edge_life e
                    WHERE e.kind = 3 AND e.died_ord IS NULL
                      AND e.to_id IN (SELECT id FROM target)
                      AND e.from_id NOT IN (SELECT id FROM target)
                ),
                up(from_id, current, weight) AS (
                    SELECT from_id, from_id, weight FROM hit
                    UNION ALL
                    SELECT u.from_id, s.container_id, u.weight
                    FROM up u JOIN symbol s ON s.id = u.current
                    WHERE s.kind <> 3 AND s.container_id IS NOT NULL
                )
            SELECT s.display, p.name, SUM(u.weight)
            FROM up u
            JOIN symbol s ON s.id = u.current
            LEFT JOIN package p ON p.id = s.package_id
            WHERE s.kind = 3
            GROUP BY s.id
            ORDER BY SUM(u.weight) DESC
            LIMIT 60
            """;
        command.Parameters.AddWithValue("$name", type);

        var callers = new List<object>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            callers.Add(new
            {
                type = reader.GetString(0),
                module = reader.IsDBNull(1) ? null : reader.GetString(1),
                references = reader.GetInt64(2),
            });
        }

        return Json(new { type, count = callers.Count, confidence = "inferred", callers });
    }

    [McpServerTool(Name = "subgraph")]
    [Description("The subgraph around one point. Budgets apply (2,000 nodes, degree 50) and "
        + "whatever was cut comes back under truncated. Narrow the seed and ask again if needed.")]
    public static string GetSubgraph(
        [Description("Seed symbol name. Leave it empty to see the whole level.")] string? seed = null,
        [Description("One of module, namespace, type. Default type.")] string granularity = "type",
        [Description("How many hops. Default 1.")] int depth = 1,
        [Description("Leave out what this repo did not write - system and package types. Default false.")]
        bool ownOnly = false)
    {
        if (!Enum.TryParse<Granularity>(granularity, ignoreCase: true, out var level))
        {
            return Json(new { error = $"unknown level: {granularity}" });
        }

        using var db = Open();

        List<long> seeds;
        var wholeLevel = string.IsNullOrWhiteSpace(seed);
        if (wholeLevel)
        {
            seeds = Subgraph.AllAt(db, level, ownOnly);
        }
        else
        {
            using var lookup = db.CreateCommand();
            lookup.CommandText = "SELECT id FROM symbol WHERE display = $name LIMIT 1";
            lookup.Parameters.AddWithValue("$name", seed);
            if (lookup.ExecuteScalar() is not long id) return Json(new { error = $"no symbol named '{seed}'" });
            seeds = Subgraph.SeedsUnder(db, id, level, ownOnly);
        }

        if (seeds.Count == 0) return Json(new { error = "no seeds" });

        var result = Subgraph.Extract(db, new SubgraphRequest
        {
            Seeds = seeds,
            Granularity = level,
            WholeLevel = wholeLevel,
            OwnOnly = ownOnly,
            Depth = Math.Clamp(depth, 1, 4),
        });

        // 심볼 문자열 원문 대신 짧은 이름으로 돌려준다. 토큰이 싼 모양이 우선이다.
        var names = result.Nodes.ToDictionary(node => node.Id, node => node.Display);

        return Json(new
        {
            nodes = result.Nodes.Select(node => new { node.Display, node.Module, node.Flavor }),
            edges = result.Edges.Select(edge => new
            {
                from = names.GetValueOrDefault(edge.From, "?"),
                to = names.GetValueOrDefault(edge.To, "?"),
                kind = edge.Kind.ToString(),
                edge.Weight,
                edge.Inferred,
            }),
            truncated = result.Truncated.Select(item => new { item.Display, item.Hidden }),
            result.BudgetExhausted,
        });
    }

    [McpServerTool(Name = "changed_since")]
    [Description("Dependencies that appeared or went away between two snapshots. "
        + "Answers «how has the structure moved since the last release» in one call.")]
    public static string ChangedSince(
        [Description("The snapshot ord to measure from.")] int fromOrd,
        [Description("The snapshot ord to compare against. Empty means the latest.")] int? toOrd = null,
        [Description("One of module, namespace, type. Default module.")] string granularity = "module")
    {
        if (!Enum.TryParse<Granularity>(granularity, ignoreCase: true, out var level))
        {
            return Json(new { error = $"unknown level: {granularity}" });
        }

        using var db = Open();
        var snapshots = TimeAxis.Snapshots(db);
        if (snapshots.Count == 0) return Json(new { error = "no snapshots" });

        var to = toOrd ?? snapshots[^1].Ord;
        var appeared = TimeAxis.Appeared(db, fromOrd, to, level);
        var gone = TimeAxis.Disappeared(db, fromOrd, to, level);

        return Json(new
        {
            fromOrd,
            toOrd = to,
            appeared = appeared.Take(50).Select(Describe),
            disappeared = gone.Take(50).Select(Describe),
            crossingBoundary = appeared.Where(change => change.CrossesBoundary).Take(20).Select(Describe),
        });
    }

    [McpServerTool(Name = "snapshots")]
    [Description("The snapshots on file. Pick the ord to hand to changed_since from here.")]
    public static string Snapshots()
    {
        using var db = Open();
        return Json(TimeAxis.Snapshots(db).Select(snapshot => new
        {
            snapshot.Ord,
            commit = snapshot.CommitSha,
            indexedAt = snapshot.IndexedAt.ToString("u"),
        }));
    }

    private static object Describe(DependencyChange change) => new
    {
        from = change.From,
        to = change.To,
        fromModule = change.FromModule,
        toModule = change.ToModule,
        kind = change.Kind.ToString(),
        change.Weight,
        change.CrossesBoundary,
    };
}
