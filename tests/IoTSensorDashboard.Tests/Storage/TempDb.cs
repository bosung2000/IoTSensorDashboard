using IoTSensorDashboard.Sqlite;

namespace IoTSensorDashboard.Tests.Storage;

/// <summary>
/// 테스트마다 격리된 DB 파일.
///
/// 🔒 실제 데이터 위치(%LOCALAPPDATA%)를 쓰지 않는다.
///    테스트가 사용자의 진짜 데이터를 건드리면 안 되고,
///    이전 실행이 남긴 수백 MB 가 결과를 바꿔서도 안 된다.
/// </summary>
internal sealed class TempDb : IDisposable
{
    private readonly string _dir;

    public TempDb()
    {
        _dir = Path.Combine(Path.GetTempPath(), "iotsd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        DbPath = Path.Combine(_dir, "events.db");
    }

    public string DbPath { get; }

    public SqliteEventStore OpenStore() => new(DbPath);

    public SqliteOutageLog OpenOutageLog() => new(DbPath);

    public SqliteAuditLog OpenAuditLog() => new(DbPath);

    /// <summary>파일 크기(바이트). 없으면 0.</summary>
    public long FileBytes => File.Exists(DbPath) ? new FileInfo(DbPath).Length : 0;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 파일 핸들이 아직 안 풀렸으면 임시 폴더에 남는다. OS 가 정리한다.
            // 테스트를 실패시킬 이유는 아니다.
        }
    }
}
