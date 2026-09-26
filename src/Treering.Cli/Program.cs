using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Treering.Cli;
using Microsoft.Data.Sqlite;
using Treering.Core;

// 10분은 사람이 한 가지를 붙들고 있는 시간쯤이다. 더 짧으면 색인기가 계속 돌고,
// 더 길면 돌아와서 볼 때 이미 남의 코드처럼 보인다.
const int DefaultWatchMinutes = 10;

// serve 는 들여온 프로젝트를 스스로 최신으로 둔다 — watch 를 따로 띄울 필요가 없다.
// 끄려면 --no-watch, 주기를 바꾸려면 --watch-minutes N.
var noWatch = args.Contains("--no-watch");
var watchMinutes = DefaultWatchMinutes;
int? servePort = null;
var argv = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--no-watch") continue;
    if (args[i] == "--watch-minutes" && i + 1 < args.Length && int.TryParse(args[i + 1], out var every) && every >= 1)
    {
        watchMinutes = every;
        i++;
        continue;
    }
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var chosen) && chosen is > 0 and < 65536)
    {
        servePort = chosen;
        i++;
        continue;
    }
    argv.Add(args[i]);
}

return argv.ToArray() switch
{
    ["scan", var scip] => Scan(scip),
    ["load", var scip, var db] => Load(scip, db, ord: 0, fresh: true),
    ["snap", var scip, var db, var ord] when int.TryParse(ord, out var parsed)
        => Load(scip, db, parsed, fresh: false),
    ["patch", var scip, var db, var ord] when int.TryParse(ord, out var parsed)
        => Load(scip, db, parsed, fresh: false, partial: true),
    ["callers", var db, var name] => Callers(db, name),
    ["map", var db, var granularity] => Map(db, granularity, seed: null),
    ["map", var db, var granularity, "--own"] => Map(db, granularity, seed: null, ownOnly: true),
    ["map", var db, var granularity, var seed] => Map(db, granularity, seed),
    ["map", var db, var granularity, var seed, "--own"] => Map(db, granularity, seed, ownOnly: true),
    ["diff", var db, var from, var to] when int.TryParse(from, out var f) && int.TryParse(to, out var t)
        => Diff(db, f, t),
    ["snapshots", var db] => Snapshots(db),
    ["growth", var db, var name] => Growth(db, name),
    ["update", var repo] => Update(repo, DbForRepo(repo), since: null, restore: false),
    ["update", var repo, var db] => Update(repo, db, since: null, restore: false),
    ["update", var repo, var db, var since] => Update(repo, db, since, restore: false),
    ["update", var repo, var db, var since, "--restore"] => Update(repo, db, since, restore: true),
    ["import", var repo] => ImportRepo(repo, db: null),
    ["import", var repo, var db] => ImportRepo(repo, db),
    ["projects"] => ListProjects(),
    ["watch", var repo] => Watch(repo, DbForRepo(repo), DefaultWatchMinutes),
    ["watch", var repo, var db] => Watch(repo, db, DefaultWatchMinutes),
    ["watch", var repo, var db, var minutes] when int.TryParse(minutes, out var parsed)
        => Watch(repo, db, parsed),
    ["mcp", var db] => Mcp(db),
    ["serve"] => Serve(null, servePort ?? 7377, noWatch ? null : watchMinutes),
    ["serve", var db] => Serve(db, servePort ?? 7377, noWatch ? null : watchMinutes),
    ["serve", var db, var port] when int.TryParse(port, out var parsed) => Serve(db, parsed, noWatch ? null : watchMinutes),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  treering import  <repo> [db]             index a whole repository, any language, in one go");
    Console.Error.WriteLine("  treering projects                        list what has been imported");
    Console.Error.WriteLine("  treering scan    <index.scip>            walk the index, report statistics only");
    Console.Error.WriteLine("  treering load    <index.scip> <db>       build a graph from scratch (ord 0)");
    Console.Error.WriteLine("  treering snap    <index.scip> <db> <ord> add a snapshot (keeps the existing DB)");
    Console.Error.WriteLine("  treering patch   <index.scip> <db> <ord> swap in a partial re-index");
    Console.Error.WriteLine("  treering callers <db> <type>             find what calls this type");
    Console.Error.WriteLine("  treering map     <db> <level> [seed] [--own]  pull out a subgraph");
    Console.Error.WriteLine("       level = module | namespace | type | symbol");
    Console.Error.WriteLine("       --own leaves out what we did not write (no definition in the index)");
    Console.Error.WriteLine("  treering serve   [db] [port]             serve the local web UI (default 7377)");
    Console.Error.WriteLine("       without a db it shows every imported project, and can import more from the page");
    Console.Error.WriteLine($"       keeps imported projects current every {DefaultWatchMinutes} min; --no-watch turns that off,");
    Console.Error.WriteLine("       --watch-minutes N changes the period, --port N the port");
    Console.Error.WriteLine("  treering snapshots <db>                  list the snapshots on file");
    Console.Error.WriteLine("  treering diff    <db> <from> <to>        dependency changes between two snapshots");
    Console.Error.WriteLine("  treering growth  <db> <type>             when the callers started piling up");
    Console.Error.WriteLine("  treering update  <repo> <db> [since] [--restore]");
    Console.Error.WriteLine("       re-index only the projects that changed - C#, TypeScript/JavaScript, Python.");
    Console.Error.WriteLine("       --restore (C# only) when packages moved");
    Console.Error.WriteLine($"  treering watch   <repo> [db] [minutes]   keep it current on a timer (default {DefaultWatchMinutes})");
    Console.Error.WriteLine("       update and watch find an imported repository's DB by themselves");
    Console.Error.WriteLine("  treering mcp     <db>                    MCP server (stdio). Agents attach here");
    return 1;
}

static int Scan(string path)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"no such file: {path}");
        return 1;
    }

    var watch = Stopwatch.StartNew();
    var metadata = ScipReader.ReadMetadata(path);

    var documents = 0;
    var occurrences = 0;
    var symbolInfos = 0;
    var largestDocument = 0;
    var kinds = new Dictionary<SymbolKind, int>();

    foreach (var document in ScipReader.ReadDocuments(path))
    {
        documents++;
        occurrences += document.Occurrences.Count;
        symbolInfos += document.Symbols.Count;
        largestDocument = Math.Max(largestDocument, document.CalculateSize());

        foreach (var occurrence in document.Occurrences)
        {
            var kind = SymbolParser.Parse(occurrence.Symbol).Kind;
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
        }
    }

    watch.Stop();

    Console.WriteLine($"tool         : {metadata?.ToolInfo?.Name} {metadata?.ToolInfo?.Version}");
    Console.WriteLine($"project root : {metadata?.ProjectRoot}");
    Console.WriteLine();
    Console.WriteLine($"documents    : {documents:N0}");
    Console.WriteLine($"occurrences  : {occurrences:N0}");
    Console.WriteLine($"symbols      : {symbolInfos:N0}");
    Console.WriteLine($"largest doc  : {largestDocument:N0} bytes");
    Console.WriteLine();

    var noise = kinds.GetValueOrDefault(SymbolKind.Local) + kinds.GetValueOrDefault(SymbolKind.Namespace);
    foreach (var (kind, count) in kinds.OrderByDescending(pair => pair.Value))
    {
        Console.WriteLine($"  {kind,-14} {count,8:N0}  {100.0 * count / occurrences,5:0.0}%");
    }

    Console.WriteLine($"  -> noise {noise:N0} ({100.0 * noise / occurrences:0.0}%), edge candidates {occurrences - noise:N0}");
    Report(watch);
    return 0;
}

static int Load(string scipPath, string dbPath, int ord, bool fresh, bool partial = false)
{
    if (!File.Exists(scipPath))
    {
        Console.Error.WriteLine($"no such file: {scipPath}");
        return 1;
    }

    // load 는 처음부터 다시 만든다. 스냅샷을 쌓으려면 snap 을 쓴다.
    if (fresh && File.Exists(dbPath))
    {
        File.Delete(dbPath);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }
    }

    var watch = Stopwatch.StartNew();

    LoadStats stats;
    using (var db = GraphDb.Open(dbPath))
    {
        stats = Loader.Load(db, scipPath, ord, indexer: IndexerOf(scipPath), partial);
    }

    // WAL 이 본 파일로 넘어가야 크기가 맞게 나온다. 연결을 닫기 전에 재면 0 이 나온다.
    SqliteConnection.ClearAllPools();
    watch.Stop();

    Console.WriteLine($"documents        : {stats.Documents:N0}");
    Console.WriteLine($"symbols (unique) : {stats.Symbols:N0}");
    Console.WriteLine($"edge rows        : {stats.EdgeRows:N0}   (distinct from-to-kind)");
    Console.WriteLine($"  contains       : {stats.ContainsEdges:N0}   (read off the symbol string)");
    Console.WriteLine($"  inherit        : {stats.InheritEdges:N0}   (stated by the indexer)");
    Console.WriteLine($"attributed refs  : {stats.AttributedReferences:N0}   (inferred from position)");
    Console.WriteLine($"noise dropped    : {stats.SkippedNoise:N0}");
    Console.WriteLine($"unattributed     : {stats.UnattributedReferences:N0}   (no preceding definition = using, etc.)");
    var dbBytes = new FileInfo(dbPath).Length;
    var walPath = dbPath + "-wal";
    if (File.Exists(walPath)) dbBytes += new FileInfo(walPath).Length;
    Console.WriteLine($"DB size          : {dbBytes / 1048576.0:0.0} MB");

    if (!fresh)
    {
        Console.WriteLine();
        Console.WriteLine($"snapshot {ord} reconciled{(partial ? " (partial - only the files this index covered)" : "")}:");
        Console.WriteLine($"  born symbols : {stats.BornSymbols:N0}   died symbols : {stats.DiedSymbols:N0}");
        Console.WriteLine($"  born edges   : {stats.BornEdges:N0}   died edges   : {stats.DiedEdges:N0}");
        Console.WriteLine($"  weight moves : {stats.WeightChanges:N0}");
    }

    Report(watch);
    return 0;
}

/// <summary>
/// 색인을 만든 도구. 색인이 스스로 적은 이름과 판을 그대로 옮긴다 — 무엇으로 만들었든
/// scip-dotnet 이라고 적던 것을 고쳤다. 판은 도구가 적은 값이라 틀릴 수 있다
/// (scip-dotnet 0.2.14 는 0.1.0-SNAPSHOT 이라고 적는다).
/// </summary>
/// <summary>
/// 펼친 첫 화면을 무엇으로 묶을까. 우리 모듈이 여럿이면 모듈이 곧 무리라 여기서 할 일이 없다.
/// 하나뿐이면 그 안을 볼 만한 가지들로 나눠, 거품 하나로 끝나지 않게 한다.
/// </summary>
static List<object>? GroupsFor(SqliteConnection db, SubgraphResult result)
{
    var ours = result.Nodes.Where(node => node.Own && node.ModuleId is not null)
        .Select(node => node.ModuleId!.Value).Distinct().ToList();
    if (ours.Count != 1) return null;

    var shown = result.Nodes.Select(node => node.Id).ToHashSet();
    return Tree.Groups(db, ours[0])
        .Select(group => (Group: group, Members: group.Namespaces.Where(shown.Contains).ToList()))
        .Where(pair => pair.Members.Count > 0)
        .Select(pair => (object)new { pair.Group.Id, pair.Group.Display, pair.Members })
        .ToList();
}

/// <summary>
/// 리포 하나를 통째로 들여온다. DB 를 안 주면 사용자 폴더의 프로젝트 목록에 만든다 —
/// 리포 안에는 아무것도 쓰지 않는다.
/// </summary>
static int ImportRepo(string repo, string? db)
{
    if (!Directory.Exists(repo)) { Console.Error.WriteLine($"no such repo: {repo}"); return 1; }

    var plan = Import.Plan(repo);
    var target = db ?? Projects.DbFor(repo);
    if (db is null) Directory.CreateDirectory(Path.GetDirectoryName(target)!);

    var watch = Stopwatch.StartNew();
    var result = Import.Run(repo, target, Console.WriteLine);
    watch.Stop();
    if (!result.Ok)
    {
        Console.Error.WriteLine("import failed: nothing could be indexed.");
        return 1;
    }

    if (db is null) Projects.Record(repo, result.Languages);

    Console.WriteLine();
    Console.WriteLine($"imported {Path.GetFullPath(repo)} in {watch.Elapsed.TotalSeconds:0.0}s");
    if (result.Failed.Count > 0) Console.WriteLine($"  not indexed: {string.Join(", ", result.Failed)}");
    Console.WriteLine($"  db: {target}");
    Console.WriteLine(db is null ? "  look at it: treering serve" : $"  look at it: treering serve \"{target}\"");
    Console.WriteLine($"  keep it current: treering watch \"{Path.GetFullPath(repo)}\"");
    return 0;
}

static int ListProjects()
{
    var projects = Projects.List();
    if (projects.Count == 0)
    {
        Console.WriteLine("nothing imported yet. treering import <repo>");
        return 0;
    }

    foreach (var project in projects)
    {
        Console.WriteLine($"  {project.Id,-36} {string.Join('+', project.Languages),-16} {project.ImportedAt.LocalDateTime:yyyy-MM-dd HH:mm}  {project.Repo}");
    }

    return 0;
}

/// <summary>들여온 리포면 그 DB. 아니면 들여오라고 말한다.</summary>
static string DbForRepo(string repo)
{
    if (Projects.ForRepo(repo) is { } project) return project.Db;
    Console.Error.WriteLine($"{repo} has not been imported. treering import \"{repo}\" first, or give a db.");
    return Projects.DbFor(repo);
}

static string IndexerOf(string scipPath)
{
    var tool = ScipReader.ReadMetadata(scipPath)?.ToolInfo;
    var label = $"{tool?.Name} {tool?.Version}".Trim();
    return label.Length > 0 ? label : "unknown";
}

static int Callers(string dbPath, string name)
{
    if (!File.Exists(dbPath))
    {
        Console.Error.WriteLine($"no such DB: {dbPath}");
        return 1;
    }

    using var db = GraphDb.Open(dbPath);

    // 「이 타입을 부르는 곳」 은 두 방향으로 말아 올려야 한다.
    //  - 받는 쪽: 타입 자신뿐 아니라 그 안의 메서드·필드로 가는 참조도 이 타입을 부른 것이다.
    //  - 보내는 쪽: 참조가 붙은 메서드·필드를 감싸는 타입까지 올라간다.
    // 두 번째가 추론이 쌓이는 곳이다. 타입까지 올리면 메서드 단위의 오차가 같은 타입 안에서 상쇄된다.
    using var command = db.CreateCommand();
    command.CommandText = """
        WITH RECURSIVE
            target(id) AS (
                SELECT id FROM symbol WHERE kind = 3 AND display = $name
                UNION
                SELECT s.id FROM symbol s JOIN target t ON s.container_id = t.id
            ),
            hit(from_id, weight) AS (
                SELECT e.from_id, e.weight
                FROM edge_life e
                WHERE e.kind = 3
                  AND e.died_ord IS NULL
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
        """;
    command.Parameters.AddWithValue("$name", name);

    var rows = 0;
    var total = 0L;

    using (var reader = command.ExecuteReader())
    {
        while (reader.Read())
        {
            var weight = reader.GetInt64(2);
            total += weight;
            rows++;
            Console.WriteLine($"  {reader.GetString(0),-38} {reader.GetString(1),-32} {weight,6:N0}");
        }
    }

    if (rows == 0)
    {
        Console.WriteLine($"no type '{name}', or nothing calls it.");
        return 0;
    }

    Console.WriteLine();
    Console.WriteLine($"{total:N0} references from {rows:N0} types - all attributed by position, so method level is unreliable.");
    return 0;
}

static int Map(string dbPath, string granularityName, string? seed, bool ownOnly = false)
{
    if (!File.Exists(dbPath))
    {
        Console.Error.WriteLine($"no such DB: {dbPath}");
        return 1;
    }

    if (!Enum.TryParse<Granularity>(granularityName, ignoreCase: true, out var granularity))
    {
        Console.Error.WriteLine($"unknown level: {granularityName}");
        return 1;
    }

    using var db = GraphDb.Open(dbPath);

    // 씨앗을 안 주면 그 층 전체가 씨앗이다 — 그게 «전체 지도» 다.
    var seeds = SeedsFor(db, granularity, seed, ownOnly);
    if (seeds.Count == 0)
    {
        Console.Error.WriteLine($"no such seed: {seed}");
        return 1;
    }

    var watch = Stopwatch.StartNew();
    var result = Subgraph.Extract(db, new SubgraphRequest
    {
        Seeds = seeds,
        Granularity = granularity,
        WholeLevel = seed is null,
        OwnOnly = ownOnly,
        Depth = seed is null ? 1 : 2,
    });
    watch.Stop();

    var json = JsonSerializer.Serialize(result);

    Console.WriteLine($"seeds         : {seeds.Count:N0}");
    Console.WriteLine($"nodes         : {result.Nodes.Count:N0} / 2,000");
    Console.WriteLine($"edges         : {result.Edges.Count:N0}");
    Console.WriteLine($"depth reached : {result.DepthReached}");
    Console.WriteLine($"budget spent  : {(result.BudgetExhausted ? "yes" : "no")}");
    Console.WriteLine($"JSON size     : {json.Length / 1024.0:0.0} KB / 1,024 KB");
    Console.WriteLine($"query time    : {watch.ElapsedMilliseconds:N0} ms");

    if (result.Truncated.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("folded in by the degree cap:");
        foreach (var item in result.Truncated.Take(5))
        {
            Console.WriteLine($"  {item.Display,-40} +{item.Hidden:N0} more");
        }
    }

    Console.WriteLine();
    Console.WriteLine("heaviest edges:");
    foreach (var edge in result.Edges.OrderByDescending(e => e.Weight).Take(8))
    {
        var from = result.Nodes.First(n => n.Id == edge.From).Display;
        var to = result.Nodes.First(n => n.Id == edge.To).Display;
        var mark = edge.Inferred ? "inferred" : "exact";
        Console.WriteLine($"  {from,-30} -> {to,-32} {edge.Weight,6:N0}  {mark}");
    }

    return 0;
}

static List<long> SeedsFor(
    SqliteConnection db, Granularity granularity, string? seed, bool ownOnly = false)
{
    var column = granularity switch
    {
        Granularity.Module => "module_id",
        Granularity.Namespace => "namespace_id",
        Granularity.Type => "type_id",
        _ => "id",
    };

    var mine = ownOnly ? " AND own = 1" : string.Empty;

    using var command = db.CreateCommand();
    command.CommandText = seed is null
        ? $"SELECT DISTINCT {column} FROM symbol WHERE {column} IS NOT NULL{mine}"
        : "SELECT id FROM symbol WHERE display = $seed";

    if (seed is not null) command.Parameters.AddWithValue("$seed", seed);

    var ids = new List<long>();
    using var reader = command.ExecuteReader();
    while (reader.Read()) ids.Add(reader.GetInt64(0));
    return ids;
}

static int Snapshots(string dbPath)
{
    if (!File.Exists(dbPath)) { Console.Error.WriteLine($"no such DB: {dbPath}"); return 1; }

    using var db = GraphDb.Open(dbPath);
    foreach (var snapshot in TimeAxis.Snapshots(db))
    {
        Console.WriteLine($"  ord {snapshot.Ord,-4} {snapshot.IndexedAt.ToLocalTime():yyyy-MM-dd HH:mm}  " +
            $"{snapshot.CommitSha ?? "(no commit)",-12} {snapshot.Indexer}");
    }

    return 0;
}

static int Diff(string dbPath, int fromOrd, int toOrd)
{
    if (!File.Exists(dbPath)) { Console.Error.WriteLine($"no such DB: {dbPath}"); return 1; }

    using var db = GraphDb.Open(dbPath);

    var appeared = TimeAxis.Appeared(db, fromOrd, toOrd);
    var gone = TimeAxis.Disappeared(db, fromOrd, toOrd);
    var crossing = appeared.Where(change => change.CrossesBoundary).ToList();

    Console.WriteLine($"snapshot {fromOrd} -> {toOrd}");
    Console.WriteLine($"  new dependencies : {appeared.Count:N0}");
    Console.WriteLine($"  gone             : {gone.Count:N0}");
    Console.WriteLine($"  of those, new references across a module boundary : {crossing.Count:N0}");

    Show("dependencies that appeared", appeared);
    Show("dependencies that went away", gone);

    if (crossing.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("new references across a module boundary - watching architecture rules starts here:");
        foreach (var change in crossing.Take(10))
        {
            Console.WriteLine($"  {change.FromModule}/{change.From} -> {change.ToModule}/{change.To}");
        }
    }

    return 0;
}

static void Show(string title, List<DependencyChange> changes)
{
    if (changes.Count == 0) return;

    Console.WriteLine();
    Console.WriteLine(title + ":");
    foreach (var change in changes.Take(12))
    {
        var mark = change.Kind == EdgeKind.Inherit ? "inherit" : "reference";
        Console.WriteLine($"  {change.From,-34} -> {change.To,-34} {change.Weight,5:N0}  {mark}");
    }

    if (changes.Count > 12) Console.WriteLine($"  ... {changes.Count - 12:N0} more");
}

static int Growth(string dbPath, string name)
{
    if (!File.Exists(dbPath)) { Console.Error.WriteLine($"no such DB: {dbPath}"); return 1; }

    using var db = GraphDb.Open(dbPath);
    var points = TimeAxis.CallersOverTime(db, name);

    if (points.Count == 0)
    {
        Console.WriteLine($"no history for '{name}'.");
        return 0;
    }

    var peak = points.Max(point => point.Weight);
    foreach (var point in points)
    {
        var bar = new string('#', Math.Max(1, point.Weight * 40 / Math.Max(1, peak)));
        Console.WriteLine($"  ord {point.Ord,-4} {point.Weight,6:N0}  {bar}");
    }

    return 0;
}

static int Update(string repo, string dbPath, string? since, bool restore)
{
    if (!Directory.Exists(repo)) { Console.Error.WriteLine($"no such repo: {repo}"); return 1; }
    if (!File.Exists(dbPath)) { Console.Error.WriteLine($"no such DB: {dbPath}"); return 1; }

    return UpdateOnce(repo, dbPath, Incremental.ChangedFiles(repo, since), restore);
}

/// <summary>
/// 한 번 갱신한다. 바뀐 파일 목록을 밖에서 받는 것이 전부다 —
/// <c>update</c> 는 기준 하나로 고르고, <c>watch</c> 는 커밋과 작업 트리를 합쳐 고른다.
/// </summary>
static int UpdateOnce(string repo, string dbPath, List<string> changed, bool restore)
{
    var total = Stopwatch.StartNew();

    var affected = Incremental.AffectedProjects(repo, changed);
    if (affected.Count == 0)
    {
        Console.WriteLine("no source file in a project changed.");
        return 0;
    }

    Console.WriteLine($"{changed.Count:N0} changed files -> {affected.Count:N0} projects");
    foreach (var project in affected)
    {
        Console.WriteLine($"  {project.Name,-40} {project.Indexer.Name,-16} files {project.ChangedFiles.Count}");
    }

    // 다음 스냅샷 번호를 잇는다.
    int ord;
    using (var db = GraphDb.Open(dbPath))
    {
        var snapshots = TimeAxis.Snapshots(db);
        ord = snapshots.Count == 0 ? 0 : snapshots[^1].Ord + 1;
    }

    var scratch = Path.Combine(Path.GetTempPath(), $"treering-{Guid.NewGuid():N}");
    Directory.CreateDirectory(scratch);

    var born = 0;
    var died = 0;
    var indexing = TimeSpan.Zero;

    try
    {
        var number = 0;
        foreach (var project in affected)
        {
            // 두 프로젝트가 같은 이름일 수 있다(web 이 둘). 번호를 붙여 겹치지 않게 한다.
            var output = Path.Combine(scratch, $"{number++}-{Path.GetFileNameWithoutExtension(project.Name)}.scip");
            Console.WriteLine();
            Console.WriteLine($"indexing: {project.Name} ({project.Indexer.Name})");

            var (ok, log, elapsed) = Incremental.Index(project, output, restore);
            indexing += elapsed;

            if (!ok || !File.Exists(output))
            {
                Console.Error.WriteLine($"  indexing failed ({elapsed.TotalSeconds:0.0}s)");
                Console.Error.WriteLine(log.Length > 600 ? log[^600..] : log);
                return 1;
            }

            using var db = GraphDb.Open(dbPath);
            var gone = project.ChangedFiles
                .Select(file => Path.GetFullPath(Path.Combine(repo, file)))
                .Where(path => !File.Exists(path));
            var stats = Loader.Load(db, output, ord, indexer: IndexerOf(output), partial: true, gone);

            born += stats.BornEdges;
            died += stats.DiedEdges;
            Console.WriteLine($"  indexed in {elapsed.TotalSeconds:0.0}s · documents {stats.Documents:N0} · " +
                $"born edges {stats.BornEdges:N0} · died edges {stats.DiedEdges:N0}");
        }
    }
    finally
    {
        try { Directory.Delete(scratch, recursive: true); } catch { /* 지워지지 않아도 그만이다 */ }
    }

    // 어느 커밋을 담은 스냅샷인지 적어 둔다. 다음 주기가 여기서부터 이어 간다.
    // 다시 색인했는데 그래프가 그대로면 스냅샷을 남기지 않는다 — 시간축에 빈칸이 쌓인다.
    using (var db = GraphDb.Open(dbPath))
    {
        if (TimeAxis.DropIfEmpty(db, ord))
        {
            Console.WriteLine("nothing in the graph changed - no snapshot kept.");
            return 0;
        }

        TimeAxis.StampCommit(db, ord, Incremental.HeadSha(repo));
    }

    total.Stop();

    Console.WriteLine();
    Console.WriteLine($"snapshot {ord} written · born edges {born:N0} · died edges {died:N0}");
    Console.WriteLine($"{total.Elapsed.TotalSeconds:0.0}s all in " +
        $"(indexing {indexing.TotalSeconds:0.0}s of that - the indexer's time, not ours to cut)");

    return 0;
}

/// <summary>
/// 주기 갱신. 시간마다 「무엇이 바뀌었나」 를 묻고, 바뀐 프로젝트만 다시 색인해 스냅샷을 쌓는다.
///
/// 기준은 <b>마지막 스냅샷에 적힌 커밋</b> 이다. 상태 파일을 따로 두지 않으므로
/// 껐다 켜도 이어진다. 거기에 작업 트리의 미커밋 변경을 더한다 — 사람이 지금 고치고 있는
/// 파일이 커밋 전에는 안 보이면 「코드가 살아 있다」 를 보여 주지 못한다.
///
/// 한 주기가 실패해도 멈추지 않는다. 색인기는 반쯤 고친 코드에서 곧잘 실패하고,
/// 그때 감시가 죽어 버리면 돌아와 보니 멈춰 있는 도구가 된다.
/// </summary>
static int Watch(string repo, string dbPath, int minutes)
{
    if (!Directory.Exists(repo)) { Console.Error.WriteLine($"no such repo: {repo}"); return 1; }
    if (!File.Exists(dbPath)) { Console.Error.WriteLine($"no such DB: {dbPath}"); return 1; }

    if (minutes < 1)
    {
        Console.Error.WriteLine("the period is in whole minutes, one at the least.");
        return 1;
    }

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, signal) =>
    {
        signal.Cancel = true;
        stop.Cancel();
    };

    Console.WriteLine($"watching {Path.GetFullPath(repo)} every {minutes} min. Ctrl+C to stop.");

    string? lastSeen = null;

    while (true)
    {
        Console.WriteLine();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] looking");

        try
        {
            var changed = WatchedChanges(repo, dbPath);
            var now = Fingerprint(repo, changed);

            // 커밋하지 않은 변경은 커밋되기 전까지 매번 「바뀐 것」 으로 나온다.
            // 그대로 두면 같은 스냅샷을 10분마다 한 장씩 쌓는다.
            if (now == lastSeen)
            {
                Console.WriteLine("nothing moved since the last round.");
            }
            else
            {
                UpdateOnce(repo, dbPath, changed, restore: false);
                lastSeen = now;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"  this round failed: {error.Message}");
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] next round at {DateTime.Now.AddMinutes(minutes):HH:mm}");

        try
        {
            Task.Delay(TimeSpan.FromMinutes(minutes), stop.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("stopped.");
            return 0;
        }
    }
}

/// <summary>
/// 이번에 본 것이 지난번과 같은가. 커밋과 파일이 마지막으로 쓰인 시각을 잇는다 —
/// 내용을 읽지 않고도 「손대지 않았다」 를 알아보기에 이만하면 충분하다.
/// </summary>
static string Fingerprint(string repo, List<string> changed)
{
    var root = Path.GetFullPath(repo);
    var parts = changed
        .Order(StringComparer.Ordinal)
        .Select(file =>
        {
            var path = Path.Combine(root, file);
            return File.Exists(path)
                ? $"{file}:{File.GetLastWriteTimeUtc(path).Ticks}"
                : $"{file}:gone";
        });

    return (Incremental.HeadSha(repo) ?? "-") + "|" + string.Join('\n', parts);
}

/// <summary>마지막 스냅샷의 커밋 이후 바뀐 것 + 아직 커밋하지 않은 것.</summary>
static List<string> WatchedChanges(string repo, string dbPath)
{
    string? since;
    using (var db = GraphDb.Open(dbPath))
    {
        var snapshots = TimeAxis.Snapshots(db);
        since = snapshots.Count == 0 ? null : snapshots[^1].CommitSha;
    }

    var changed = new List<string>(Incremental.ChangedFiles(repo, null));
    if (since is not null)
    {
        foreach (var file in Incremental.ChangedFiles(repo, since))
        {
            if (!changed.Contains(file, StringComparer.OrdinalIgnoreCase)) changed.Add(file);
        }
    }

    return changed;
}

static int Mcp(string dbPath)
{
    if (!File.Exists(dbPath))
    {
        Console.Error.WriteLine($"no such DB: {dbPath}");
        return 1;
    }

    McpTools.Use(Path.GetFullPath(dbPath));

    var builder = Host.CreateApplicationBuilder();
    // stdio 가 프로토콜 통로다. 로그가 섞이면 대화가 깨지므로 stderr 로 돌린다.
    builder.Logging.ClearProviders();
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    builder.Build().Run();
    return 0;
}

/// <summary>
/// 화면을 띄운다. DB 를 주면 그것 하나만, 안 주면 들여온 프로젝트 전부 — 화면이 어느 것을
/// 볼지 <c>?project=</c> 로 고르고, 화면에서 새로 들여올 수도 있다.
/// </summary>
/// <param name="watchMinutes">들여온 프로젝트를 이만큼마다 최신으로 둔다. <c>null</c> 이면 두지 않는다.</param>
static int Serve(string? dbPath, int port, int? watchMinutes)
{
    if (dbPath is not null && !File.Exists(dbPath))
    {
        Console.Error.WriteLine($"no such DB: {dbPath}");
        return 1;
    }

    // 요청마다 어느 DB 인가. 하나만 받았으면 그것이고, 아니면 고른 프로젝트, 안 골랐으면 최근 것.
    string Resolve(string? project)
    {
        if (dbPath is not null) return dbPath;
        var chosen = project is { Length: > 0 } ? Projects.Find(project) : Projects.List().FirstOrDefault();
        return chosen?.Db ?? throw new NoProjectException();
    }

    var page = ReadEmbeddedPage();

    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    var app = builder.Build();

    // 고를 프로젝트가 없으면 404 로 말한다 — 화면은 그걸 보고 들여오기를 권한다.
    app.Use(async (context, next) =>
    {
        try { await next(); }
        catch (NoProjectException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "no project" });
        }
        catch (Exception error) when (!context.Response.HasStarted)
        {
            // 로그를 꺼 두었으니 여기서 말하지 않으면 아무도 모른다 — 500 만 남고 이유는 사라진다.
            Console.Error.WriteLine($"{context.Request.Method} {context.Request.Path}: {error.GetType().Name}: {error.Message}");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = error.Message });
        }
    });

    app.MapGet("/", () => Results.Content(page, "text/html; charset=utf-8"));

    app.MapGet("/api/projects", () => Results.Ok(new
    {
        Single = dbPath is not null,
        CanPick = dbPath is null && FolderPicker.Available,
        Projects = dbPath is not null
            ? []
            : Projects.List().Select(project => new
            {
                project.Id, project.Name, project.Repo, project.Languages,
                ImportedAt = project.ImportedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            }).ToList<object>(),
    }));

    // 들여오기. 색인기는 남의 빌드 스크립트를 돌리므로, 이 화면이 아닌 곳에서 온 요청은 받지 않는다 —
    // 아무 웹 페이지나 127.0.0.1 로 POST 를 보낼 수 있다. JSON 만 받으면 브라우저가 미리 묻고(preflight),
    // 우리는 그 물음에 답하지 않으므로 남의 페이지는 여기까지 오지 못한다. Origin 도 확인한다.
    bool FromThisPage(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        var ours = origin.Length == 0
            || origin == $"http://127.0.0.1:{port}" || origin == $"http://localhost:{port}";
        var json = context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;
        return ours && json;
    }

    app.MapPost("/api/import", async (HttpContext context) =>
    {
        if (dbPath is not null)
        {
            return Results.BadRequest(new { error = "this server shows one DB - start it with `treering serve` to import" });
        }

        if (!FromThisPage(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);

        ImportRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<ImportRequest>();
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.BadRequest(new { error = "the request body is not valid JSON - send {\"path\": \"...\"}" });
        }
        var path = request?.Path?.Trim().Trim('"');
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return Results.BadRequest(new { error = $"no such folder: {path}" });
        }

        return ImportJob.TryStart(Path.GetFullPath(path))
            ? Results.Accepted()
            : Results.Conflict(new { error = "an import is already running" });
    });

    app.MapGet("/api/import", () => Results.Ok(ImportJob.Status()));

    // 폴더 고르기 창. 이 컴퓨터의 화면에 창을 띄우는 일이라 들여오기와 같은 문을 지난다.
    // 창은 한 번에 하나 — 두 번 누르면 두 번째는 기다리지 않고 거절된다.
    var picking = 0;
    app.MapPost("/api/pick-folder", async (HttpContext context, PickRequest? request) =>
    {
        if (dbPath is not null || !FolderPicker.Available) return Results.NotFound();
        if (!FromThisPage(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (Interlocked.Exchange(ref picking, 1) == 1) return Results.Conflict(new { error = "a folder window is already open" });

        try
        {
            // 지난번에 들여온 저장소 옆에서 시작한다. 저장소는 대개 한 폴더에 모여 있다.
            var startIn = Projects.List().Select(project => Path.GetDirectoryName(project.Repo)).FirstOrDefault(Directory.Exists);
            var path = await FolderPicker.Pick(request?.Title is { Length: > 0 } title ? title : "Choose a repository", startIn);
            return Results.Ok(new { Path = path });
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"folder window failed: {error.GetType().Name}: {error.Message}");
            return Results.Json(new { error = "the folder window could not be opened - type the path instead" }, statusCode: 500);
        }
        finally
        {
            Volatile.Write(ref picking, 0);
        }
    });

    app.MapGet("/api/snapshots", (string? project) =>
    {
        using var db = GraphDb.Open(Resolve(project));
        return Results.Ok(TimeAxis.Snapshots(db).Select(snapshot => new
        {
            snapshot.Ord,
            commit = snapshot.CommitSha,
            // Stored in UTC; shown in the time of the machine it runs on, like everything else here.
            indexedAt = snapshot.IndexedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        }));
    });

    // 이름으로 찾기. 결과마다 담고 있는 모듈·네임스페이스·타입이 딸려 있어서
    // 화면이 «그게 있는 자리» 로 곧장 데려갈 수 있다.
    app.MapGet("/api/search", (string? q, int? limit, string? project) =>
    {
        using var db = GraphDb.Open(Resolve(project));
        return Results.Ok(Search.Find(db, q ?? string.Empty, limit ?? 12));
    });

    // 계층도. 누르면 펼치는 나무라 한 번에 한 층씩 준다.
    app.MapGet("/api/tree/children", (long? id, int? own, string? project) =>
    {
        using var db = GraphDb.Open(Resolve(project));
        return Results.Ok(Tree.Children(db, id, own == 1));
    });

    app.MapGet("/api/tree/path", (long id, string? project) =>
    {
        using var db = GraphDb.Open(Resolve(project));
        return Results.Ok(Tree.Path(db, id));
    });

    app.MapGet("/api/tree/links", (long id, string? project) =>
    {
        using var db = GraphDb.Open(Resolve(project));
        return Results.Ok(Tree.Links(db, id));
    });

    app.MapGet("/api/symbol", (long id, string? project) =>
    {
        using var db = GraphDb.Open(Resolve(project));
        var detail = SymbolDetail.Of(db, id);

        return detail is null
            ? Results.NotFound(new { error = $"no symbol {id}" })
            : Results.Ok(new
            {
                detail.Id,
                detail.Display,
                Kind = detail.Kind.ToString(),
                // A member says what it is by the rule the tree and the member lists use.
                Shape = detail.Kind is SymbolKind.Method or SymbolKind.Term
                    ? new Treering.Core.MemberInfo(detail.Id, detail.Display, detail.Kind, detail.Signature).Shape
                    : null,
                detail.Flavor,
                detail.Signature,
                detail.Doc,
                detail.Module,
                detail.Namespace,
                detail.File,
                detail.Line,
                Members = detail.Members.Select(member => new
                {
                    member.Id, member.Display, member.Shape, member.Signature, member.Summary,
                }),
                detail.Injects,
                detail.InjectedInto,
                detail.Implements,
                detail.ImplementedBy,
                detail.Callers,
                detail.Calls,
            });
    });

    app.MapGet("/api/map", (string? granularity, long? seed, int? depth, int? at, int? from, int? own, int? open, string? project) =>
    {
        if (!Enum.TryParse<Granularity>(granularity ?? "module", ignoreCase: true, out var level))
        {
            return Results.BadRequest(new { error = $"unknown level: {granularity}" });
        }

        // 첫 화면의 모듈 지도를 «펼쳐서» 달라는 것. 모듈 공만으로는 그 안에 무엇이 있는지가
        // 안 보이므로, 한 층 아래 네임스페이스로 답하고 모듈로 묶는 일은 화면이 한다 —
        // 우리 모듈은 거품으로 펼치고, 남의 것은 모듈 하나로 도로 접는다.
        var opened = open == 1 && seed is null && level == Granularity.Module;
        if (opened) level = Granularity.Namespace;

        using var db = GraphDb.Open(Resolve(project));

        var wholeLevel = seed is null;
        var ownOnly = own == 1;
        var seeds = seed is { } seedId
            ? Subgraph.SeedsUnder(db, seedId, level, ownOnly)
            : Subgraph.AllAt(db, level, ownOnly);

        if (seeds.Count == 0) return Results.NotFound(new { error = "no seeds" });

        var request = new SubgraphRequest
        {
            Seeds = seeds,
            Granularity = level,
            WholeLevel = wholeLevel,
            OwnOnly = ownOnly,
            // 씨앗이 이미 «보여 줄 것» 이라 한 홉이면 이웃까지 나온다.
            Depth = depth ?? 1,
            AtOrd = at,
            CompareFrom = from,
        };

        var result = Subgraph.Extract(db, request);

        // 예산을 화면에도 내보낸다. 잘린 사실과 마찬가지로 숨기지 않는다.
        return Results.Ok(new
        {
            result.Nodes,
            // 상태를 문자열로 내보낸다. 화면이 숫자를 해석하게 두지 않는다.
            Edges = result.Edges.Select(edge => new
            {
                edge.From,
                edge.To,
                Kind = (int)edge.Kind,
                edge.Weight,
                edge.Inferred,
                State = edge.State.ToString().ToLowerInvariant(),
            }),
            result.Truncated,
            result.BudgetExhausted,
            result.DepthReached,
            request.NodeBudget,
            // 누른 것 «안» 에 든 것. 한 홉 넓히면 바깥 이웃도 딸려 오므로, 화면이 누른 것과
            // 그 안의 것을 이으려면 어느 것이 안인지 알려 줘야 한다.
            Children = wholeLevel ? null : seeds,
            Opened = opened,
            Groups = opened ? GroupsFor(db, result) : null,
        });
    });

    // 들여온 프로젝트를 최신으로 둔다. DB 하나만 받았으면, 그 DB 가 들여온 프로젝트일 때만.
    IEnumerable<(string Repo, string Db, string Name)> Watched() =>
        Projects.List()
            .Where(project => dbPath is null
                || string.Equals(Path.GetFullPath(project.Db), Path.GetFullPath(dbPath), StringComparison.OrdinalIgnoreCase))
            .Select(project => (project.Repo, project.Db, project.Name));

    ServeWatcher.Configure(
        Watched,
        WatchedChanges,
        Fingerprint,
        (repo, db, changed) => UpdateOnce(repo, db, changed, restore: false),
        watchMinutes ?? DefaultWatchMinutes,
        on: watchMinutes is not null);

    app.MapGet("/api/watch", () => Results.Ok(ServeWatcher.Status()));

    // 화면의 설정에서 켜고 끈다. 색인기를 돌리는 스위치이므로 들여오기와 같은 문을 지난다.
    app.MapPost("/api/watch", async (HttpContext context) =>
    {
        if (!FromThisPage(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        WatchRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<WatchRequest>(); }
        catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "send {\"on\": true} or {\"on\": false}" }); }
        if (request?.On is not { } on) return Results.BadRequest(new { error = "send {\"on\": true} or {\"on\": false}" });
        ServeWatcher.Switch(on);
        return Results.Ok(ServeWatcher.Status());
    });

    // 로컬 전용이다. 127.0.0.1 밖으로는 듣지 않는다.
    var url = $"http://127.0.0.1:{port}";
    Console.WriteLine($"Treering is listening on {url}. Ctrl+C to stop.");
    Console.WriteLine(watchMinutes is { } minutes
        ? $"Keeping imported projects current every {minutes} min (--no-watch turns it off)."
        : "Not keeping projects current (--no-watch).");
    app.Run(url);
    return 0;
}

static string ReadEmbeddedPage()
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")
        ?? throw new InvalidOperationException("index.html is not embedded in this executable.");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}

static void Report(Stopwatch watch)
{
    Console.WriteLine();
    Console.WriteLine($"elapsed          : {watch.ElapsedMilliseconds:N0} ms");
    Console.WriteLine($"peak working set : {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:0.0} MB");
}

/// <summary>고를 프로젝트가 없다. 화면은 404 를 보고 들여오기를 권한다.</summary>
sealed class NoProjectException : Exception;

sealed record ImportRequest(string? Path);

sealed record PickRequest(string? Title);

sealed record WatchRequest(bool? On);

/// <summary>
/// serve 안에서 도는 watch. 들여온 프로젝트를 차례로 보고, 지난번과 달라진 것만 다시 색인해
/// 스냅샷을 쌓는다 — <c>treering watch</c> 와 같은 일을, 따로 띄우지 않아도 하도록.
/// 켜고 끄는 것은 화면의 설정이나 <c>--no-watch</c>. 꺼도 서버는 그대로 돈다.
/// </summary>
static class ServeWatcher
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, string> LastSeen = new(StringComparer.OrdinalIgnoreCase);
    private static Func<IEnumerable<(string Repo, string Db, string Name)>> _targets = () => [];
    private static Func<string, string, List<string>> _changes = (_, _) => [];
    private static Func<string, List<string>, string> _fingerprint = (_, _) => "";
    private static Action<string, string, List<string>> _update = (_, _, _) => { };
    private static CancellationTokenSource _wake = new();
    private static int _minutes;
    private static bool _on;
    private static bool _busy;
    private static DateTime? _lastRound;
    private static DateTime? _nextRound;
    private static string? _current;
    private static DateTimeOffset? _currentSince;

    public static void Configure(
        Func<IEnumerable<(string Repo, string Db, string Name)>> targets,
        Func<string, string, List<string>> changes,
        Func<string, List<string>, string> fingerprint,
        Action<string, string, List<string>> update,
        int minutes,
        bool on)
    {
        _targets = targets;
        _changes = changes;
        _fingerprint = fingerprint;
        _update = update;
        _minutes = minutes;
        _on = on;
        Task.Run(Loop);
    }

    public static void Switch(bool on)
    {
        lock (Gate)
        {
            _on = on;
            if (!on) _nextRound = null;
        }
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] keeping projects current: {(on ? "on" : "off")}");
        if (on) Wake();   // switched back on: look now, not in ten minutes
    }

    public static object Status()
    {
        lock (Gate)
        {
            return new
            {
                On = _on, Minutes = _minutes, Busy = _busy, Current = _current,
                CurrentSince = _currentSince?.ToUnixTimeMilliseconds(),
                LastRound = _lastRound?.ToString("HH:mm"), NextRound = _nextRound?.ToString("HH:mm"),
            };
        }
    }

    private static void Wake()
    {
        CancellationTokenSource old;
        lock (Gate)
        {
            old = _wake;
            _wake = new CancellationTokenSource();
        }
        old.Cancel();
    }

    private static async Task Loop()
    {
        while (true)
        {
            bool on;
            lock (Gate) on = _on;
            if (on) Round();

            CancellationToken wake;
            lock (Gate)
            {
                _nextRound = _on ? DateTime.Now.AddMinutes(_minutes) : null;
                wake = _wake.Token;
            }

            try { await Task.Delay(TimeSpan.FromMinutes(_minutes), wake); }
            catch (OperationCanceledException) { /* switched on again - go round now */ }
        }
    }

    private static void Round()
    {
        lock (Gate) _busy = true;
        try
        {
            foreach (var (repo, db, name) in _targets().ToList())
            {
                lock (Gate)
                {
                    if (!_on) return;
                    _current = name;
                    _currentSince = null;
                }

                // An import rewrites its DB from scratch; leave everything alone while one runs.
                if (ImportJob.Running) return;
                if (!Directory.Exists(repo) || !File.Exists(db)) continue;

                try
                {
                    var changed = _changes(repo, db);
                    var seen = _fingerprint(repo, changed);
                    if (LastSeen.TryGetValue(db, out var last) && last == seen) continue;

                    lock (Gate) _currentSince = DateTimeOffset.Now;   // from here on it is really indexing

                    Console.WriteLine();
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {name}: looking");
                    _update(repo, db, changed);
                    LastSeen[db] = seen;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {name}: this round failed: {error.Message}");
                }
            }
        }
        finally
        {
            lock (Gate)
            {
                _busy = false;
                _current = null;
                _currentSince = null;
                _lastRound = DateTime.Now;
            }
        }
    }
}

/// <summary>
/// 화면에서 시킨 들여오기. 한 번에 하나만 돌고, 화면은 <c>GET /api/import</c> 로 진행을 본다.
/// 색인은 몇십 초에서 몇 분이라 요청 안에서 기다리게 할 수 없다.
/// </summary>
static class ImportJob
{
    private static readonly Lock Gate = new();
    private static readonly List<string> Log = [];
    private static IReadOnlyList<StepProgress> _steps = [];
    private static int _symbols;
    private static int _edges;
    private static bool _running;
    private static bool? _ok;
    private static string? _project;
    private static string? _repo;

    public static bool TryStart(string repo)
    {
        lock (Gate)
        {
            if (_running) return false;
            _running = true;
            _ok = null;
            _project = null;
            _repo = repo;
            _steps = [];
            _symbols = _edges = 0;
            Log.Clear();
        }

        Task.Run(() =>
        {
            try
            {
                var db = Projects.DbFor(repo);
                Directory.CreateDirectory(Path.GetDirectoryName(db)!);
                var result = Import.Run(repo, db, Say, steps => { lock (Gate) _steps = steps; });
                string? id = null;
                if (result.Ok) id = Projects.Record(repo, result.Languages).Id;
                lock (Gate) { _ok = result.Ok; _project = id; _symbols = result.Symbols; _edges = result.Edges; }
            }
            catch (Exception error)
            {
                Say("failed: " + error.Message);
                lock (Gate) _ok = false;
            }
            finally
            {
                lock (Gate) _running = false;
            }
        });

        return true;
    }

    private static void Say(string line)
    {
        lock (Gate) Log.Add(line);
    }

    public static bool Running
    {
        get { lock (Gate) return _running; }
    }

    public static object Status()
    {
        lock (Gate)
        {
            return new
            {
                Running = _running, Ok = _ok, Project = _project, Repo = _repo,
                Steps = _steps.Select(step => new
                {
                    step.Name, step.Language, step.Indexer, State = step.State.ToString().ToLowerInvariant(),
                    step.Reason, step.Detail, Seconds = Math.Round(step.Seconds, 1), step.Symbols,
                    step.Phase, StartedAt = step.StartedAt?.ToUnixTimeMilliseconds(), step.ExpectedSeconds,
                }).ToList(),
                Symbols = _symbols, Edges = _edges,
                Log = Log.ToList(),
            };
        }
    }
}
