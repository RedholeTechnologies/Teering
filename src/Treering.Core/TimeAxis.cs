using Microsoft.Data.Sqlite;

namespace Treering.Core;

public sealed record SnapshotInfo(int Ord, string? CommitSha, DateTimeOffset IndexedAt, string? Indexer);

public sealed record DependencyChange(
    string From,
    string To,
    string? FromModule,
    string? ToModule,
    EdgeKind Kind,
    int Weight,
    int Ord,
    bool BothOurs)
{
    /// <summary>
    /// 모듈 경계를 넘는 참조. 아키텍처 규칙 감시가 여기서 시작된다.
    ///
    /// <b>우리 코드끼리</b> 넘는 것만 센다. 외부 패키지를 새로 쓰기 시작한 것은
    /// 경계를 넘은 게 아니라 의존이 는 것이고, 실측에서 이걸 섞었더니
    /// 1,094건 중 916건이 EF Core 행이라 감시로 쓸 수 없었다.
    /// </summary>
    public bool CrossesBoundary =>
        BothOurs && FromModule is not null && ToModule is not null && FromModule != ToModule;
}

public sealed record WeightPoint(int Ord, int Weight);

/// <summary>
/// 시간축. 구간 저장 덕분에 「언제 생겼나」 가 스냅샷 둘을 떠서 비교하는 일이 아니라
/// <c>born_ord</c> · <c>died_ord</c> 인덱스를 한 번 타는 일이 된다.
/// </summary>
public static class TimeAxis
{
    /// <summary>
    /// 스냅샷에 커밋을 적는다. 색인을 시킨 쪽만 리포가 어디인지 아므로 적재기가 아니라
    /// 부르는 쪽에서 적는다.
    /// </summary>
    public static void StampCommit(SqliteConnection db, int ord, string? sha)
    {
        if (sha is null) return;

        using var command = db.CreateCommand();
        command.CommandText = "UPDATE snapshot SET commit_sha = $sha WHERE ord = $ord";
        command.Parameters.AddWithValue("$sha", sha);
        command.Parameters.AddWithValue("$ord", ord);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 아무것도 바뀌지 않은 스냅샷이면 지우고 참을 돌려준다. 생긴 것 · 없어진 것 · 굵어진 것이
    /// 하나도 없는 스냅샷은 시간축에 빈칸만 늘린다. 다시 색인했는데 그래프가 그대로일 때 —
    /// 서버를 다시 띄워 이미 반영한 커밋 전 변경을 한 번 더 본 경우가 그렇다 — 가 여기 걸린다.
    ///
    /// 패키지 버전 기록(<c>symbol_version</c>)은 세지 않는다. 일부 프로젝트만 다시 색인하면
    /// 나머지의 버전 줄이 「없어짐」 으로 찍히는데, 그래프에서 달라진 것은 없다. 지울 때는 그
    /// 줄들도 되돌린다 — 다음 스냅샷이 같은 번호를 다시 쓰므로, 남겨 두면 거기서 죽은 것이 된다.
    /// </summary>
    public static bool DropIfEmpty(SqliteConnection db, int ord)
    {
        using var look = db.CreateCommand();
        look.CommandText = """
            SELECT EXISTS (SELECT 1 FROM symbol_life    WHERE born_ord = $ord OR died_ord = $ord)
                OR EXISTS (SELECT 1 FROM definition     WHERE born_ord = $ord OR died_ord = $ord)
                OR EXISTS (SELECT 1 FROM edge_life      WHERE born_ord = $ord OR died_ord = $ord)
                OR EXISTS (SELECT 1 FROM edge_roll      WHERE born_ord = $ord OR died_ord = $ord)
                OR EXISTS (SELECT 1 FROM edge_weight    WHERE ord = $ord)
            """;
        look.Parameters.AddWithValue("$ord", ord);
        if (Convert.ToInt32(look.ExecuteScalar()) == 1) return false;

        using var drop = db.CreateCommand();
        drop.CommandText = """
            DELETE FROM symbol_version WHERE born_ord = $ord;
            UPDATE symbol_version SET died_ord = NULL WHERE died_ord = $ord;
            DELETE FROM snapshot WHERE ord = $ord;
            """;
        drop.Parameters.AddWithValue("$ord", ord);
        drop.ExecuteNonQuery();
        return true;
    }

    public static List<SnapshotInfo> Snapshots(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText =
            "SELECT ord, commit_sha, indexed_at, indexer FROM snapshot ORDER BY ord";

        var snapshots = new List<SnapshotInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            snapshots.Add(new SnapshotInfo(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return snapshots;
    }

    /// <summary>두 스냅샷 사이에 <b>새로 생긴</b> 의존.</summary>
    public static List<DependencyChange> Appeared(
        SqliteConnection db, int fromOrd, int toOrd, Granularity granularity = Granularity.Type) =>
        Changed(db, fromOrd, toOrd, granularity, appeared: true);

    /// <summary>두 스냅샷 사이에 <b>끊어진</b> 의존.</summary>
    public static List<DependencyChange> Disappeared(
        SqliteConnection db, int fromOrd, int toOrd, Granularity granularity = Granularity.Type) =>
        Changed(db, fromOrd, toOrd, granularity, appeared: false);

    /// <summary>
    /// 말아 올린 두 끝 사이의 의존이 «그때 있었나» 를 기준으로 판단한다.
    ///
    /// 간선 하나의 born/died 를 보면 안 된다. 모듈 단위에서는 A→B 하나에 수천 개가 말려 들어가는데,
    /// 그중 하나가 죽었다고 A 가 B 를 안 쓰게 된 것이 아니고, 하나가 새로 났다고 A 가 B 를
    /// 처음 쓰기 시작한 것도 아니다. 「기준 시점엔 있었고 지금은 없다」 가 옳은 정의다.
    /// </summary>
    private static List<DependencyChange> Changed(
        SqliteConnection db, int fromOrd, int toOrd, Granularity granularity, bool appeared)
    {
        var roll = granularity switch
        {
            Granularity.Module => "module_id",
            Granularity.Namespace => "namespace_id",
            Granularity.Type => "type_id",
            _ => "id",
        };

        using var command = db.CreateCommand();
        // 말아 올린 두 끝이 같으면 안쪽 이동일 뿐 의존이 아니다.
        // «우리 코드» 는 이 리포에 정의가 있는 모듈이다 — 외부 패키지에는 정의 행이 없다.
        command.CommandText = $"""
            WITH ours(module_id) AS (
                SELECT DISTINCT s.module_id
                FROM definition d JOIN symbol s ON s.id = d.symbol_id
                WHERE s.module_id IS NOT NULL
            )
            SELECT fs.display, ts.display, fp.name, tp.name, e.kind,
                   SUM(CASE WHEN e.born_ord <= $to AND (e.died_ord IS NULL OR e.died_ord > $to)
                            THEN e.weight ELSE 0 END),
                   $to,
                   MIN(CASE WHEN f.module_id IN (SELECT module_id FROM ours)
                             AND t.module_id IN (SELECT module_id FROM ours) THEN 1 ELSE 0 END),
                   MAX(CASE WHEN e.born_ord <= $from AND (e.died_ord IS NULL OR e.died_ord > $from)
                            THEN 1 ELSE 0 END) AS at_from,
                   MAX(CASE WHEN e.born_ord <= $to AND (e.died_ord IS NULL OR e.died_ord > $to)
                            THEN 1 ELSE 0 END) AS at_to
            FROM edge_life e
            JOIN symbol f  ON f.id  = e.from_id
            JOIN symbol t  ON t.id  = e.to_id
            JOIN symbol fs ON fs.id = f.{roll}
            JOIN symbol ts ON ts.id = t.{roll}
            LEFT JOIN package fp ON fp.id = fs.package_id
            LEFT JOIN package tp ON tp.id = ts.package_id
            WHERE e.kind <> 1
              AND f.{roll} <> t.{roll}
            GROUP BY fs.id, ts.id, e.kind
            HAVING at_from = $wasThere AND at_to = $isThere
            ORDER BY 6 DESC
            """;
        command.Parameters.AddWithValue("$from", fromOrd);
        command.Parameters.AddWithValue("$to", toOrd);
        command.Parameters.AddWithValue("$wasThere", appeared ? 0 : 1);
        command.Parameters.AddWithValue("$isThere", appeared ? 1 : 0);

        var changes = new List<DependencyChange>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            changes.Add(new DependencyChange(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                (EdgeKind)reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7) == 1));
        }

        return changes;
    }

    /// <summary>
    /// 「이 타입을 부르는 곳이 언제부터 늘었나」.
    /// 간선이 굵어진 것은 죽고 새로 난 것이 아니므로 별도 이력에서 읽는다.
    /// </summary>
    public static List<WeightPoint> CallersOverTime(SqliteConnection db, string display)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE target(id) AS (
                SELECT id FROM symbol WHERE kind = 3 AND display = $name
                UNION
                SELECT s.id FROM symbol s JOIN target t ON s.container_id = t.id
            )
            SELECT w.ord, SUM(w.weight)
            FROM edge_weight w
            WHERE w.kind = 3
              AND w.to_id IN (SELECT id FROM target)
              AND w.from_id NOT IN (SELECT id FROM target)
            GROUP BY w.ord
            ORDER BY w.ord
            """;
        command.Parameters.AddWithValue("$name", display);

        var points = new List<WeightPoint>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) points.Add(new WeightPoint(reader.GetInt32(0), reader.GetInt32(1)));
        return points;
    }
}
