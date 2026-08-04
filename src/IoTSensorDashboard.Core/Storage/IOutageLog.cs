namespace IoTSensorDashboard.Core.Storage;

/// <summary>센서가 죽어 있던 한 구간. 가동률·MTTR 의 원재료다.</summary>
public readonly record struct OutageRecord(
    string SensorId, string Store, DateTimeOffset BornAt, DateTimeOffset ResolvedAt)
{
    public TimeSpan Duration => ResolvedAt - BornAt;
}

/// <summary>
/// 장애 이력.
///
/// 🔑 이벤트와 같은 DB 파일에 산다. 관제실이 기록한 장애를 대시보드 프로세스가 읽어
///    SLA 를 계산하기 때문이다 — 파일이 나뉘어 있으면 그 연결이 끊긴다.
/// </summary>
public interface IOutageLog
{
    void Record(in OutageRecord r);

    IReadOnlyList<OutageRecord> Snapshot();
}
