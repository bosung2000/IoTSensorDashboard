namespace IoTSensorDashboard.Core.Ingestion;

/// <summary>
/// 수집 채널 플러그인 — 어디선가 원본을 받아 sink 로 흘려보낸다. "수신만" 한다.
///
/// 이번 범위의 구현: MQTT · 시뮬레이션. (HTTP · CoAP · AMQP 는 계약만 열어 둔다.)
/// </summary>
public interface IIngestionSource
{
    string Name { get; }

    Task RunAsync(Func<RawPayload, Task> sink, CancellationToken ct);
}
