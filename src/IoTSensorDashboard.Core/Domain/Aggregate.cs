namespace IoTSensorDashboard.Core.Domain;

/// <summary>
/// 집계 한 칸 — 센서·방향·시간 버킷 단위의 합.
///
/// 집계는 언제나 원본에서 파생된다(I2). 집계를 원본처럼 다루지 말 것 —
/// 원본이 있으면 언제든 다시 계산할 수 있어야 한다.
/// </summary>
public sealed record Aggregate
{
    public required string SensorId { get; init; }

    public string? Direction { get; init; }

    /// <summary>버킷 시작 시각 — 표시 타임존의 '벽시계 시(hour)' 시작(I3).</summary>
    public required DateTimeOffset BucketStart { get; init; }

    public required int Count { get; init; }
}
