using Microsoft.Data.Sqlite;
using Scip;

namespace Treering.Core;

public readonly record struct LoadStats(
    int Documents,
    int Symbols,
    int EdgeRows,
    int ContainsEdges,
    int InheritEdges,
    int AttributedReferences,
    int SkippedNoise,
    int UnattributedReferences,
    int BornEdges,
    int DiedEdges,
    int BornSymbols,
    int DiedSymbols,
    int WeightChanges,
    int ReattachedOrphans);

/// <summary>
/// index.scip 를 읽어 그래프로 바꾼다. 문서를 하나씩 받아 처리하고 바로 버린다 —
/// 힙에 남는 것은 심볼 인턴 표와 간선 집계뿐이고, 둘 다 리포 크기가 아니라
/// «서로 다른 심볼 수» 에 비례한다.
///
/// 같은 DB 에 스냅샷을 거듭 쌓을 수 있다. 두 번째부터는 그래프를 복제하지 않고
/// 살아 있는 것과 «대조» 한다 — 새로 난 것만 태우고, 사라진 것에 died_ord 를 찍는다.
/// 커밋 하나가 실제로 건드리는 것은 수십~수백이므로 저장량이 변경량을 따라간다.
/// </summary>
public sealed class Loader
{
    private readonly SqliteConnection _db;
    private readonly int _ord;
    private bool _partial;
    private HashSet<long>? _ownedCache;
    private PythonModuleNames _moduleNames = PythonModuleNames.None;

    /// <summary>이 색인이 제 패키지를 통째로 덮는가. scip-python 은 프로젝트 하나를 언제나 전부 색인한다.</summary>
    private bool _wholePackage;

    /// <summary>이번 색인이 덮은 파일. 부분 적재일 때 대조 범위가 된다.</summary>
    private readonly HashSet<long> _scope = [];

    /// <summary>이번 색인의 문서 경로와, 그 안에 정의를 둔 패키지. 사라진 파일을 가려낼 때 쓴다.</summary>
    private readonly List<string> _documentPaths = [];
    private readonly HashSet<string> _definedPackages = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Interned> _symbols = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Interned> _byId = [];
    private readonly Dictionary<string, long> _packageIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fileIds = new(StringComparer.Ordinal);

    private readonly Dictionary<(long From, long To, EdgeKind Kind), int> _edges = [];
    private readonly Dictionary<(long Symbol, long File), int> _definitions = [];
    private readonly Dictionary<long, string> _versions = [];
    private readonly HashSet<long> _seen = [];

    private int _inheritEdges;
    private int _referenceEdges;
    private int _skippedNoise;
    private int _unattributed;
    private int _bornEdges;
    private int _diedEdges;
    private int _bornSymbols;
    private int _diedSymbols;
    private int _weightChanges;
    private int _reattached;

    private Loader(SqliteConnection db, int ord)
    {
        _db = db;
        _ord = ord;
    }

    /// <summary>
    /// 색인 하나를 스냅샷 <paramref name="ord"/> 로 적재한다.
    ///
    /// <paramref name="partial"/> 가 참이면 <b>이 색인이 덮는 파일 안에서만</b> 대조한다.
    /// 증분 갱신에서 프로젝트 하나만 다시 색인했을 때, 손대지 않은 코드가
    /// 「사라졌다」 로 기록되지 않게 하는 것이 이 구분의 전부다.
    /// </summary>
    /// <param name="gone">
    /// 부분 적재에서, 지워진 파일의 절대 경로. 지워진 파일은 새 색인에 없으므로 이것을
    /// 따로 받지 않으면 범위에 들지 못하고, 그 안에 정의됐던 것은 영영 살아 있다.
    /// </param>
    public static LoadStats Load(
        SqliteConnection db, string scipPath, int ord, string indexer, bool partial = false,
        IEnumerable<string>? gone = null)
    {
        var loader = new Loader(db, ord) { _partial = partial };

        // 한 번 먼저 훑어 배운다. 문서 하나씩 흘려 읽으므로 메모리는 그대로다.
        if (ScipReader.ReadMetadata(scipPath)?.ToolInfo?.Name == "scip-python")
        {
            loader._moduleNames = PythonModuleNames.Learn(
                ScipReader.ReadDocuments(scipPath), PythonIndexing.ReadPackages(scipPath + ".packages.json"), PythonIndexing.StdlibModules());
            loader._wholePackage = true;
        }

        using var transaction = db.BeginTransaction();
        loader.InsertSnapshot(indexer);
        loader.LoadExisting();
        var root = partial ? ProjectRootOf(scipPath) : null;
        if (partial && gone is not null) loader.ScopeGone(root, gone);

        var documents = 0;
        foreach (var document in ScipReader.ReadDocuments(scipPath))
        {
            loader.LoadDocument(document);
            documents++;
        }

        if (partial) loader.ScopeMissing(root);

        loader.Reconcile();
        loader.ReattachOrphans();
        loader.CarryTypeDown();
        loader.MendContainment();
        loader.DropEmptyPlaceholders();

        // 되붙이기가 끝난 뒤에 정한다. 되붙인 멤버는 그 전까지 가짜 타입에 매달려 있다.
        GraphDb.MarkOwn(db);

        // 층이 다 정해진 뒤에 만다. 그 전에 말면 가짜 타입으로 말려 올라간 간선이 박힌다.
        GraphDb.RollEdges(db, ord);
        transaction.Commit();

        // 인덱스는 지금 만든다. 적재 중에 들고 있으면 행마다 갱신 비용을 낸다.
        GraphDb.CreateIndexes(db);

        return new LoadStats(
            documents,
            loader._symbols.Count,
            loader._edges.Count,
            loader._edges.Count(pair => pair.Key.Kind == EdgeKind.Contains),
            loader._inheritEdges,
            loader._referenceEdges,
            loader._skippedNoise,
            loader._unattributed,
            loader._bornEdges,
            loader._diedEdges,
            loader._bornSymbols,
            loader._diedSymbols,
            loader._weightChanges,
            loader._reattached);
    }

    private void InsertSnapshot(string indexer)
    {
        using var command = _db.CreateCommand();
        command.CommandText = """
            INSERT INTO snapshot(ord, indexed_at, indexer) VALUES ($ord, $at, $indexer)
            ON CONFLICT(ord) DO UPDATE SET indexed_at = excluded.indexed_at
            """;
        command.Parameters.AddWithValue("$ord", _ord);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$indexer", indexer);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 이미 들어 있는 심볼을 인턴 표로 끌어온다. 두 번째 스냅샷부터는 같은 심볼이
    /// 같은 id 로 이어져야 시간축이 의미를 갖는다.
    /// </summary>
    private void LoadExisting()
    {
        using (var command = _db.CreateCommand())
        {
            command.CommandText =
                "SELECT key, id, container_id, type_id, namespace_id, module_id FROM symbol";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var interned = new Interned(
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5));

                _symbols[reader.GetString(0)] = interned;
                _byId[interned.Id] = interned;
            }
        }

        Cache("SELECT name, id FROM package", _packageIds);
        Cache("SELECT path, id FROM file", _fileIds);
    }

    /// <summary>
    /// 이번 적재가 «임자» 인 심볼들.
    ///
    /// 전량이면 전부다. 부분이면 <b>이 색인이 정의를 가진 심볼과 그 조상들</b> 뿐이다 —
    /// 범위를 한쪽만 좁히면(살아 있는 것만 좁히고 이번에 본 것은 안 좁히면)
    /// 범위 밖 외부 심볼이 매번 새로 태어난 것으로 기록된다. 실측으로 183개가 그렇게 샜다.
    ///
    /// 조상까지 넣는 이유: 새 타입이 생기면 그것을 담는 네임스페이스도 이번 적재가 임자다.
    /// 반대로 <c>System/IComparable#</c> 같은 외부 심볼은 여기 정의가 없으므로 손대지 않는다.
    /// </summary>
    private HashSet<long> Owned()
    {
        if (!_partial) return _seen;

        if (_ownedCache is not null) return _ownedCache;

        // 씨앗은 «이 색인이 덮은 파일» 이다. 이번에 찾은 정의만으로 잡으면,
        // 파일에서 타입이 통째로 지워졌을 때 범위가 비어 삭제를 영영 못 잡는다.
        var seeds = new HashSet<long>(_definitions.Keys.Select(key => key.Symbol));

        using (var command = _db.CreateCommand())
        {
            command.CommandText = """
                SELECT symbol_id FROM definition
                WHERE died_ord IS NULL AND file_id IN (SELECT value FROM json_each($scope))
                """;
            command.Parameters.AddWithValue("$scope", Json(_scope));

            using var reader = command.ExecuteReader();
            while (reader.Read()) seeds.Add(reader.GetInt64(0));
        }

        // 우리 패키지 이름을 달았지만 어디에도 정의가 없는 것 — 참조로만 생긴 심볼이다. 색인기가 우리 이름을
        // 잘못 붙인 라이브러리 것이거나, 지운 코드가 import 이름으로 부르던 것이다. 정의가 없으니 위의 씨앗에
        // 영영 들지 못해, 아무도 부르지 않게 된 뒤에도 살아 있었다. 색인이 패키지를 통째로 덮을 때만 거둔다 —
        // 그때는 «이번에 안 보였다» 가 곧 «이제 없다» 다.
        if (_wholePackage && _definedPackages.Count > 0)
        {
            using var command = _db.CreateCommand();
            command.CommandText = """
                SELECT s.id FROM symbol s
                JOIN package p ON p.id = s.package_id
                WHERE p.name IN (SELECT value FROM json_each($packages))
                  AND EXISTS (SELECT 1 FROM symbol_life l WHERE l.symbol_id = s.id AND l.died_ord IS NULL)
                  AND NOT EXISTS (SELECT 1 FROM definition d WHERE d.symbol_id = s.id AND d.died_ord IS NULL)
                """;
            command.Parameters.AddWithValue("$packages", System.Text.Json.JsonSerializer.Serialize(_definedPackages));

            using var reader = command.ExecuteReader();
            while (reader.Read()) seeds.Add(reader.GetInt64(0));
        }

        var owned = new HashSet<long>();
        foreach (var symbolId in seeds)
        {
            var current = symbolId;
            while (owned.Add(current))
            {
                if (!_byId.TryGetValue(current, out var node)) break;
                if (node.ContainerId is not { } parent) break;
                current = parent;
            }
        }

        _ownedCache = owned;
        return owned;
    }

    /// <summary>
    /// id 목록을 <c>json_each</c> 에 넘길 배열 문자열로. 정수뿐이라 직렬화기가 필요 없고,
    /// NativeAOT 에서 리플렉션 기반 직렬화가 막히는 문제도 함께 피한다.
    /// </summary>
    private static string Json(IEnumerable<long> ids) => "[" + string.Join(',', ids) + "]";

    private void Cache(string sql, Dictionary<string, long> into)
    {
        using var command = _db.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read()) into[reader.GetString(0)] = reader.GetInt64(1);
    }

    private ParsedSymbol Parse(string symbol) => SymbolParser.Parse(_moduleNames.Canonical(symbol));

    private void LoadDocument(Document document)
    {
        var fileId = InternFile(document.RelativePath);
        _scope.Add(fileId);
        _documentPaths.Add(document.RelativePath);
        var definitions = new List<(int Line, int Char, long SymbolId)>();

        foreach (var occurrence in document.Occurrences)
        {
            if ((occurrence.SymbolRoles & (int)SymbolRole.Definition) == 0) continue;

            var parsed = Parse(occurrence.Symbol);
            if (!CanBeEdgeEndpoint(parsed)) continue;
            if (parsed.HasPackage) _definedPackages.Add(parsed.Package);

            var symbolId = InternSymbol(parsed).Id;
            var (line, character) = PositionOf(occurrence);
            definitions.Add((line, character, symbolId));
            _definitions[(symbolId, fileId)] = line;
        }

        definitions.Sort(static (left, right) =>
            left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Char.CompareTo(right.Char));

        // 상속·구현. SCIP 가 직접 주는 유일한 심볼 간 관계라 추정이 아니다.
        foreach (var information in document.Symbols)
        {
            var parsed = Parse(information.Symbol);
            if (!CanBeEdgeEndpoint(parsed)) continue;

            var fromId = InternSymbol(parsed).Id;

            // 타입의 종류(class·interface·enum…)와 멤버의 선언은 여기서만 알 수 있다.
            // SCIP 의 kind 는 비어 있고 documentation 에 컴파일러가 적은 것이 들어 있다.
            var flavor = parsed.Kind == SymbolKind.Type
                ? SymbolParser.FlavorOf(information.Documentation)
                : null;
            var signature = SymbolParser.SignatureOf(information.Documentation);
            var doc = DocComment.Of(information.Documentation);

            if (flavor is not null || signature is not null || doc is not null)
            {
                Describe(fromId, flavor, signature, doc);
            }

            foreach (var relationship in information.Relationships)
            {
                if (!relationship.IsImplementation) continue;

                var target = Parse(relationship.Symbol);
                if (!CanBeEdgeEndpoint(target)) continue;

                AddEdge(fromId, InternSymbol(target).Id, EdgeKind.Inherit);
                _inheritEdges++;
            }
        }

        // 참조. enclosing_symbol 이 비어 있으므로 위치로 «누가» 를 추론한다.
        foreach (var occurrence in document.Occurrences)
        {
            if ((occurrence.SymbolRoles & (int)SymbolRole.Definition) != 0) continue;

            var parsed = Parse(occurrence.Symbol);
            if (!CanBeEdgeEndpoint(parsed))
            {
                _skippedNoise++;
                continue;
            }

            var (line, character) = PositionOf(occurrence);
            var fromId = EnclosingDefinition(definitions, line, character);
            if (fromId is null)
            {
                // 파일 상단의 using 지시문처럼 어떤 정의보다도 앞선 참조.
                _unattributed++;
                continue;
            }

            AddEdge(fromId.Value, InternSymbol(parsed).Id, EdgeKind.Reference);
            _referenceEdges++;
        }
    }

    /// <summary>
    /// 색인기가 주인을 못 찾은 멤버를 위치로 되붙인다.
    ///
    /// <b>색인기가 언어를 따라오지 못할 때 생긴다.</b> C# 12 의 기본 생성자
    /// (<c>class OrderRepository(ShopDbContext db)</c>)로 선언한 클래스의 멤버를
    /// scip-dotnet 0.2.14 가 묶지 못하고 <c>&lt;invalid-global-code&gt;</c> 라는 가짜 타입 밑으로 보낸다.
    /// 컴파일 오류 때문이 아니다 — 실측한 리포는 경고 0개로 빌드된다.
    ///
    /// 진짜 주인은 <b>같은 파일에서 가장 가까운 앞선 타입 정의</b> 다. 참조의 «누가» 를
    /// 찾을 때 쓰는 것과 같은 규칙이고, 마찬가지로 추정이다 — 다만 안 붙이면
    /// 그 클래스는 멤버가 통째로 없는 것으로 보인다.
    /// </summary>
    private void ReattachOrphans()
    {
        // 두 가지가 큰 C# 저장소에서 적재를 8초에서 2분으로 만들었다.
        //  - 주인 찾기가 파일별 정의를 찾는데, 인덱스는 적재 뒤에 만들어서 고아마다
        //    정의 표 전체를 훑었다.
        //  - 주인 목록을 CTE 로 두었더니 고치는 행마다, 열마다 다시 계산됐다.
        // 인덱스를 먼저 만들고, 주인 목록은 한 번만 계산해 임시 표에 둔다.
        using var command = _db.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS def_file ON definition(file_id, died_ord, line);

            CREATE TEMP TABLE owner(id INTEGER PRIMARY KEY, type_id INTEGER);

            INSERT INTO owner(id, type_id)
            SELECT o.id,
                   (SELECT t.id
                    FROM definition td
                    JOIN symbol t ON t.id = td.symbol_id
                    WHERE td.file_id = o.file_id AND td.died_ord IS NULL
                      AND td.line <= o.line
                      AND t.kind = 3 AND t.display <> '<invalid-global-code>'
                    ORDER BY td.line DESC
                    LIMIT 1)
            FROM (
                SELECT s.id, d.file_id, d.line
                FROM symbol bad
                JOIN symbol s ON s.container_id = bad.id
                JOIN definition d ON d.symbol_id = s.id AND d.died_ord IS NULL
                WHERE bad.display = '<invalid-global-code>'
                GROUP BY s.id
            ) o;

            DELETE FROM owner WHERE type_id IS NULL;
            """;
        command.ExecuteNonQuery();

        using (var move = _db.CreateCommand())
        {
            move.CommandText = """
                UPDATE symbol
                SET container_id = (SELECT type_id FROM owner WHERE owner.id = symbol.id),
                    type_id      = (SELECT type_id FROM owner WHERE owner.id = symbol.id)
                WHERE id IN (SELECT id FROM owner)
                """;
            _reattached = move.ExecuteNonQuery();
        }

        using (var drop = _db.CreateCommand())
        {
            drop.CommandText = "DROP TABLE owner";
            drop.ExecuteNonQuery();
        }

        // 선언에도 가짜 이름이 박혀 있다. 되붙인 뒤에 주인 이름으로 바꾼다.
        // 한 문장에 합치면 CTE 를 상관 서브쿼리로 다시 들여다봐야 해서 조용히 안 먹는다.
        using var rename = _db.CreateCommand();
        rename.CommandText = """
            UPDATE symbol
            SET signature = REPLACE(
                signature,
                '<invalid-global-code>',
                (SELECT t.display FROM symbol t WHERE t.id = symbol.container_id))
            WHERE signature LIKE '%<invalid-global-code>%'
              AND container_id IN (SELECT id FROM symbol WHERE kind = 3 AND display <> '<invalid-global-code>')
            """;
        rename.ExecuteNonQuery();
    }

    /// <summary>
    /// 되붙인 멤버의 <b>자손</b> 까지 새 주인으로 말아 올린다.
    ///
    /// 되붙이기는 멤버의 <c>type_id</c> 만 고친다. 그 아래 매개변수·지역 함수는
    /// 여전히 가짜 타입으로 말아 올려져서, 타입 층 지도에서 그 참조가 전부
    /// <c>&lt;invalid-global-code&gt;</c> 에서 나가는 선으로 그려진다.
    /// 사슬은 얕으므로 바뀌는 것이 없을 때까지 한 층씩 내려보낸다.
    /// </summary>
    private void CarryTypeDown()
    {
        using var command = _db.CreateCommand();
        command.CommandText = """
            WITH placeholder(id) AS (
                SELECT id FROM symbol WHERE display = '<invalid-global-code>'
            )
            UPDATE symbol
            SET type_id = (
                SELECT CASE WHEN c.kind = 3 THEN c.id ELSE c.type_id END
                FROM symbol c WHERE c.id = symbol.container_id)
            WHERE type_id IN (SELECT id FROM placeholder)
              AND container_id NOT IN (SELECT id FROM placeholder)
              AND (SELECT CASE WHEN c.kind = 3 THEN c.id ELSE c.type_id END
                   FROM symbol c WHERE c.id = symbol.container_id)
                  NOT IN (SELECT id FROM placeholder)
            """;

        for (var pass = 0; pass < 8 && command.ExecuteNonQuery() > 0; pass++)
        {
        }
    }

    /// <summary>
    /// 담는 간선을 <c>container_id</c> 에 맞춘다.
    ///
    /// <b>되붙이기는 심볼의 주인만 고치고 간선은 그대로 둔다.</b> 그러면 그래프는
    /// 가짜 타입이 멤버 56개를 담고 있다고 말하고, 진짜 주인에게는 담는 간선이 하나도
    /// 없게 된다 — 실측한 C# 저장소에서 그랬다. 화면에 없는 관계가 그려지고,
    /// 있는 관계가 안 그려진다.
    ///
    /// 규칙 하나로 맞춘다 — <b>살아 있는 심볼에는 지금 주인에게서 오는 담는 간선이 하나 있다.</b>
    /// 이번에 만든 틀린 간선은 우리 실수이므로 지우고, 전 스냅샷부터 있던 것은
    /// «옮겨 갔다» 가 사실이므로 끊어진 것으로 남긴다.
    /// </summary>
    private void MendContainment()
    {
        // 아래 존재 확인은 (from, to, kind) 로 간선을 찾는다. 인덱스는 적재가 끝난 뒤에
        // 만드는 것이 원칙이지만, 이 확인은 적재 안에서 돈다 — 인덱스 없이 돌면 심볼마다
        // 간선 전체를 훑어서 실측한 C# 저장소의 첫 적재가 2초에서 18초가 됐다. 어차피 만들 인덱스를
        // 조금 일찍 만들 뿐이다. 넣기는 이미 끝났으므로 넣을 때의 비용은 없다.
        using (var index = _db.CreateCommand())
        {
            index.CommandText =
                "CREATE INDEX IF NOT EXISTS edge_key ON edge_life(from_id, to_id, kind, died_ord)";
            index.ExecuteNonQuery();
        }

        const string Wrong = """
            kind = 1 AND died_ord IS NULL
              AND from_id <> COALESCE(
                  (SELECT container_id FROM symbol WHERE id = edge_life.to_id), from_id)
            """;

        using (var scrap = _db.CreateCommand())
        {
            scrap.CommandText = $"DELETE FROM edge_life WHERE born_ord = $ord AND {Wrong}";
            scrap.Parameters.AddWithValue("$ord", _ord);
            _bornEdges -= scrap.ExecuteNonQuery();
        }

        using (var close = _db.CreateCommand())
        {
            close.CommandText = $"UPDATE edge_life SET died_ord = $ord WHERE born_ord < $ord AND {Wrong}";
            close.Parameters.AddWithValue("$ord", _ord);
            _diedEdges += close.ExecuteNonQuery();
        }

        using var open = _db.CreateCommand();
        // 굵기는 1 로 고정한다 — 담는 간선을 만들 때 쓰는 규칙과 같다.
        open.CommandText = """
            INSERT INTO edge_life(from_id, to_id, kind, confidence, weight, born_ord, died_ord)
            SELECT s.container_id, s.id, 1, 0, 1, $ord, NULL
            FROM symbol s
            JOIN symbol_life l ON l.symbol_id = s.id AND l.died_ord IS NULL
            WHERE s.container_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM edge_life e
                  WHERE e.kind = 1 AND e.died_ord IS NULL
                    AND e.from_id = s.container_id AND e.to_id = s.id)
            """;
        open.Parameters.AddWithValue("$ord", _ord);
        _bornEdges += open.ExecuteNonQuery();
    }

    /// <summary>
    /// 아무것도 안 남은 가짜 타입을 그래프에서 뺀다.
    ///
    /// <c>&lt;invalid-global-code&gt;</c> 는 색인기가 「모르겠다」 고 적은 자리다. 정의가 없고,
    /// 멤버를 되붙이고 나면 담는 것도 없다. 그런데도 타입 층에 노드로 앉아 선을 달고 있으면
    /// <b>타입이 아닌 것이 타입 자리에 있는 것</b> 이다. 지운다.
    ///
    /// 멤버가 남은 것은 그대로 둔다 — 진짜 주인을 못 찾은 심볼이 거기 매달려 있고,
    /// 그것까지 지우면 있는 코드를 없다고 말하게 된다.
    /// </summary>
    private void DropEmptyPlaceholders()
    {
        using (var mark = _db.CreateCommand())
        {
            // 「밑에 남은 것이 있나」 를 두 열로 묻는다. 적재 뒤에 만들 인덱스를 먼저 만들어
            // 둔다 — 없으면 가짜 타입마다 심볼 표 전체를 훑는다(큰 저장소에서 3초).
            mark.CommandText = """
                CREATE INDEX IF NOT EXISTS sym_container ON symbol(container_id);
                CREATE INDEX IF NOT EXISTS sym_type ON symbol(type_id);

                CREATE TEMP TABLE phantom AS
                SELECT s.id FROM symbol s
                WHERE s.display = '<invalid-global-code>'
                  AND NOT EXISTS (SELECT 1 FROM symbol m WHERE m.container_id = s.id)
                  AND NOT EXISTS (SELECT 1 FROM symbol m WHERE m.type_id = s.id AND m.id <> s.id);
                """;
            mark.ExecuteNonQuery();
        }

        // 이번에 «새로 났다» 고 센 것 중 지금 지우는 것은 빼 준다. 안 그러면 적재 보고가
        // 없는 심볼과 없는 간선을 났다고 말한다.
        using (var count = _db.CreateCommand())
        {
            count.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM symbol_life
                     WHERE born_ord = $ord AND symbol_id IN (SELECT id FROM phantom)),
                    (SELECT COUNT(*) FROM edge_life
                     WHERE born_ord = $ord
                       AND (from_id IN (SELECT id FROM phantom) OR to_id IN (SELECT id FROM phantom)))
                """;
            count.Parameters.AddWithValue("$ord", _ord);
            using var reader = count.ExecuteReader();
            reader.Read();
            _bornSymbols -= reader.GetInt32(0);
            _bornEdges -= reader.GetInt32(1);
        }

        using var command = _db.CreateCommand();
        command.CommandText = """
            DELETE FROM edge_life
            WHERE from_id IN (SELECT id FROM phantom) OR to_id IN (SELECT id FROM phantom);
            DELETE FROM edge_weight
            WHERE from_id IN (SELECT id FROM phantom) OR to_id IN (SELECT id FROM phantom);
            DELETE FROM edge_roll
            WHERE from_id IN (SELECT id FROM phantom) OR to_id IN (SELECT id FROM phantom);
            DELETE FROM definition WHERE symbol_id IN (SELECT id FROM phantom);
            DELETE FROM symbol_life WHERE symbol_id IN (SELECT id FROM phantom);
            DELETE FROM symbol_version WHERE symbol_id IN (SELECT id FROM phantom);
            DELETE FROM symbol WHERE id IN (SELECT id FROM phantom);

            DROP TABLE phantom;
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 이번 스냅샷의 그래프를 이미 살아 있는 것과 맞춰 본다.
    /// 둘 다 있으면 건드리지 않고, 새 것만 태우고, 없어진 것에 died_ord 를 찍는다.
    /// </summary>
    private void Reconcile()
    {
        ReconcileSymbols();
        ReconcileVersions();
        ReconcileEdges();
        ReconcileDefinitions();
    }

    private void ReconcileSymbols()
    {
        var owned = Owned();
        var alive = new HashSet<long>();

        using (var command = _db.CreateCommand())
        {
            command.CommandText = _partial
                ? """
                  SELECT symbol_id FROM symbol_life
                  WHERE died_ord IS NULL AND symbol_id IN (SELECT value FROM json_each($owned))
                  """
                : "SELECT symbol_id FROM symbol_life WHERE died_ord IS NULL";

            if (_partial) command.Parameters.AddWithValue("$owned", Json(owned));

            using var reader = command.ExecuteReader();
            while (reader.Read()) alive.Add(reader.GetInt64(0));
        }

        using var born = _db.CreateCommand();
        born.CommandText = "INSERT INTO symbol_life(symbol_id, born_ord) VALUES ($id, $ord)";
        var bornId = born.Parameters.Add("$id", SqliteType.Integer);
        born.Parameters.AddWithValue("$ord", _ord);

        // 「이번에 본 것 ∩ 이번 적재가 임자인 것」. 범위만 돌면 아무것도 죽지 않고,
        // 관측만 돌면 남의 코드까지 죽인다.
        foreach (var id in _seen.Where(owned.Contains))
        {
            if (alive.Remove(id)) continue;
            bornId.Value = id;
            born.ExecuteNonQuery();
            _bornSymbols++;
        }

        // 임자가 아닌 것은 죽이지 않는다. 그래도 «살아 있지 않은데 이번에 보인 것» 은 태어나게 한다 —
        // 부분 갱신에서 처음 부른 라이브러리 심볼이 그렇다. 안 그러면 선은 있는데 생애가 없어서
        // 트리에도 지도에도 검색에도 나오지 않는다. 이미 살아 있으면 그대로 둔다(매번 새로 태어나지 않게).
        if (_partial)
        {
            using var adopt = _db.CreateCommand();
            adopt.CommandText = """
                INSERT INTO symbol_life(symbol_id, born_ord)
                SELECT $id, $ord
                WHERE NOT EXISTS (SELECT 1 FROM symbol_life WHERE symbol_id = $id AND died_ord IS NULL)
                """;
            var adoptId = adopt.Parameters.Add("$id", SqliteType.Integer);
            adopt.Parameters.AddWithValue("$ord", _ord);

            foreach (var id in _seen.Where(id => !owned.Contains(id)))
            {
                adoptId.Value = id;
                if (adopt.ExecuteNonQuery() > 0) _bornSymbols++;
            }
        }

        using var died = _db.CreateCommand();
        died.CommandText =
            "UPDATE symbol_life SET died_ord = $ord WHERE symbol_id = $id AND died_ord IS NULL";
        died.Parameters.AddWithValue("$ord", _ord);
        var diedId = died.Parameters.Add("$id", SqliteType.Integer);

        // 남은 것은 이번 스냅샷에 나타나지 않은 심볼이다.
        foreach (var id in alive)
        {
            diedId.Value = id;
            died.ExecuteNonQuery();
            _diedSymbols++;
        }
    }

    /// <summary>
    /// 버전은 심볼의 «속성» 이다(키에서 뺐다). 대신 바뀐 구간을 따로 남겨야
    /// 「이 의존성이 언제 버전이 올랐나」 를 답할 수 있다.
    /// </summary>
    private void ReconcileVersions()
    {
        var alive = new Dictionary<long, string>();
        using (var command = _db.CreateCommand())
        {
            command.CommandText = _partial
                ? """
                  SELECT symbol_id, version FROM symbol_version
                  WHERE died_ord IS NULL AND symbol_id IN (SELECT value FROM json_each($owned))
                  """
                : "SELECT symbol_id, version FROM symbol_version WHERE died_ord IS NULL";

            if (_partial) command.Parameters.AddWithValue("$owned", Json(Owned()));

            using var reader = command.ExecuteReader();
            while (reader.Read()) alive[reader.GetInt64(0)] = reader.GetString(1);
        }

        using var close = _db.CreateCommand();
        close.CommandText =
            "UPDATE symbol_version SET died_ord = $ord WHERE symbol_id = $id AND died_ord IS NULL";
        close.Parameters.AddWithValue("$ord", _ord);
        var closeId = close.Parameters.Add("$id", SqliteType.Integer);

        using var open = _db.CreateCommand();
        open.CommandText =
            "INSERT INTO symbol_version(symbol_id, version, born_ord) VALUES ($id, $version, $ord)";
        var openId = open.Parameters.Add("$id", SqliteType.Integer);
        var openVersion = open.Parameters.Add("$version", SqliteType.Text);
        open.Parameters.AddWithValue("$ord", _ord);

        var ownedVersions = Owned();

        foreach (var (symbolId, version) in _versions)
        {
            if (_partial && !ownedVersions.Contains(symbolId)) continue;

            if (alive.TryGetValue(symbolId, out var previous))
            {
                alive.Remove(symbolId);
                if (previous == version) continue;

                closeId.Value = symbolId;
                close.ExecuteNonQuery();
            }

            openId.Value = symbolId;
            openVersion.Value = version;
            open.ExecuteNonQuery();
        }

        foreach (var symbolId in alive.Keys)
        {
            closeId.Value = symbolId;
            close.ExecuteNonQuery();
        }
    }

    private void ReconcileEdges()
    {
        var owned = Owned();
        var alive = new Dictionary<(long From, long To, EdgeKind Kind), int>();
        using (var command = _db.CreateCommand())
        {
            // 참조·상속은 «부르는 쪽» 이 있는 파일이 임자다.
            // contains 는 반대로 «담긴 쪽» 이 임자다 — 담는 네임스페이스에는 정의 행이 없다.
            // 참조·상속은 «부르는 쪽» 이 임자다. contains 는 반대로 «담긴 쪽» 이 임자다 —
            // 담는 네임스페이스는 여러 파일에 걸쳐 있어 한 색인이 대표할 수 없다.
            command.CommandText = _partial
                ? """
                  SELECT from_id, to_id, kind, weight FROM edge_life
                  WHERE died_ord IS NULL
                    AND (CASE WHEN kind = 1 THEN to_id ELSE from_id END)
                        IN (SELECT value FROM json_each($owned))
                  """
                : "SELECT from_id, to_id, kind, weight FROM edge_life WHERE died_ord IS NULL";

            if (_partial) command.Parameters.AddWithValue("$owned", Json(owned));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                alive[(reader.GetInt64(0), reader.GetInt64(1), (EdgeKind)reader.GetInt32(2))] =
                    reader.GetInt32(3);
            }
        }

        using var insert = _db.CreateCommand();
        insert.CommandText = """
            INSERT INTO edge_life(from_id, to_id, kind, confidence, weight, born_ord)
            VALUES ($from, $to, $kind, $confidence, $weight, $ord)
            """;
        var iFrom = insert.Parameters.Add("$from", SqliteType.Integer);
        var iTo = insert.Parameters.Add("$to", SqliteType.Integer);
        var iKind = insert.Parameters.Add("$kind", SqliteType.Integer);
        var iConfidence = insert.Parameters.Add("$confidence", SqliteType.Integer);
        var iWeight = insert.Parameters.Add("$weight", SqliteType.Integer);
        insert.Parameters.AddWithValue("$ord", _ord);

        using var reweigh = _db.CreateCommand();
        reweigh.CommandText = """
            UPDATE edge_life SET weight = $weight
            WHERE from_id = $from AND to_id = $to AND kind = $kind AND died_ord IS NULL
            """;
        var rWeight = reweigh.Parameters.Add("$weight", SqliteType.Integer);
        var rFrom = reweigh.Parameters.Add("$from", SqliteType.Integer);
        var rTo = reweigh.Parameters.Add("$to", SqliteType.Integer);
        var rKind = reweigh.Parameters.Add("$kind", SqliteType.Integer);

        using var history = _db.CreateCommand();
        history.CommandText = """
            INSERT INTO edge_weight(from_id, to_id, kind, ord, weight)
            VALUES ($from, $to, $kind, $ord, $weight)
            """;
        var hFrom = history.Parameters.Add("$from", SqliteType.Integer);
        var hTo = history.Parameters.Add("$to", SqliteType.Integer);
        var hKind = history.Parameters.Add("$kind", SqliteType.Integer);
        var hWeight = history.Parameters.Add("$weight", SqliteType.Integer);
        history.Parameters.AddWithValue("$ord", _ord);

        foreach (var (key, weight) in _edges)
        {
            // 임자가 아닌 간선은 건드리지 않는다. 부분 적재가 남의 코드를 고치면 안 된다.
            // (처음 부른 라이브러리 심볼을 담는 선은 여기서 내지 않아도 된다 — 심볼이 살아나면
            // MendContainment 가 지금 주인과의 contains 를 맞춘다.)
            if (_partial && !owned.Contains(key.Kind == EdgeKind.Contains ? key.To : key.From)) continue;

            if (alive.TryGetValue(key, out var previous))
            {
                alive.Remove(key);
                if (previous == weight) continue;

                // 같은 간선이 굵어지거나 얇아진 것이다. 죽이고 새로 내지 않는다.
                rWeight.Value = weight;
                rFrom.Value = key.From; rTo.Value = key.To; rKind.Value = (int)key.Kind;
                reweigh.ExecuteNonQuery();

                hFrom.Value = key.From; hTo.Value = key.To; hKind.Value = (int)key.Kind;
                hWeight.Value = weight;
                history.ExecuteNonQuery();
                _weightChanges++;
                continue;
            }

            iFrom.Value = key.From;
            iTo.Value = key.To;
            iKind.Value = (int)key.Kind;
            // contains 와 inherit 은 색인기가 준 사실이다. 참조의 «누가» 만 우리가 추론한 것이다.
            iConfidence.Value = key.Kind == EdgeKind.Reference
                ? (int)Confidence.Inferred
                : (int)Confidence.Exact;
            iWeight.Value = weight;
            insert.ExecuteNonQuery();
            _bornEdges++;

            hFrom.Value = key.From; hTo.Value = key.To; hKind.Value = (int)key.Kind;
            hWeight.Value = weight;
            history.ExecuteNonQuery();
        }

        using var died = _db.CreateCommand();
        died.CommandText = """
            UPDATE edge_life SET died_ord = $ord
            WHERE from_id = $from AND to_id = $to AND kind = $kind AND died_ord IS NULL
            """;
        died.Parameters.AddWithValue("$ord", _ord);
        var dFrom = died.Parameters.Add("$from", SqliteType.Integer);
        var dTo = died.Parameters.Add("$to", SqliteType.Integer);
        var dKind = died.Parameters.Add("$kind", SqliteType.Integer);

        foreach (var key in alive.Keys)
        {
            dFrom.Value = key.From;
            dTo.Value = key.To;
            dKind.Value = (int)key.Kind;
            died.ExecuteNonQuery();
            _diedEdges++;
        }
    }

    private void ReconcileDefinitions()
    {
        var alive = new Dictionary<(long Symbol, long File), int>();
        using (var command = _db.CreateCommand())
        {
            command.CommandText = _partial
                ? """
                  SELECT symbol_id, file_id, line FROM definition
                  WHERE died_ord IS NULL AND file_id IN (SELECT value FROM json_each($scope))
                  """
                : "SELECT symbol_id, file_id, line FROM definition WHERE died_ord IS NULL";

            if (_partial) command.Parameters.AddWithValue("$scope", Json(_scope));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                alive[(reader.GetInt64(0), reader.GetInt64(1))] = reader.GetInt32(2);
            }
        }

        using var move = _db.CreateCommand();
        move.CommandText =
            "UPDATE definition SET line = $line WHERE symbol_id = $s AND file_id = $f AND died_ord IS NULL";
        var mLine = move.Parameters.Add("$line", SqliteType.Integer);
        var mSymbol = move.Parameters.Add("$s", SqliteType.Integer);
        var mFile = move.Parameters.Add("$f", SqliteType.Integer);

        using var insert = _db.CreateCommand();
        insert.CommandText =
            "INSERT INTO definition(symbol_id, file_id, line, born_ord) VALUES ($s, $f, $l, $o)";
        var iSymbol = insert.Parameters.Add("$s", SqliteType.Integer);
        var iFile = insert.Parameters.Add("$f", SqliteType.Integer);
        var iLine = insert.Parameters.Add("$l", SqliteType.Integer);
        insert.Parameters.AddWithValue("$o", _ord);

        foreach (var (key, line) in _definitions)
        {
            if (alive.TryGetValue(key, out var previousLine))
            {
                alive.Remove(key);
                if (previousLine == line) continue;

                // 위에 한 줄만 끼어들어도 아래가 전부 밀린다. 줄 이동은 이력이 아니라 갱신이다.
                mLine.Value = line; mSymbol.Value = key.Symbol; mFile.Value = key.File;
                move.ExecuteNonQuery();
                continue;
            }

            iSymbol.Value = key.Symbol; iFile.Value = key.File; iLine.Value = line;
            insert.ExecuteNonQuery();
        }

        using var died = _db.CreateCommand();
        died.CommandText =
            "UPDATE definition SET died_ord = $o WHERE symbol_id = $s AND file_id = $f AND died_ord IS NULL";
        died.Parameters.AddWithValue("$o", _ord);
        var dSymbol = died.Parameters.Add("$s", SqliteType.Integer);
        var dFile = died.Parameters.Add("$f", SqliteType.Integer);

        foreach (var key in alive.Keys)
        {
            dSymbol.Value = key.Symbol; dFile.Value = key.File;
            died.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 이 참조를 감싸는 정의. 「가장 가까운 앞선 정의」 규칙이다.
    /// 타입 단위로 말아 올리면 견고하지만 메서드 단위는 휴리스틱이다 —
    /// 중첩 타입·람다·프로퍼티 접근자·필드 이니셜라이저에서 깨진다.
    /// 그래서 이렇게 만든 간선은 전부 <see cref="Confidence.Inferred"/> 다.
    /// </summary>
    private static long? EnclosingDefinition(
        List<(int Line, int Char, long SymbolId)> definitions, int line, int character)
    {
        var low = 0;
        var high = definitions.Count - 1;
        var found = -1;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            var candidate = definitions[mid];
            var before = candidate.Line < line
                || (candidate.Line == line && candidate.Char <= character);

            if (before)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found < 0 ? null : definitions[found].SymbolId;
    }

    private static (int Line, int Char) PositionOf(Occurrence occurrence)
    {
#pragma warning disable CS0612, CS0618
        // scip-dotnet 0.2.14 는 지금도 deprecated 된 packed range 로만 위치를 준다.
        // 새 single_line_range / multi_line_range 는 비어 있다. 쓸 수밖에 없다.
        var range = occurrence.Range;
#pragma warning restore CS0612, CS0618

        return range.Count >= 2 ? (range[0], range[1]) : (0, 0);
    }

    /// <summary>아예 들이지 않는 것. 지역 심볼은 문서 안에서만 뜻이 있어 그래프에 올릴 수 없다.</summary>
    private static bool IsIgnorable(in ParsedSymbol parsed) =>
        parsed.Kind is SymbolKind.Local or SymbolKind.Unknown;

    /// <summary>
    /// 참조·상속 간선의 끝이 될 수 있는가.
    ///
    /// 네임스페이스는 <b>들이되 간선의 끝으로는 쓰지 않는다.</b> 잡음인 것은 네임스페이스
    /// «occurrence» 지 심볼 자체가 아니다 — <c>using System.Diagnostics;</c> 한 줄이 조각마다
    /// occurrence 를 만들어 의미 없는 간선이 되는 게 문제였다. 그렇다고 심볼까지 버리면
    /// 타입들이 소속 네임스페이스를 잃어서 전체 지도를 모듈 단위로 말아 올릴 수 없다.
    /// </summary>
    private static bool CanBeEdgeEndpoint(in ParsedSymbol parsed) =>
        !IsIgnorable(parsed) && parsed.Kind != SymbolKind.Namespace;

    private void AddEdge(long from, long to, EdgeKind kind)
    {
        if (from == to) return;

        var key = (from, to, kind);
        _edges[key] = _edges.GetValueOrDefault(key) + 1;
    }

    /// <summary>
    /// scip-python 의 모듈 <c>`a.b.c`/</c> 이면 그 부모 <c>`a.b`/</c>, 아니면 <c>null</c>.
    /// 부모 모듈이 색인에 없어도(<c>__init__.py</c> 가 없는 폴더) 같은 키로 세우므로,
    /// 나중에 색인이 그 모듈을 내놓으면 같은 심볼이 된다.
    /// </summary>
    private static ParsedSymbol? DottedModuleParent(in ParsedSymbol parsed)
    {
        if (parsed.Kind != SymbolKind.Namespace || !parsed.Key.StartsWith("scip-python ", StringComparison.Ordinal))
        {
            return null;
        }

        var name = SymbolParser.Display(parsed.Descriptors);
        var dot = name.LastIndexOf('.');
        if (dot <= 0) return null;

        return SymbolParser.Parse(parsed.Prefix + parsed.Version + " " + SymbolParser.EscapeName(name[..dot]) + "/");
    }

    /// <summary>인턴된 심볼과, 말아 올릴 때 쓸 조상들.</summary>
    private readonly record struct Interned(
        long Id, long? ContainerId, long? TypeId, long? NamespaceId, long? ModuleId);

    private Interned InternSymbol(in ParsedSymbol parsed)
    {
        if (_symbols.TryGetValue(parsed.Key, out var existing))
        {
            Touch(existing);
            if (parsed.Version.Length > 0 && parsed.Version != ".") _versions[existing.Id] = parsed.Version;
            return existing;
        }

        // 포함 관계는 심볼 문자열에 이미 들어 있다. 부모를 먼저 만들어 둔다.
        Interned? container = null;
        var parentDescriptors = SymbolParser.ParentDescriptors(parsed.Descriptors);
        if (parentDescriptors is not null)
        {
            var parent = SymbolParser.Parse(
                parsed.Prefix + parsed.Version + " " + parentDescriptors);

            if (!IsIgnorable(parent))
            {
                container = InternSymbol(parent);
            }
        }
        else if (DottedModuleParent(parsed) is { } dotted)
        {
            // scip-python 은 모듈 하나를 조각 하나로 쓴다 — `src.shop.orders`/ 가 한 덩어리라
            // 사슬이 없다. 점을 따라 부모를 세워, C# · TypeScript 처럼 안으로 뻗게 한다.
            container = InternSymbol(dotted);
        }
        else if (parsed.HasPackage)
        {
            // 사슬의 꼭대기. 여기에 패키지를 끼워 넣어야 모듈 단위로 말아 올릴 수 있다.
            container = InternPackageSymbol(parsed.Package);
        }

        long id;
        using (var command = _db.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO symbol(key, kind, package_id, display, container_id, type_id, namespace_id, module_id)
                VALUES ($key, $kind, $package, $display, $container, $type, $namespace, $module)
                RETURNING id
                """;
            command.Parameters.AddWithValue("$key", parsed.Key);
            command.Parameters.AddWithValue("$kind", (int)parsed.Kind);
            command.Parameters.AddWithValue(
                "$package", parsed.HasPackage ? InternPackage(parsed.Package) : DBNull.Value);
            command.Parameters.AddWithValue("$display", SymbolParser.Display(parsed.Descriptors));
            command.Parameters.AddWithValue("$container", (object?)container?.Id ?? DBNull.Value);
            // 부모의 대표를 물려받는다. 부모를 먼저 인턴했으므로 이미 알고 있다.
            command.Parameters.AddWithValue("$type", (object?)container?.TypeId ?? DBNull.Value);
            command.Parameters.AddWithValue("$namespace", (object?)container?.NamespaceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$module", (object?)container?.ModuleId ?? DBNull.Value);

            id = (long)command.ExecuteScalar()!;
        }

        // 자기 자신이 그 층이면 자기가 대표다. 자기 id 는 INSERT 뒤에야 알 수 있으므로
        // 이 경우에만 한 번 더 쓴다 — 전체 심볼이 아니라 타입·네임스페이스·패키지만 해당한다.
        var interned = new Interned(
            id,
            container?.Id,
            parsed.Kind == SymbolKind.Type ? id : container?.TypeId,
            parsed.Kind == SymbolKind.Namespace ? id : container?.NamespaceId,
            parsed.Kind == SymbolKind.Package ? id : container?.ModuleId);

        if (parsed.Kind is SymbolKind.Type or SymbolKind.Namespace or SymbolKind.Package)
        {
            SetRollups(interned);
        }

        _symbols[parsed.Key] = interned;
        _byId[id] = interned;
        Touch(interned);
        if (parsed.Version.Length > 0 && parsed.Version != ".") _versions[id] = parsed.Version;

        return interned;
    }

    /// <summary>
    /// 이번 스냅샷에 나타난 심볼과 그것을 담는 사슬 전체를 훑는다.
    ///
    /// 담는 사슬을 <b>매번</b> 훑어야 한다. 처음 인턴할 때만 contains 간선을 만들면,
    /// 두 번째 스냅샷에서는 심볼이 전부 캐시에 걸려 간선이 하나도 안 만들어지고
    /// 결국 「포함 관계가 전부 끊어졌다」 로 기록된다. 실측으로 8,182개가 통째로 죽었다.
    ///
    /// contains 간선의 굵기는 1 로 고정한다. 담긴 것의 수를 세면 자식이 하나 늘 때마다
    /// 굵기가 바뀌어 의미 없는 변화가 이력에 쌓인다.
    /// </summary>
    private void Touch(in Interned interned)
    {
        var current = interned;

        while (true)
        {
            // 이미 이 스냅샷에서 훑은 가지면 위쪽도 훑은 것이다.
            if (!_seen.Add(current.Id)) return;
            if (current.ContainerId is not { } parentId) return;

            _edges[(parentId, current.Id, EdgeKind.Contains)] = 1;

            if (!_byId.TryGetValue(parentId, out var parent)) return;
            current = parent;
        }
    }

    private long InternPackage(string name) => Intern(_packageIds, "package", "name", name);

    private long InternFile(string path) => Intern(_fileIds, "file", "path", path);

    /// <summary>
    /// 지워진 파일을 대조 범위에 넣는다. 적어 둔 경로는 색인의 프로젝트 뿌리에서 본 상대 경로라,
    /// 그 뿌리로 바꿔 맞춘다 — 같은 리포의 다른 프로젝트에 같은 이름의 파일이 있어도 섞이지 않는다.
    /// 구분자는 색인기마다 다르다(scip-python 은 Windows 에서 <c>\</c>).
    /// </summary>
    private void ScopeGone(string? root, IEnumerable<string> gone)
    {
        if (root is null) return;

        var known = _fileIds.ToLookup(pair => pair.Key.Replace('\\', '/'), pair => pair.Value);
        foreach (var path in gone)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) continue;
            foreach (var fileId in known[relative]) _scope.Add(fileId);
        }
    }

    /// <summary>
    /// 변경 목록에 한 번도 오르지 않고 사라진 파일도 범위에 넣는다 — 추적되지 않은 채 색인됐다가
    /// 지워진 파일은 git 이 모른다. 이 색인의 패키지가 정의를 둔 파일만 본다: 같은 리포의 다른
    /// 프로젝트 파일은 그쪽 뿌리 기준 경로라, 이쪽 뿌리로 풀면 없는 파일로 보인다.
    /// 이번 색인의 문서가 하나라도 디스크에서 안 보이면 경로를 맞출 수 없는 것이다 — 그때는
    /// 아무것도 지우지 않는다. 다른 기계에서 만든 색인을 적재할 때가 그렇다.
    /// </summary>
    private void ScopeMissing(string? root)
    {
        if (root is null || !Directory.Exists(root) || _definedPackages.Count == 0) return;
        if (!_documentPaths.All(path => OnDisk(root, path))) return;

        var candidates = new List<(long Id, string Path)>();
        using (var command = _db.CreateCommand())
        {
            command.CommandText = """
                SELECT DISTINCT f.id, f.path FROM definition d
                JOIN file f ON f.id = d.file_id
                JOIN symbol s ON s.id = d.symbol_id
                JOIN package p ON p.id = s.package_id
                WHERE d.died_ord IS NULL AND p.name IN (SELECT value FROM json_each($packages))
                """;
            command.Parameters.AddWithValue("$packages", System.Text.Json.JsonSerializer.Serialize(_definedPackages));
            using var reader = command.ExecuteReader();
            while (reader.Read()) candidates.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        foreach (var (id, path) in candidates)
        {
            if (!_scope.Contains(id) && !OnDisk(root, path)) _scope.Add(id);
        }
    }

    private static bool OnDisk(string root, string relativePath) =>
        File.Exists(Path.Combine(root, relativePath.Replace('\\', '/')));

    /// <summary>색인의 프로젝트 뿌리, 이 기계의 경로로. 적혀 있지 않거나 파일 URI 가 아니면 null.</summary>
    private static string? ProjectRootOf(string scipPath) =>
        ScipReader.ReadMetadata(scipPath)?.ProjectRoot is { Length: > 0 } root
        && Uri.TryCreate(root, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : null;

    /// <summary>
    /// 패키지를 나타내는 심볼. SCIP 에는 없는 것을 우리가 만든다 —
    /// 담는 사슬의 뿌리가 있어야 전체 지도가 모듈 단위로 접힌다.
    /// </summary>
    private Interned InternPackageSymbol(string name)
    {
        var key = "package " + name;
        if (_symbols.TryGetValue(key, out var existing))
        {
            Touch(existing);
            return existing;
        }

        long id;
        using (var command = _db.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO symbol(key, kind, package_id, display)
                VALUES ($key, $kind, $package, $display)
                RETURNING id
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$kind", (int)SymbolKind.Package);
            command.Parameters.AddWithValue("$package", InternPackage(name));
            command.Parameters.AddWithValue("$display", name);
            id = (long)command.ExecuteScalar()!;
        }

        var interned = new Interned(id, null, null, null, id);
        SetRollups(interned);
        _symbols[key] = interned;
        _byId[id] = interned;
        Touch(interned);
        return interned;
    }

    private long Intern(Dictionary<string, long> cache, string table, string column, string value)
    {
        if (cache.TryGetValue(value, out var existing)) return existing;

        using var command = _db.CreateCommand();
        command.CommandText = $"INSERT INTO {table}({column}) VALUES ($value) RETURNING id";
        command.Parameters.AddWithValue("$value", value);

        var id = (long)command.ExecuteScalar()!;
        cache[value] = id;
        return id;
    }

    private void Describe(long symbolId, string? flavor, string? signature, string? doc)
    {
        using var command = _db.CreateCommand();
        // 이미 채워진 것을 null 로 덮지 않는다 — 문서마다 설명이 붙는 정도가 다르다.
        command.CommandText = """
            UPDATE symbol
            SET flavor    = COALESCE($flavor, flavor),
                signature = COALESCE($signature, signature),
                doc       = COALESCE($doc, doc)
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$flavor", (object?)flavor ?? DBNull.Value);
        command.Parameters.AddWithValue("$signature", (object?)signature ?? DBNull.Value);
        command.Parameters.AddWithValue("$doc", (object?)doc ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", symbolId);
        command.ExecuteNonQuery();
    }

    private void SetRollups(in Interned interned)
    {
        using var command = _db.CreateCommand();
        command.CommandText =
            "UPDATE symbol SET type_id = $type, namespace_id = $namespace, module_id = $module WHERE id = $id";
        command.Parameters.AddWithValue("$type", (object?)interned.TypeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$namespace", (object?)interned.NamespaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$module", (object?)interned.ModuleId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", interned.Id);
        command.ExecuteNonQuery();
    }
}
