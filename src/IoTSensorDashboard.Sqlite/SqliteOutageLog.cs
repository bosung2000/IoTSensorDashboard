using System.Globalization;
using IoTSensorDashboard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace IoTSensorDashboard.Sqlite;

/// <summary>
/// 장애 이력.
///
/// 🔑 이벤트와 <b>같은 파일</b>에 산다. 관제실이 기록한 장애를 대시보드 프로세스가 읽어
///    SLA 를 계산하기 때문이다. 파일이 나뉘면 그 연결이 끊긴다.
/// </summary>
public sealed class SqliteOutageLog : IOutageLog, IDisposable
{
    private const string Iso = "o";

    private readonly object _gate = new();
    private readonly SqliteConnection _conn;
    private bool _disposed;

    public SqliteOutageLog(string? dbPath = null)
    {
        var path = dbPath ?? DbPaths.EventsDb;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection(DbPaths.ConnectionString(path));
        _conn.Open();
        SqliteSchema.ApplyPragmas(_conn);
        SqliteSchema.CreateTables(_conn);
    }

    public void Record(in OutageRecord r)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO outages(sensor_id,store,born_at,resolved_at) VALUES($s,$t,$b,$r);";
            cmd.Parameters.AddWithValue("$s", r.SensorId);
            cmd.Parameters.AddWithValue("$t", r.Store);
            cmd.Parameters.AddWithValue("$b", r.BornAt.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$r", r.ResolvedAt.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>기록된 순서대로 전부.</summary>
    public IReadOnlyList<OutageRecord> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<OutageRecord>();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT sensor_id, store, born_at, resolved_at FROM outages ORDER BY id;";
            using var r = cmd.ExecuteReader();

            while (r.Read())
            {
                list.Add(new OutageRecord(
                    r.GetString(0),
                    r.GetString(1),
                    Parse(r.GetString(2)),
                    Parse(r.GetString(3))));
            }

            return list;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Dispose();
    }

    /// <summary>저장된 offset 을 그대로 복원한다(AdjustToUniversal 금지 — 왕복이 깨진다).</summary>
    private static DateTimeOffset Parse(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
