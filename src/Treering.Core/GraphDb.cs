using Microsoft.Data.Sqlite;

namespace Treering.Core;

public enum EdgeKind
{
    Contains = 1,
    Inherit = 2,
    Reference = 3,
}

/// <summary>간선을 얼마나 믿을 수 있는지. UI 가 그대로 노출해야 한다.</summary>
public enum Confidence
{
    /// <summary>색인기가 직접 준 것. contains·inherit 이 여기 해당한다.</summary>
    Exact = 0,

    /// <summary>위치로 추론한 것. SCIP 가 enclosing_symbol 을 주지 않아 참조의 «누가» 는 전부 여기다.</summary>
    Inferred = 1,
}

public static class GraphDb
{
    /// <summary>
    /// 스키마. 스냅샷마다 그래프를 복제하지 않고 <c>born_ord</c>/<c>died_ord</c> 구간으로 저장한다.
    /// 살아 있으면 <c>died_ord</c> 가 NULL 이다.
    /// </summary>
    private const string Schema = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;

        CREATE TABLE IF NOT EXISTS snapshot(
            id           INTEGER PRIMARY KEY,
            ord          INTEGER NOT NULL UNIQUE,
            commit_sha   TEXT,
            committed_at INTEGER,
            indexed_at   INTEGER NOT NULL,
            -- 색인기가 메타데이터에 적는 자기 버전은 믿을 수 없다(0.2.14 가 0.1.0-SNAPSHOT 이라고 적는다).
            -- 우리가 실행할 때 확인한 값을 넣는다.
            indexer      TEXT
        );

        CREATE TABLE IF NOT EXISTS package(
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS file(
            id   INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE
        );

        -- 심볼 문자열은 여기서 한 번만 저장하고, 그 뒤로는 전부 정수로 오간다.
        -- key 에는 버전이 빠져 있다. 버전을 키에 넣으면 릴리스 한 번에 전 심볼이 갈린다.
        CREATE TABLE IF NOT EXISTS symbol(
            id           INTEGER PRIMARY KEY,
            key          TEXT NOT NULL UNIQUE,
            kind         INTEGER NOT NULL,
            package_id   INTEGER REFERENCES package(id),
            display      TEXT NOT NULL,
            container_id INTEGER REFERENCES symbol(id),
            -- 말아 올릴 대상을 적재할 때 미리 계산해 둔다. 담는 사슬은 심볼마다 고정이라
            -- 질의할 때마다 재귀로 올라갈 이유가 없다. granularity 전환이 열 조회 한 번이 된다.
            type_id      INTEGER REFERENCES symbol(id),
            namespace_id INTEGER REFERENCES symbol(id),
            module_id    INTEGER REFERENCES symbol(id),
            -- class · interface · enum · struct · record · delegate.
            -- SCIP 의 kind 필드는 비어 있지만 documentation 에 컴파일러가 적은 선언이 들어 있다.
            -- 우리 코드에만 붙는다 — 외부 패키지 타입에는 documentation 이 없다.
            flavor       TEXT,
            -- 선언 그대로. 상세 패널이 「이 클래스에 무엇이 들어 있나」 를 보여줄 때 쓴다.
            signature    TEXT,
            -- 우리가 쓴 코드인가. 「정의가 이 색인 안에 있는가」 로 정한다 — 이름 규칙이 아니다.
            -- System.* 로 거르면 회사 패키지가 Microsoft.* 일 때 틀리고, 남의 코드가
            -- 우리 이름을 쓰면 또 틀린다. 색인기가 준 사실만 쓴다.
            own          INTEGER NOT NULL DEFAULT 0,
            -- 문서 주석을 읽을 글로 바꾼 것. 보통 주석(# · //)은 색인에 없어 담지 못한다.
            doc          TEXT
        );

        -- 버전은 심볼의 속성이다. 「이 의존성이 언제 버전이 올랐나」가 여기서 나온다.
        -- 심볼이 살아 있던 구간. 「이 타입이 언제 생겼나 / 언제 사라졌나」 가 여기서 나온다.
        CREATE TABLE IF NOT EXISTS symbol_life(
            symbol_id INTEGER NOT NULL REFERENCES symbol(id),
            born_ord  INTEGER NOT NULL,
            died_ord  INTEGER
        );

        CREATE TABLE IF NOT EXISTS symbol_version(
            symbol_id INTEGER NOT NULL REFERENCES symbol(id),
            version   TEXT NOT NULL,
            born_ord  INTEGER NOT NULL,
            died_ord  INTEGER
        );

        -- 정의의 «존재» 는 (심볼, 파일) 로 잡는다. 줄 번호는 위에 한 줄만 끼어들어도 바뀌므로
        -- 구간의 일부로 두면 커밋마다 전부 죽고 되살아난다. 줄은 «지금 어디» 를 뜻하는 속성이다.
        CREATE TABLE IF NOT EXISTS definition(
            symbol_id INTEGER NOT NULL REFERENCES symbol(id),
            file_id   INTEGER NOT NULL REFERENCES file(id),
            line      INTEGER NOT NULL,
            born_ord  INTEGER NOT NULL,
            died_ord  INTEGER
        );

        -- 간선의 «굵기» 는 존재와 따로 간다. 참조가 3곳에서 12곳으로 는 것은
        -- 간선이 죽고 새로 난 것이 아니라 같은 간선이 굵어진 것이다.
        -- 바뀐 스냅샷에만 한 줄 남긴다 — 「언제부터 늘었나」 가 여기서 나온다.
        CREATE TABLE IF NOT EXISTS edge_weight(
            from_id INTEGER NOT NULL REFERENCES symbol(id),
            to_id   INTEGER NOT NULL REFERENCES symbol(id),
            kind    INTEGER NOT NULL,
            ord     INTEGER NOT NULL,
            weight  INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS edge_life(
            from_id    INTEGER NOT NULL REFERENCES symbol(id),
            to_id      INTEGER NOT NULL REFERENCES symbol(id),
            kind       INTEGER NOT NULL,
            confidence INTEGER NOT NULL,
            weight     INTEGER NOT NULL,
            born_ord   INTEGER NOT NULL,
            died_ord   INTEGER
        );

        -- 층마다 미리 말아 둔 간선. edge_life 와 열 모양이 같고, 양 끝이 심볼이 아니라
        -- 그 층의 대표(모듈·네임스페이스·타입)다. level 은 Granularity 값 그대로다.
        --
        -- 지도가 하는 일은 «말아 올린 간선» 을 묶는 것이 전부인데, 질의할 때마다 말면
        -- 간선 18만 개마다 symbol 을 두 번 찾아가야 한다. 실측으로 그 조인이 질의 시간의
        -- 대부분이었다. 적재할 때 한 번 말아 두면 모듈 지도가 행 수백 개를 읽는 일이 된다.
        --
        -- 구간 저장은 edge_life 와 같다. 말아 올린 간선은 밑에 깔린 간선이 하나라도
        -- 살아 있는 동안 살아 있다. 굵기는 마지막으로 잰 값이다 — edge_life 와 같은 규칙이다.
        CREATE TABLE IF NOT EXISTS edge_roll(
            level    INTEGER NOT NULL,
            from_id  INTEGER NOT NULL,
            to_id    INTEGER NOT NULL,
            kind     INTEGER NOT NULL,
            weight   INTEGER NOT NULL,
            born_ord INTEGER NOT NULL,
            died_ord INTEGER
        );

        -- 적재 중 대조가 이것을 탄다. 행이 수만 개뿐이라 적재 중에 들고 있어도 싸다.
        CREATE INDEX IF NOT EXISTS roll_key ON edge_roll(level, from_id, to_id, kind, died_ord);
        CREATE INDEX IF NOT EXISTS roll_to  ON edge_roll(level, to_id, kind);

        """;


    /// <summary>
    /// 인덱스는 적재가 끝난 뒤에 만든다. 적재 중에 있으면 INSERT 마다 갱신되어
    /// 실측으로 적재 시간이 배 이상 늘었다. 읽기 전에만 있으면 된다.
    /// </summary>
    private const string Indexes = """
        -- 「이 심볼을 부르는 곳」 이 인덱스 하나로 끝나야 한다.
        CREATE INDEX IF NOT EXISTS edge_to   ON edge_life(to_id, kind, born_ord, died_ord);
        CREATE INDEX IF NOT EXISTS edge_from ON edge_life(from_id, kind, born_ord, died_ord);
        -- 「지난주에 새로 생긴 의존」 은 이쪽을 탄다.
        CREATE INDEX IF NOT EXISTS edge_born ON edge_life(born_ord);
        CREATE INDEX IF NOT EXISTS edge_key ON edge_life(from_id, to_id, kind, died_ord);
        CREATE INDEX IF NOT EXISTS life_symbol ON symbol_life(symbol_id, died_ord);
        CREATE INDEX IF NOT EXISTS weight_ord ON edge_weight(ord);
        CREATE INDEX IF NOT EXISTS sym_container ON symbol(container_id);
        CREATE INDEX IF NOT EXISTS sym_type ON symbol(type_id);
        CREATE INDEX IF NOT EXISTS sym_namespace ON symbol(namespace_id);
        CREATE INDEX IF NOT EXISTS sym_module ON symbol(module_id);
        CREATE INDEX IF NOT EXISTS sym_package ON symbol(package_id);
        CREATE INDEX IF NOT EXISTS sym_display ON symbol(display, kind);
        CREATE INDEX IF NOT EXISTS sym_own ON symbol(own);
        CREATE INDEX IF NOT EXISTS def_symbol ON definition(symbol_id);
        CREATE INDEX IF NOT EXISTS def_file ON definition(file_id, died_ord, line);
        """;

    /// <summary>
    /// 말아 둘 층. 번호는 <see cref="Granularity"/> 값 그대로 <c>edge_roll.level</c> 에 들어간다.
    /// 심볼 층은 없다 — 그 층은 원본 간선이 곧 답이다.
    /// </summary>
    private static readonly (Granularity Level, string Column)[] RolledLevels =
    [
        (Granularity.Module, "module_id"),
        (Granularity.Namespace, "namespace_id"),
        (Granularity.Type, "type_id"),
    ];

    /// <summary>
    /// <c>PRAGMA user_version</c> 으로 적는 스키마 판. 1 부터 <c>edge_roll</c> 이 채워져 있다.
    /// 그 전에 만든 DB 는 열 때 스냅샷마다 한 번 말아서 따라잡는다.
    /// 2 부터 선이 닿는 심볼에는 생애가 있다(<see cref="AdoptLifeless"/>).
    /// </summary>
    private const int SchemaVersion = 2;

    public static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = Schema;
            command.ExecuteNonQuery();
        }

        // 열을 늘리기 전에 만든 DB 는 이 열이 0 인 채로 열린다. 그대로 두면 「우리 코드만」
        // 이 아무것도 없다고 조용히 거짓말을 한다. 한 번 채우고 넘어간다.
        if (AddColumnIfMissing(connection, "symbol", "own", "INTEGER NOT NULL DEFAULT 0"))
        {
            MarkOwn(connection);
        }

        // 주석 칸이 생기기 전의 DB. 채울 원문이 DB 에 없으므로 다음 색인 때 채워진다.
        AddColumnIfMissing(connection, "symbol", "doc", "TEXT");

        var version = UserVersion(connection);
        if (version < 1) CatchUp(connection);
        if (version < 2) AdoptLifeless(connection);
        if (version < SchemaVersion) Stamp(connection);

        return connection;
    }

    /// <summary>
    /// 선은 있는데 생애가 없는 심볼에 생애를 준다. 부분 갱신이 처음 부른 라이브러리 심볼을 태어나게 하지 않던
    /// 때에 생긴 것들이다 — 생애가 없으면 트리에도 지도에도 검색에도 나오지 않는다. 그 심볼에 닿은 선이 처음
    /// 난 스냅샷을 생일로 삼는다. 그때 처음 불렸기 때문이다. 살아 있는 선이 하나도 없으면 이제 아무도 부르지
    /// 않는 것이라 그대로 둔다.
    /// </summary>
    private static void AdoptLifeless(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH touching(symbol_id, born_ord, died_ord) AS (
                SELECT to_id, born_ord, died_ord FROM edge_life
                UNION ALL
                SELECT from_id, born_ord, died_ord FROM edge_life
            )
            INSERT INTO symbol_life(symbol_id, born_ord)
            SELECT t.symbol_id, MIN(t.born_ord) FROM touching t
            WHERE NOT EXISTS (SELECT 1 FROM symbol_life l WHERE l.symbol_id = t.symbol_id)
            GROUP BY t.symbol_id
            HAVING SUM(t.died_ord IS NULL) > 0
            """;
        command.ExecuteNonQuery();
    }

    private static void Stamp(SqliteConnection connection)
    {
        using var stamp = connection.CreateCommand();
        stamp.CommandText = $"PRAGMA user_version = {SchemaVersion}";
        stamp.ExecuteNonQuery();
    }

    /// <summary>
    /// 스냅샷 <paramref name="ord"/> 의 그래프를 층마다 말아서 <c>edge_roll</c> 과 맞춘다.
    /// 적재가 끝난 뒤, 층이 다 정해진 뒤에 부른다 — 되붙이기와 «우리 코드» 표시 다음이다.
    ///
    /// 말아 올린 상태는 <c>edge_life</c> 의 그 시점 모습에서 곧바로 나온다. 그래서 부분 적재든
    /// 전체 적재든 따로 셈할 것이 없다 — 그 시점을 다시 말고, 달라진 것만 적는다.
    /// </summary>
    public static void RollEdges(SqliteConnection connection, int ord)
    {
        foreach (var (level, column) in RolledLevels)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TEMP TABLE rolled_now AS
                SELECT f.{column} AS from_id, t.{column} AS to_id, e.kind AS kind, SUM(e.weight) AS weight
                FROM edge_life e
                JOIN symbol f ON f.id = e.from_id
                JOIN symbol t ON t.id = e.to_id
                WHERE e.born_ord <= $ord AND (e.died_ord IS NULL OR e.died_ord > $ord)
                  AND f.{column} IS NOT NULL AND t.{column} IS NOT NULL
                  AND f.{column} <> t.{column}
                GROUP BY 1, 2, 3;

                CREATE UNIQUE INDEX rolled_now_key ON rolled_now(from_id, to_id, kind);

                -- 밑에 깔린 간선이 하나도 안 남은 것.
                UPDATE edge_roll SET died_ord = $ord
                WHERE level = $level AND died_ord IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM rolled_now n
                      WHERE n.from_id = edge_roll.from_id AND n.to_id = edge_roll.to_id
                        AND n.kind = edge_roll.kind);

                -- 이어지되 굵기가 바뀐 것. 같으면 건드리지 않는다.
                UPDATE edge_roll
                SET weight = (
                    SELECT n.weight FROM rolled_now n
                    WHERE n.from_id = edge_roll.from_id AND n.to_id = edge_roll.to_id
                      AND n.kind = edge_roll.kind)
                WHERE level = $level AND died_ord IS NULL
                  AND weight <> (
                    SELECT n.weight FROM rolled_now n
                    WHERE n.from_id = edge_roll.from_id AND n.to_id = edge_roll.to_id
                      AND n.kind = edge_roll.kind);

                -- 새로 난 것.
                INSERT INTO edge_roll(level, from_id, to_id, kind, weight, born_ord)
                SELECT $level, n.from_id, n.to_id, n.kind, n.weight, $ord
                FROM rolled_now n
                WHERE NOT EXISTS (
                    SELECT 1 FROM edge_roll r
                    WHERE r.level = $level AND r.died_ord IS NULL
                      AND r.from_id = n.from_id AND r.to_id = n.to_id AND r.kind = n.kind);

                -- 같은 시점을 두 번 적재하면 났다가 바로 죽은 구간이 남는다. 없던 것이다.
                DELETE FROM edge_roll WHERE level = $level AND born_ord = died_ord;

                DROP TABLE rolled_now;
                """;
            command.Parameters.AddWithValue("$ord", ord);
            command.Parameters.AddWithValue("$level", (int)level);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// <c>edge_roll</c> 이 생기기 전에 만든 DB 를 따라잡는다. 스냅샷을 차례로 말아야
    /// 시간축이 맞는다 — 마지막 것만 말면 비교 화면에서 과거가 비어 보인다.
    /// </summary>
    private static void CatchUp(SqliteConnection connection)
    {
        var ords = new List<int>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ord FROM snapshot ORDER BY ord";
            using var reader = command.ExecuteReader();
            while (reader.Read()) ords.Add(reader.GetInt32(0));
        }

        using (var transaction = connection.BeginTransaction())
        {
            foreach (var ord in ords) RollEdges(connection, ord);
            transaction.Commit();
        }
    }

    private static int UserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// 무엇이 «우리 코드» 인가를 정한다. 적재가 끝난 뒤, 주인을 되붙인 뒤에 부른다.
    ///
    /// 두 걸음이다. 먼저 <b>정의가 이 색인 안에 있는 것</b> — 파일과 줄이 잡히는 것이
    /// 우리가 쓴 것이다. 그다음 <b>그런 것이 사는 어셈블리 전체</b> — 정의가 안 잡힌
    /// 멤버까지 같이 딸려 와야 타입을 눌렀을 때 안이 비어 보이지 않는다.
    ///
    /// 바깥 패키지는 참조로만 나타나므로 정의가 하나도 없다. 실측에서 System.Runtime ·
    /// EntityFrameworkCore · ClosedXML 전부 정의 0 이었다.
    /// </summary>
    public static void MarkOwn(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE symbol SET own = 1
            WHERE own = 0 AND id IN (SELECT symbol_id FROM definition);

            UPDATE symbol SET own = 1
            WHERE own = 0 AND key NOT LIKE 'scip-python %' AND module_id IN (
                SELECT module_id FROM symbol WHERE own = 1 AND module_id IS NOT NULL);

            -- scip-python 은 풀지 못한 남의 모듈(tablekit.io.sheets …)을 우리 패키지 이름으로
            -- 적는다. 패키지가 같다고 우리 것으로 치면 그 라이브러리가 「우리 코드」 에 들어온다. 그래서
            -- Python 은 모듈 단위로 가른다: 정의가 하나라도 든 모듈의 것만 우리 것이다.
            UPDATE symbol SET own = 1
            WHERE own = 0 AND key LIKE 'scip-python %' AND namespace_id IN (
                SELECT s.namespace_id FROM symbol s JOIN definition d ON d.symbol_id = s.id
                WHERE s.key LIKE 'scip-python %' AND s.namespace_id IS NOT NULL);

            -- 그 위의 모듈(src, src.shop)도 우리 것이다 — 아래에 우리 모듈이 있으니까.
            UPDATE symbol SET own = 1
            WHERE own = 0 AND kind = 2 AND key LIKE 'scip-python %' AND EXISTS (
                SELECT 1 FROM symbol c
                WHERE c.kind = 2 AND c.own = 1 AND c.module_id = symbol.module_id
                  AND substr(c.display, 1, length(symbol.display) + 1) = symbol.display || '.');
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>먼저 만든 DB 를 지금 스키마에 맞춘다. 늘렸으면 참을 돌려준다.</summary>
    private static bool AddColumnIfMissing(
        SqliteConnection connection, string table, string column, string declaration)
    {
        using (var look = connection.CreateCommand())
        {
            look.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name";
            look.Parameters.AddWithValue("$name", column);
            if (Convert.ToInt32(look.ExecuteScalar()) > 0) return false;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration}";
        alter.ExecuteNonQuery();
        return true;
    }

    /// <summary>적재를 끝낸 뒤 한 번 부른다. 이미 있으면 아무것도 하지 않는다.</summary>
    public static void CreateIndexes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Indexes;
        command.ExecuteNonQuery();
    }
}
