using System.Globalization;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace IoTSensorDashboard.Sqlite;

/// <summary>
/// 영속 이벤트 저장소.
///
/// 여기서 I2(append-only)와 I6(지속성)이 완성된다.
/// I1(정확히 1회)은 애플리케이션이 아니라 <b>DB 의 PRIMARY KEY 가 원자적으로</b> 강제한다.
/// </summary>
public sealed class SqliteEventStore : IEventStore, IDisposable
{
    /// <summary>ISO-8601 round-trip. 저장 포맷은 하나여야 한다(I3).</summary>
    private const string Iso = "o";

    private const string InsertSql =
        "INSERT OR IGNORE INTO events(dedup_key,sensor_id,occurred_at,count,direction) VALUES($k,$s,$o,$c,$d);";

    private readonly object _gate = new();
    private readonly SqliteConnection _write;
    private readonly SqliteConnection _read;
    private bool _disposed;

    public string DbPath { get; }

    /// <param name="dbPath">null 이면 %LOCALAPPDATA%\IoTSensorDashboard\events.db.</param>
    public SqliteEventStore(string? dbPath = null)
    {
        DbPath = dbPath ?? DbPaths.EventsDb;

        var dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _write = new SqliteConnection(DbPaths.ConnectionString(DbPath));
        _write.Open();

        // ⚠️ PRAGMA 를 테이블보다 먼저. auto_vacuum 은 빈 DB 에서만 먹는다.
        SqliteSchema.ApplyPragmas(_write);
        SqliteSchema.CreateTables(_write);

        // 읽기 전용 연결을 따로 둔다 — 리포트의 긴 조회가 수집 쓰기를 막지 않게.
        // WAL 모드라 읽기와 쓰기가 동시에 가능하다.
        _read = new SqliteConnection(DbPaths.ConnectionString(DbPath));
        _read.Open();
    }

    // ────────────────────────────────────────────────────────────────────
    //  IEventStore
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 단건 저장.
    ///
    /// 🔑 INSERT OR IGNORE 가 곧 I1 이다.
    ///    "먼저 조회하고 없으면 넣기"로 만들면 두 스레드가 동시에 조회를 통과해 둘 다 넣는다.
    ///    DB 에 원자적으로 맡기면 그런 틈이 없다.
    ///
    ///    ExecuteNonQuery() 반환값이 판정이다: 1 = 새로 저장 / 0 = 이미 있어서 무시.
    /// </summary>
    public bool TryAppend(CountEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);

        lock (_gate)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = InsertSql;
            Bind(cmd, e);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// 묶음 저장 — 한 트랜잭션, 준비된 명령 재사용.
    ///
    /// 📌 왜 필요한가 (실측):
    ///      건별 트랜잭션 …  16,941 evt/s
    ///      묶음 트랜잭션 … 277,650 evt/s   (16.4배)
    ///
    ///    건별 커밋을 유지하면 워커를 아무리 늘려도 저장 단계에서 직렬화되어
    ///    처리량이 오르지 않는다 — 적응형 워커 풀이 장식이 된다.
    ///
    /// 🔒 판정은 건별과 동일하게 행마다 INSERT OR IGNORE 로 한다.
    ///    빨라지려고 판정을 건너뛰면 I1 이 깨진다. 빨리 틀리면 의미가 없다.
    /// </summary>
    public IReadOnlyList<bool> TryAppendBatch(IReadOnlyList<CountEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return [];
        if (events.Count == 1) return new[] { TryAppend(events[0]) };

        var results = new bool[events.Count];

        lock (_gate)
        {
            using var tx = _write.BeginTransaction();
            using var cmd = _write.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = InsertSql;

            // 파라미터를 한 번만 만들고 값만 갈아 끼운다.
            var pk = cmd.Parameters.Add("$k", SqliteType.Text);
            var ps = cmd.Parameters.Add("$s", SqliteType.Text);
            var po = cmd.Parameters.Add("$o", SqliteType.Text);
            var pc = cmd.Parameters.Add("$c", SqliteType.Integer);
            var pd = cmd.Parameters.Add("$d", SqliteType.Text);

            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                pk.Value = e.DedupKey;
                ps.Value = e.SensorId;
                po.Value = e.OccurredAt.ToString(Iso, CultureInfo.InvariantCulture);
                pc.Value = e.Count;
                pd.Value = (object?)e.Direction ?? DBNull.Value;

                results[i] = cmd.ExecuteNonQuery() > 0;   // 건별과 동일한 판정
            }

            tx.Commit();
        }

        return results;
    }

    /// <summary>
    /// 보존창 안에 남아 있는 원본 전체를 삽입 순서대로.
    ///
    /// ⚠️ UI 에서 부르지 말 것 — 행이 많으면 그대로 멈춘다.
    ///    화면에 쓸 값은 SQL 집계(<see cref="SumBySensor"/> 등)로 얻는다.
    ///
    /// rowid 로 정렬하는 이유: SQLite 내장 rowid 가 삽입 순서를 보존한다.
    /// 그래서 별도 seq 컬럼이 필요 없다 — 예전에 그 컬럼이 있었을 때
    /// 삽입마다 MAX(seq) 전체 스캔이 일어나 O(n²) 였다.
    /// </summary>
    public IReadOnlyList<CountEvent> Snapshot()
    {
        var list = new List<CountEvent>();

        using var cmd = _read.CreateCommand();
        cmd.CommandText = "SELECT sensor_id, occurred_at, count, direction FROM events ORDER BY rowid;";
        using var r = cmd.ExecuteReader();

        while (r.Read())
        {
            list.Add(new CountEvent
            {
                SensorId = r.GetString(0),
                OccurredAt = ParseIso(r.GetString(1)),
                Count = r.GetInt32(2),
                Direction = r.IsDBNull(3) ? null : r.GetString(3)
            });
        }

        return list;
    }

    /// <summary>
    /// 총 이벤트 수 = 보존창 안의 원본 + 롤업으로 접혀 들어간 것.
    ///
    /// 📌 raw 만 세면 롤업이 돌 때마다 총계가 줄어드는 것처럼 보인다. 그건 거짓말이다.
    ///    사용자에게 "누적 수신"은 프로그램을 켜 둔 동안 받은 전부여야 한다.
    ///
    /// ⚠️ 이 조회는 전체 스캔이다. UI 가 매초 부르면 안 된다
    ///    (기동 시 1회 읽고 이후는 메모리 카운터로 증분하는 것이 호출 측 책임).
    /// </summary>
    public long Count
    {
        get
        {
            lock (_gate)
            {
                return SqliteSchema.QueryLong(_read, """
                    SELECT (SELECT COUNT(*) FROM events)
                         + (SELECT COALESCE(SUM(event_count),0) FROM events_hourly);
                    """);
            }
        }
    }

    public bool Contains(string dedupKey)
    {
        ArgumentNullException.ThrowIfNull(dedupKey);

        using var cmd = _read.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM events WHERE dedup_key = $k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", dedupKey);
        return cmd.ExecuteScalar() is not null;
    }

    // ────────────────────────────────────────────────────────────────────
    //  롤업 · 프룬  (I2 — 원본 소멸이 아니라 집계로 승격)
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// cutoff 보다 오래된 원본을 시간별 집계로 접어 넣고 지운다.
    ///
    /// 세 단계를 <b>한 트랜잭션</b>에서 한다:
    ///   ① 이번 조각에 담을 행을 먼저 확정 (임시 테이블에 rowid 만 담는다)
    ///   ② 그 행들을 시간대별로 접어 events_hourly 에 UPSERT
    ///   ③ 그 행들을 삭제
    ///
    /// 🔑 ① 이 왜 필요한가: 롤업과 삭제가 <b>같은 행</b>을 봐야 집계가 어긋나지 않는다.
    ///    각각 조건으로 다시 고르면 그 사이에 들어온 행 때문에 수가 안 맞는다.
    /// </summary>
    /// <param name="maxRows">이번 조각의 상한. 락 점유 시간을 제한한다.</param>
    /// <returns>이번에 처리(승격 후 삭제)한 원본 행 수.</returns>
    public int RollupAndPrune(DateTimeOffset cutoff, int maxRows)
    {
        if (maxRows <= 0) throw new ArgumentOutOfRangeException(nameof(maxRows));

        var cutoffText = cutoff.ToString(Iso, CultureInfo.InvariantCulture);

        lock (_gate)
        {
            using var tx = _write.BeginTransaction();

            // ① 조각 확정
            ExecTx(tx, "CREATE TEMP TABLE IF NOT EXISTS _prune_chunk(rid INTEGER PRIMARY KEY);");
            ExecTx(tx, "DELETE FROM _prune_chunk;");

            int picked;
            using (var pick = _write.CreateCommand())
            {
                pick.Transaction = tx;
                pick.CommandText = """
                    INSERT INTO _prune_chunk(rid)
                        SELECT rowid FROM events WHERE occurred_at < $c LIMIT $n;
                    """;
                pick.Parameters.AddWithValue("$c", cutoffText);
                pick.Parameters.AddWithValue("$n", maxRows);
                picked = pick.ExecuteNonQuery();
            }

            if (picked == 0)
            {
                tx.Commit();
                return 0;
            }

            // ② 시간대별로 접어 넣기
            //
            //    direction 을 COALESCE(...,'') 로 접는 이유:
            //    events 는 NULL 을 허용하지만 events_hourly 의 direction 은 복합 PK 의 일부다.
            //    SQL 에서 NULL 은 비교가 애매해서 PK 로 쓰기 곤란하므로 빈 문자열로 정규화한다.
            ExecTx(tx, """
                INSERT INTO events_hourly(sensor_id, hour, direction, count_sum, event_count)
                SELECT sensor_id,
                       strftime('%Y-%m-%dT%H:00:00+00:00', occurred_at) AS hour,
                       COALESCE(direction,'') AS dir,
                       SUM(count), COUNT(*)
                  FROM events
                 WHERE rowid IN (SELECT rid FROM _prune_chunk)
                 GROUP BY sensor_id, hour, dir
                ON CONFLICT(sensor_id, hour, direction) DO UPDATE SET
                   count_sum   = count_sum   + excluded.count_sum,
                   event_count = event_count + excluded.event_count;
                """);

            // ③ 원본 삭제
            int deleted;
            using (var del = _write.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM events WHERE rowid IN (SELECT rid FROM _prune_chunk);";
                deleted = del.ExecuteNonQuery();
            }

            tx.Commit();
            return deleted;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  공간 회수
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 조금씩 OS 에 돌려준다. 매 유지보수 주기에 부른다.
    /// auto_vacuum=INCREMENTAL 이라야 동작한다.
    /// </summary>
    public void ReclaimIncremental(int maxPages = RetentionPolicy.ReclaimIncrementalPages)
    {
        lock (_gate)
            SqliteSchema.Pragma(_write, $"PRAGMA incremental_vacuum({maxPages});");
    }

    /// <summary>
    /// 파일 전체를 다시 쓴다.
    ///
    /// 🔒 아무 때나 부르지 말 것 — 실측 2,882MB → 321MB 에 4.5초가 걸렸고
    ///    그동안 저장소가 멈춘다. <see cref="RetentionPolicy.ShouldReclaimFull"/> 로 판정한 뒤 부른다.
    ///
    /// ⚠️ VACUUM 은 auto_vacuum 설정을 파일에 다시 굽는다.
    ///    실행 전에 PRAGMA 를 다시 걸어 두지 않으면 설정이 풀린다.
    /// </summary>
    public void ReclaimFull()
    {
        lock (_gate)
        {
            SqliteSchema.Pragma(_write, "PRAGMA auto_vacuum=INCREMENTAL;");
            SqliteSchema.Exec(_write, "VACUUM;");
        }
    }

    /// <summary>
    /// WAL 을 본 파일에 반영한다.
    /// </summary>
    /// <param name="truncate">
    /// 한가하면 true(TRUNCATE — WAL 파일까지 줄인다), 바쁘면 false(PASSIVE — 방해하지 않는 만큼만).
    /// </param>
    public void Checkpoint(bool truncate)
    {
        lock (_gate)
            SqliteSchema.Pragma(_write, truncate
                ? "PRAGMA wal_checkpoint(TRUNCATE);"
                : "PRAGMA wal_checkpoint(PASSIVE);");
    }

    /// <summary>저장소가 자기 상태를 보고한다.</summary>
    public StorageStats Stats()
    {
        lock (_gate)
        {
            long pageSize = SqliteSchema.QueryLong(_write, "PRAGMA page_size;");
            long pageCount = SqliteSchema.QueryLong(_write, "PRAGMA page_count;");
            long freeCount = SqliteSchema.QueryLong(_write, "PRAGMA freelist_count;");
            int autoVacuum = (int)SqliteSchema.QueryLong(_write, "PRAGMA auto_vacuum;");

            long file = pageCount * pageSize;
            long free = freeCount * pageSize;
            return new StorageStats(file, file - free, free, autoVacuum);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  집계 조회  — 전부 SQL 에서. UI 스레드에서 행을 훑지 않는다.
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 센서·방향별 합계.
    ///
    /// 🔑 raw 와 롤업을 반드시 UNION ALL 한다.
    ///    두 소스는 보존창을 기준으로 시간상 서로소라 겹치지 않는다.
    ///    raw 만 보면 3시간 이전 데이터가 통째로 사라진 것처럼 보인다.
    /// </summary>
    public IReadOnlyList<CountBucket> SumBySensor(DateTimeOffset cutoff)
    {
        using var cmd = _read.CreateCommand();
        cmd.CommandText = """
            SELECT k, d, SUM(s) AS s, SUM(r) AS r FROM (
                SELECT sensor_id AS k, COALESCE(direction,'') AS d,
                       SUM(count) AS s, COUNT(*) AS r
                  FROM events WHERE occurred_at >= $c
                 GROUP BY k, d
                UNION ALL
                SELECT sensor_id AS k, direction AS d,
                       SUM(count_sum) AS s, SUM(event_count) AS r
                  FROM events_hourly WHERE hour >= $c
                 GROUP BY k, d
            ) GROUP BY k, d ORDER BY k, d;
            """;
        cmd.Parameters.AddWithValue("$c", cutoff.ToString(Iso, CultureInfo.InvariantCulture));
        return ReadBuckets(cmd);
    }

    /// <summary>
    /// 일자별 합계.
    /// </summary>
    /// <param name="tzOffsetMinutes">
    /// 표시 타임존의 오프셋(분). 한국이면 540.
    ///
    /// 📌 저장은 UTC 로 하고 <b>표시할 때</b> 오프셋을 적용한다(I3).
    ///    그래야 관제실을 어느 시간대의 PC 에서 돌리든 같은 데이터가 같은 값으로 보인다.
    ///    UTC 자정과 현지 자정이 다르므로, 이 오프셋이 없으면 일자 경계가 어긋난다.
    /// </param>
    public IReadOnlyList<CountBucket> SumByDay(int tzOffsetMinutes, DateTimeOffset cutoff)
    {
        var shift = $"{(tzOffsetMinutes >= 0 ? "+" : "")}{tzOffsetMinutes} minutes";

        using var cmd = _read.CreateCommand();
        cmd.CommandText = """
            SELECT k, d, SUM(s) AS s, SUM(r) AS r FROM (
                SELECT strftime('%Y-%m-%d', datetime(occurred_at, $tz)) AS k,
                       COALESCE(direction,'') AS d,
                       SUM(count) AS s, COUNT(*) AS r
                  FROM events WHERE occurred_at >= $c
                 GROUP BY k, d
                UNION ALL
                SELECT strftime('%Y-%m-%d', datetime(hour, $tz)) AS k,
                       direction AS d,
                       SUM(count_sum) AS s, SUM(event_count) AS r
                  FROM events_hourly WHERE hour >= $c
                 GROUP BY k, d
            ) GROUP BY k, d ORDER BY k, d;
            """;
        cmd.Parameters.AddWithValue("$tz", shift);
        cmd.Parameters.AddWithValue("$c", cutoff.ToString(Iso, CultureInfo.InvariantCulture));
        return ReadBuckets(cmd);
    }

    /// <summary>
    /// 데이터가 있는 가장 이른 시각. 리포트의 "전체 기간"이 어디서 시작하는지.
    /// 여기서도 raw 와 롤업을 둘 다 봐야 한다.
    /// </summary>
    public DateTimeOffset? MinOccurredAt()
    {
        using var cmd = _read.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(m) FROM (
                SELECT MIN(occurred_at) AS m FROM events
                UNION ALL
                SELECT MIN(hour) AS m FROM events_hourly
            );
            """;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : ParseIso((string)v);
    }

    // ────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _read.Dispose();
        _write.Dispose();

        // 연결 풀이 파일 핸들을 붙잡고 있으면 테스트에서 파일을 지울 수 없다.
        SqliteConnection.ClearAllPools();
    }

    private static void Bind(SqliteCommand cmd, CountEvent e)
    {
        cmd.Parameters.AddWithValue("$k", e.DedupKey);
        cmd.Parameters.AddWithValue("$s", e.SensorId);
        cmd.Parameters.AddWithValue("$o", e.OccurredAt.ToString(Iso, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$c", e.Count);
        cmd.Parameters.AddWithValue("$d", (object?)e.Direction ?? DBNull.Value);
    }

    private void ExecTx(SqliteTransaction tx, string sql)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<CountBucket> ReadBuckets(SqliteCommand cmd)
    {
        var list = new List<CountBucket>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var direction = r.IsDBNull(1) ? "" : r.GetString(1);
            list.Add(new CountBucket(r.GetString(0), direction, r.GetInt64(2), r.GetInt64(3)));
        }
        return list;
    }

    /// <summary>
    /// 저장된 문자열을 다시 시각으로.
    ///
    /// 🔴 AdjustToUniversal 을 주지 않는다 — <b>저장된 offset 을 그대로 돌려줘야</b> 하기 때문이다.
    ///    코덱(들어올 때)은 AdjustToUniversal 로 UTC 통일하지만,
    ///    저장소(나갈 때)는 저장된 것을 있는 그대로 복원한다. 역할이 다르다.
    ///
    ///    이걸 UTC 로 바꿔 돌려주면 "저장한 것과 읽은 것이 다른" 저장소가 된다.
    ///    값은 같은 순간을 가리키니 눈에 잘 안 띄지만, 왕복(round-trip)이 깨진 것이다.
    ///
    /// AssumeUniversal 은 남긴다 — offset 이 없는 표기를 만나면 로컬 시각이 아니라 UTC 로 읽는다.
    /// 저장 포맷에는 항상 offset 이 붙으므로 여기서는 방어에 가깝다.
    /// </summary>
    private static DateTimeOffset ParseIso(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
