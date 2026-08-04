using IoTSensorDashboard.Core.Domain;

namespace IoTSensorDashboard.Core.Aggregation;

/// <summary>
/// 집계 플러그인 — 이벤트 묶음을 집계 칸으로.
///
/// 이번 범위의 구현은 시간별 하나다(일/월·체류시간은 계약만 열어 둠).
/// </summary>
public interface IAggregator
{
    string Name { get; }

    IReadOnlyList<Aggregate> Aggregate(IEnumerable<CountEvent> events);
}

/// <summary>
/// 시간대별 집계.
///
/// 🔑 집계는 <b>언제나 원본에서 파생</b>된다(I2).
///    집계를 원본처럼 다루지 말 것 — 원본이 있으면 언제든 다시 계산할 수 있어야 한다.
/// </summary>
public sealed class HourlyAggregator : IAggregator
{
    private readonly TimeSpan _displayOffset;

    /// <param name="displayOffset">
    /// 표시 타임존 오프셋. 한국이면 +9시간.
    ///
    /// 📌 저장은 UTC 로 하고 <b>표시할 때</b> 오프셋을 적용한다(I3).
    ///    UTC 자정과 현지 자정이 다르므로, 이게 없으면 「몇 시대 손님인지」가 어긋난다.
    /// </param>
    public HourlyAggregator(TimeSpan displayOffset = default)
    {
        _displayOffset = displayOffset;
    }

    public string Name => "hourly";

    public IReadOnlyList<Aggregate> Aggregate(IEnumerable<CountEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        return events
            .Where(e => e is not null)
            .GroupBy(e => (e.SensorId, e.Direction, Bucket: BucketOf(e.OccurredAt)))
            .Select(g => new Aggregate
            {
                SensorId = g.Key.SensorId,
                Direction = g.Key.Direction,
                BucketStart = g.Key.Bucket,
                Count = g.Sum(e => e.Count)
            })
            .OrderBy(a => a.BucketStart)
            .ThenBy(a => a.SensorId, StringComparer.Ordinal)
            .ThenBy(a => a.Direction, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>표시 타임존의 「벽시계 시」 시작으로 자른다.</summary>
    private DateTimeOffset BucketOf(DateTimeOffset at)
    {
        var shifted = at.ToOffset(_displayOffset);

        return new DateTimeOffset(
            shifted.Year, shifted.Month, shifted.Day, shifted.Hour, 0, 0, shifted.Offset);
    }
}
