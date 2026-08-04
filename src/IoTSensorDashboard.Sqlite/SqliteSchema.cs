using Microsoft.Data.Sqlite;

namespace IoTSensorDashboard.Sqlite;

/// <summary>
/// 테이블 정의와 연결 설정. 세 저장소가 같은 파일을 공유하므로 한 곳에 모은다.
/// </summary>
internal static class SqliteSchema
{
    /// <summary>
    /// 연결 직후, 테이블을 만들기 전에 부른다.
    ///
    /// ⚠️ 순서가 중요하다 — auto_vacuum 은 <b>빈 DB 에서만</b> 설정이 먹는다.
    ///    테이블이 하나라도 생긴 뒤에 걸면 조용히 무시된다(오류도 안 난다).
    /// </summary>
    public static void ApplyPragmas(SqliteConnection conn)
    {
        // ① auto_vacuum — 지운 공간을 OS 에 돌려줄 수 있게 한다.
        //
        //    📌 근거: SQLite 는 DELETE 해도 파일을 줄이지 않고 free list 에 넣어 재사용만 한다.
        //       이걸 안 걸면 보존창이 정상 동작하는데도 파일이 2,882MB 가 되고,
        //       그중 88% 가 빈 페이지인 상태가 된다.
        //       "지우고 있는데 파일이 안 줄어드는" 상태라 원인을 찾기 매우 어렵다.
        Pragma(conn, "PRAGMA auto_vacuum=INCREMENTAL;");

        // ② WAL — 읽기와 쓰기가 서로를 막지 않는다.
        //    리포트가 긴 조회를 하는 동안에도 수집이 계속 쓸 수 있어야 한다.
        Pragma(conn, "PRAGMA journal_mode=WAL;");

        // ③ NORMAL — 크래시에 안전하면서 디스크 동기화 횟수를 줄여 삽입 처리량을 올린다.
        Pragma(conn, "PRAGMA synchronous=NORMAL;");
    }

    public static void CreateTables(SqliteConnection conn)
    {
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS events(
                dedup_key   TEXT PRIMARY KEY,
                sensor_id   TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                count       INTEGER NOT NULL,
                direction   TEXT
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS events_hourly(
                sensor_id   TEXT NOT NULL,
                hour        TEXT NOT NULL,
                direction   TEXT NOT NULL,
                count_sum   INTEGER NOT NULL,
                event_count INTEGER NOT NULL,
                PRIMARY KEY(sensor_id, hour, direction)
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS outages(
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                sensor_id   TEXT NOT NULL,
                store       TEXT NOT NULL,
                born_at     TEXT NOT NULL,
                resolved_at TEXT NOT NULL
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS audit(
                id     INTEGER PRIMARY KEY AUTOINCREMENT,
                actor  TEXT NOT NULL,
                role   TEXT NOT NULL,
                action TEXT NOT NULL,
                target TEXT NOT NULL,
                scope  TEXT NOT NULL,
                at     TEXT NOT NULL
            );
            """);
    }

    /// <summary>
    /// PRAGMA 는 결과 행을 돌려주는 것이 있어서 ExecuteScalar 로 실행한다.
    /// 설정이 실제로 먹었는지 확인하려면 반환값을 봐야 한다.
    /// </summary>
    public static object? Pragma(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static long QueryLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
    }
}
