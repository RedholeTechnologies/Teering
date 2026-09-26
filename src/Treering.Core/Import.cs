using Microsoft.Data.Sqlite;

namespace Treering.Core;

/// <summary>임포트가 돌릴 색인 하나 — 어느 색인기로, 무엇을.</summary>
public sealed record ImportStep(Indexer Indexer, string Target)
{
    public string Name => Path.GetFileName(Target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

/// <summary>들여오기 한 걸음이 어디까지 왔나.</summary>
public enum StepState { Waiting, Running, Done, Skipped, Failed }

/// <summary>
/// 들여오기 한 걸음의 형편. 화면은 이것으로 목록을 그린다 — 로그 줄을 읽어 짐작하지 않도록.
/// 왜 멈췄는지는 <see cref="Reason"/> 코드로 주고, 사람이 읽을 말은 화면이 제 언어로 고른다.
/// </summary>
/// <param name="Reason"><c>not-installed</c>, <c>indexer-failed</c>.</param>
/// <param name="Detail">이유에 딸린 것 — 설치 명령, 색인기가 남긴 마지막 줄.</param>
/// <param name="Phase">돌고 있는 동안, 색인기가 마지막으로 한 말. 「지금 어디쯤」 이다.</param>
/// <param name="StartedAt">돌기 시작한 때. 화면이 흐른 시간을 센다.</param>
/// <param name="ExpectedSeconds">지난번에 같은 걸음이 걸린 시간. 진행 막대의 기준이다.</param>
public sealed record StepProgress(
    string Name,
    string Language,
    string Indexer,
    StepState State,
    string? Reason = null,
    string? Detail = null,
    double Seconds = 0,
    int Symbols = 0,
    string? Phase = null,
    DateTimeOffset? StartedAt = null,
    double? ExpectedSeconds = null);

/// <summary>임포트가 무엇을 했는가.</summary>
public sealed record ImportResult(
    bool Ok,
    IReadOnlyList<string> Indexed,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Languages,
    int Symbols,
    int Edges);

/// <summary>
/// 리포 하나를 통째로 들여온다 — 무슨 언어가 있는지 보고, 언어마다 맞는 색인기를 맞는
/// 인자로 돌리고, 결과를 DB 하나에 쌓고, 커밋을 적는다.
///
/// 사람이 할 일을 줄이는 것이 전부다. 색인기마다 부르는 법이 다르고
/// (<c>--allow-global-symbol-definitions</c> 를 빠뜨리면 그래프가 갈린다), 여러 언어가 섞이면
/// 적재도 여러 번 해야 한다. 그걸 매번 손으로 하게 두면 아무도 안 쓴다.
/// </summary>
public static class Import
{
    // 색인할 소스가 아닌 곳. 여기를 뒤지면 남의 package.json 수백 개를 프로젝트로 착각한다.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".venv", "venv", "env", "dist", "build", "out",
        "__pycache__", ".next", ".treering", "coverage", ".idea", ".vs",
    };

    /// <summary>
    /// 무엇을 색인할지 정한다. 실행하지 않는다 — 계획을 먼저 보여 줄 수 있게.
    ///
    ///  - C#: 루트의 솔루션(.slnx · .sln). 없으면 가장 위에 있는 솔루션, 그것도 없으면
    ///    프로젝트마다. 솔루션 하나로 색인해야 프로젝트 사이 참조가 한 번에 풀린다.
    ///  - TypeScript · JavaScript: 가장 위에 있는 package.json 마다. 그 안에 든 package.json 은
    ///    위의 것이 이미 덮는다(워크스페이스는 색인기가 따로 안다).
    ///  - Python: 가장 위에 있는 pyproject.toml · setup.py 마다. 없는데 .py 가 있으면 리포 루트.
    ///
    /// 그 언어의 소스가 한 줄도 없으면 계획에 넣지 않는다.
    /// </summary>
    public static List<ImportStep> Plan(string repo)
    {
        var root = Path.GetFullPath(repo);
        var files = Walk(root).ToList();
        var steps = new List<ImportStep>();

        bool Has(Indexer indexer) => files.Any(file =>
            indexer.Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));

        if (Has(Incremental.CSharp))
        {
            var solutions = files.Where(IsSolution).ToList();
            var atRoot = solutions.Where(file => Path.GetDirectoryName(file) == root).ToList();
            var chosen = atRoot.Count > 0 ? atRoot : Topmost(solutions);
            if (chosen.Count == 0) chosen = files.Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();

            // A root with both an .sln and an .slnx describes one solution twice.
            var slnx = chosen.Where(file => file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.ChangeExtension(file, null)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            chosen = chosen.Where(file => !(file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                && slnx.Contains(Path.ChangeExtension(file, null)))).ToList();

            steps.AddRange(chosen.Order(StringComparer.Ordinal).Select(file => new ImportStep(Incremental.CSharp, file)));
        }

        if (Has(Incremental.TypeScript))
        {
            var packages = Topmost(files.Where(file => Path.GetFileName(file) == "package.json").ToList());
            steps.AddRange(packages
                .Select(file => Path.GetDirectoryName(file)!)
                .Order(StringComparer.Ordinal)
                .Select(folder => new ImportStep(Incremental.TypeScript, folder)));
        }

        if (Has(Incremental.Python))
        {
            var markers = Topmost(files.Where(file => Incremental.Python.Markers.Contains(Path.GetFileName(file))).ToList());
            var folders = markers.Select(file => Path.GetDirectoryName(file)!).Distinct().ToList();
            if (folders.Count == 0) folders.Add(root);
            steps.AddRange(folders.Order(StringComparer.Ordinal).Select(folder => new ImportStep(Incremental.Python, folder)));
        }

        return steps;
    }

    /// <summary>
    /// 계획대로 색인하고 적재한다. 첫 색인은 새로 만들고, 나머지는 같은 스냅샷에 부분 적재로
    /// 얹는다 — 부분 적재는 그 색인이 덮은 파일만 맞춰 보므로 서로를 지우지 않는다.
    /// 한 언어가 실패해도 나머지는 들여온다. 무엇이 실패했는지는 돌려준다.
    /// </summary>
    public static ImportResult Run(string repo, string dbPath, Action<string> say, Action<IReadOnlyList<StepProgress>>? progress = null)
    {
        var steps = Plan(repo);
        if (steps.Count == 0)
        {
            say("nothing to index: no C#, TypeScript, JavaScript or Python source found.");
            progress?.Invoke([]);
            return new ImportResult(false, [], [], [], 0, 0);
        }

        foreach (var step in steps) say($"plan: {step.Indexer.Name} {step.Name}");

        // 걸음마다 지난번에 걸린 시간. DB 옆에 둔다 — 같은 리포를 다시 들여오면 진행 막대가 그걸 기준으로 찬다.
        var timesPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "index-times.json");
        var times = ReadTimes(timesPath);
        var board = steps.Select(step => new StepProgress(step.Name, step.Indexer.Language, step.Indexer.Name, StepState.Waiting,
            ExpectedSeconds: times.TryGetValue(step.Name, out var before) ? before : null)).ToArray();
        void Mark(int i, StepProgress now)
        {
            board[i] = now;
            progress?.Invoke(board.ToArray());
        }
        progress?.Invoke(board.ToArray());

        var scratch = Path.Combine(Path.GetTempPath(), $"treering-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var indexed = new List<string>();
        var failed = new List<string>();
        var languages = new List<string>();
        var loadedAny = false;

        try
        {
            if (File.Exists(dbPath))
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
                }
            }

            var number = 0;
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var output = Path.Combine(scratch, $"{number++}-{Path.GetFileNameWithoutExtension(step.Name)}.scip");
                say($"indexing {step.Name} with {step.Indexer.Name}…");
                Mark(i, board[i] with { State = StepState.Running, StartedAt = DateTimeOffset.Now });

                // A first import restores C# packages: nothing says they are there yet.
                var project = new AffectedProject(step.Indexer, step.Target, []);
                // 색인기가 말하는 대로 「지금 어디쯤」 을 옮긴다. 줄마다 알리면 너무 잦으니 반 초에 한 번.
                var index = i;
                var lastSaid = DateTime.MinValue;
                var timedOnly = step.Indexer == Incremental.Python;
                void Said(string text)
                {
                    // scip-python announces each stage with a "(15:10:31)" stamp; its other lines are
                    // chatter - "Python script failed with code null" is a harmless fallback that reads
                    // like an error. Only the stamped lines say where it is.
                    if (timedOnly && !IsStamped(text)) return;
                    if ((DateTime.UtcNow - lastSaid).TotalMilliseconds < 500) return;
                    lastSaid = DateTime.UtcNow;
                    Mark(index, board[index] with { Phase = PhaseOf(text) });
                }

                var (ok, log, elapsed) = Incremental.Index(project, output, restore: true, line: Said);
                if (!ok || !File.Exists(output))
                {
                    say($"  failed after {elapsed.TotalSeconds:0.0}s: {Tail(log)}");
                    var missing = log.StartsWith($"{step.Indexer.Name} is not installed.", StringComparison.Ordinal);
                    Mark(i, board[i] with
                    {
                        State = StepState.Failed,
                        Reason = missing ? "not-installed" : "indexer-failed",
                        Detail = missing ? step.Indexer.Install : Tail(log),
                        Seconds = elapsed.TotalSeconds,
                    });
                    failed.Add(step.Name);
                    continue;
                }

                using var db = GraphDb.Open(dbPath);
                var stats = Loader.Load(db, output, ord: 0, indexer: IndexerOf(output), partial: loadedAny);
                loadedAny = true;
                indexed.Add(step.Name);
                if (!languages.Contains(step.Indexer.Language)) languages.Add(step.Indexer.Language);
                say($"  indexed in {elapsed.TotalSeconds:0.0}s · {stats.Documents:N0} documents · {stats.Symbols:N0} symbols");
                Mark(i, board[i] with { State = StepState.Done, Phase = null, Seconds = elapsed.TotalSeconds, Symbols = stats.Symbols });
                times[step.Name] = Math.Round(elapsed.TotalSeconds, 1);
                WriteTimes(timesPath, times);
            }

            if (!loadedAny) return new ImportResult(false, indexed, failed, languages, 0, 0);

            using (var db = GraphDb.Open(dbPath))
            {
                TimeAxis.StampCommit(db, 0, Incremental.HeadSha(repo));
                var (symbols, edges) = Count(db);
                say($"done: {symbols:N0} symbols, {edges:N0} edges.");
                return new ImportResult(true, indexed, failed, languages, symbols, edges);
            }
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* 지워지지 않아도 그만이다 */ }
        }
    }

    /// <summary>
    /// 색인기의 한 줄을 화면에 보일 만하게. scip-python 이 앞에 붙이는 <c>(15:10:31)</c> 시각은 뗀다
    /// — 화면이 따로 시간을 센다. 너무 긴 줄은 자른다.
    /// </summary>
    public static string PhaseOf(string line)
    {
        var text = line.Trim();
        if (IsStamped(text)) text = text[10..].Trim();
        return text.Length > 120 ? text[..117] + "…" : text;
    }

    /// <summary>Starts with a <c>(HH:MM:SS)</c> stamp, as scip-python's stage lines do.</summary>
    public static bool IsStamped(string line)
    {
        var text = line.Trim();
        return text.Length > 10 && text[0] == '(' && text[9] == ')' && text[3] == ':' && text[6] == ':';
    }

    private static Dictionary<string, double> ReadTimes(string path)
    {
        try
        {
            return File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static void WriteTimes(string path, Dictionary<string, double> times)
    {
        try { File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(times)); }
        catch (IOException) { /* 다음에 기준이 없을 뿐이다 */ }
    }

    private static string IndexerOf(string scipPath)
    {
        var tool = ScipReader.ReadMetadata(scipPath)?.ToolInfo;
        var label = $"{tool?.Name} {tool?.Version}".Trim();
        return label.Length > 0 ? label : "unknown";
    }

    private static (int Symbols, int Edges) Count(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM symbol), (SELECT COUNT(*) FROM edge_life WHERE died_ord IS NULL)";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static string Tail(string log)
    {
        var text = log.Trim().Replace("\r", "");
        var last = text.Split('\n').LastOrDefault(line => line.Trim().Length > 0) ?? text;
        return last.Length > 300 ? last[^300..] : last;
    }

    private static bool IsSolution(string file) =>
        file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);

    /// <summary>다른 것의 폴더 안에 들어 있지 않은 것만. 가장 위에 있는 것들이다.</summary>
    private static List<string> Topmost(List<string> files)
    {
        var folders = files.Select(file => Path.GetDirectoryName(file)! + Path.DirectorySeparatorChar).ToList();
        return files
            .Where((file, i) => !folders.Where((_, j) => j != i)
                .Any(other => folders[i].Length > other.Length && folders[i].StartsWith(other, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>리포 어딘가에 그 언어의 프로젝트 표지가 있나. 하나 찾으면 멈춘다.</summary>
    internal static bool HasMarker(string root, Indexer indexer) =>
        Walk(Path.GetFullPath(root)).Any(file => indexer.Markers.Contains(Path.GetFileName(file)));

    private static IEnumerable<string> Walk(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(folder); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (!Skipped.Contains(Path.GetFileName(entry))) pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }
}
