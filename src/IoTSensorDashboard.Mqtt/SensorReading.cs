namespace IoTSensorDashboard.Mqtt;

/// <summary>
/// 센서 한 대가 한 순간에 관측한 것. 아직 발행되지 않은 상태다.
///
/// 오프라인 동안에는 이것이 링버퍼에 쌓였다가, 복구되면 <b>원본 시각 그대로</b> 발행된다.
/// 그래야 통계가 "언제 일어난 일인지"를 잃지 않는다.
/// </summary>
public readonly record struct SensorReading(
    string SensorId,
    string Vendor,
    string SiteId,
    DateTimeOffset At,
    int In,
    int Out);
