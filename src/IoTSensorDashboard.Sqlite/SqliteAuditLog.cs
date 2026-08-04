using System.Globalization;
using IoTSensorDashboard.Core.Audit;
using IoTSensorDashboard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace IoTSensorDashboard.Sqlite;

/// <summary>
/// 감사 로그 — 누가 · 언제 · 무엇을 · 어디에. append-only.
/// </summary>
public sealed class SqliteAuditLog : IAuditLog, IDisposable
{
    private const string Iso = "o";

    private readonly object _gate = new();
    private readonly SqliteConnection _conn;
    private bool _disposed;

    public SqliteAuditLog(string? dbPath = null)
    {
        var path = dbPath ?? DbPaths.EventsDb;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection(DbPaths.ConnectionString(path));
        _conn.Open();
        SqliteSchema.ApplyPragmas(_conn);
        SqliteSchema.CreateTables(_conn);
    }

    public void Record(in AuditEntry e)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO audit(actor,role,action,target,scope,at) VALUES($a,$r,$c,$t,$s,$w);";
            cmd.Parameters.AddWithValue("$a", e.Actor);
            cmd.Parameters.AddWithValue("$r", e.Role);
            cmd.Parameters.AddWithValue("$c", e.Action);
            cmd.Parameters.AddWithValue("$t", e.Target);
            cmd.Parameters.AddWithValue("$s", e.Scope);
            cmd.Parameters.AddWithValue("$w", e.At.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 최근 것부터 최대 max 건.
    ///
    /// 🔒 이 목록이 잘렸다는 사실을 화면이 말할 수 있어야 한다.
    ///    그래서 <see cref="Count"/> 가 계약에 따로 있다 — "전체 N건 중 최근 M건".
    /// </summary>
    public IReadOnlyList<AuditEntry> Recent(int max = 500)
    {
        if (max <= 0) return [];

        lock (_gate)
        {
            var list = new List<AuditEntry>();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT actor,role,action,target,scope,at FROM audit ORDER BY id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", max);
            using var r = cmd.ExecuteReader();

            while (r.Read())
            {
                list.Add(new AuditEntry(
                    r.GetString(0), r.GetString(1), r.GetString(2),
                    r.GetString(3), r.GetString(4), Parse(r.GetString(5))));
            }

            return list;
        }
    }

    public long Count
    {
        get
        {
            lock (_gate) return SqliteSchema.QueryLong(_conn, "SELECT COUNT(*) FROM audit;");
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
