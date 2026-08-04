using IoTSensorDashboard.Core.Aggregation;
using IoTSensorDashboard.Core.Codecs;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Ingestion;
using Xunit;

namespace IoTSensorDashboard.Tests.InvariantGates;

/// <summary>
/// G-I3 · 시간 해석은 전 구간 단일 규약
///
/// Given  같은 절대 순간을 다른 offset 으로 표기한 이벤트
/// When   수집 → 저장 → 집계
/// Then   같은 버킷으로 접힌다
///
/// 깨지면: 센서 현지시각과 UTC 가 섞여 시간대별 통계가 어긋난다 → <b>피크타임 오판.</b>
/// </summary>
public sealed class G_I3_TimeTests
{
    private static readonly TimeSpan Kst = TimeSpan.FromHours(9);

    private static CountEvent Event(DateTimeOffset at, int count = 1, string sensorId = "flir-0001") =>
        new() { SensorId = sensorId, OccurredAt = at, Count = count, Direction = "in" };

    private static RawPayload Raw(string body) =>
        new() { Vendor = "flir", Body = body, ReceivedAt = DateTimeOffset.UtcNow };

    // ── 같은 순간은 같은 버킷 ────────────────────────────────────────────

    [Fact]
    public void 표기가_달라도_같은_절대_순간이면_같은_버킷이다()
    {
        // UTC 00:00 == KST 09:00 — 같은 순간이다.
        var utc = new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);
        var kst = new DateTimeOffset(2026, 7, 9, 9, 0, 0, Kst);

        Assert.Equal(utc.ToUnixTimeMilliseconds(), kst.ToUnixTimeMilliseconds());

        var aggregates = new HourlyAggregator(TimeSpan.Zero)
            .Aggregate([Event(utc, 3, "a"), Event(kst, 2, "a")]);

        var bucket = Assert.Single(aggregates);
        Assert.Equal(5, bucket.Count);   // 두 건이 한 칸으로 접혔다
    }

    [Fact]
    public void offset_이_없는_타임스탬프는_UTC_로_간주한다()
    {
        // 📌 기본 파싱은 offset 이 없으면 **호스트 로컬 시각**으로 본다.
        //    그러면 관제실을 어느 시간대의 PC 에서 돌리느냐에 따라
        //    **같은 데이터가 다른 시각으로 저장된다.**
        var withZ = """{"sensorId":"s","timestamp":"2026-07-09T09:00:00Z","lines":[{"direction":"in","count":1}]}""";
        var without = """{"sensorId":"s","timestamp":"2026-07-09T09:00:00","lines":[{"direction":"in","count":1}]}""";

        var codec = new FlirCodec();
        var a = Assert.Single(codec.Decode(Raw(withZ)));
        var b = Assert.Single(codec.Decode(Raw(without)));

        Assert.Equal(a.OccurredAt, b.OccurredAt);
        Assert.Equal(a.DedupKey, b.DedupKey);
    }

    // ── 버킷 경계 ────────────────────────────────────────────────────────

    [Fact]
    public void 시간대_경계에서_칸이_갈린다()
    {
        var aggregates = new HourlyAggregator(TimeSpan.Zero).Aggregate(
        [
            Event(new DateTimeOffset(2026, 7, 9, 9, 59, 59, TimeSpan.Zero), 1),
            Event(new DateTimeOffset(2026, 7, 9, 10, 0, 0, TimeSpan.Zero), 2),
        ]);

        Assert.Equal(2, aggregates.Count);
        Assert.Equal(9, aggregates[0].BucketStart.Hour);
        Assert.Equal(10, aggregates[1].BucketStart.Hour);
    }

    [Fact]
    public void 자정_경계를_넘는_이벤트가_다른_날로_간다()
    {
        var aggregates = new HourlyAggregator(TimeSpan.Zero).Aggregate(
        [
            Event(new DateTimeOffset(2026, 7, 9, 23, 30, 0, TimeSpan.Zero), 1),
            Event(new DateTimeOffset(2026, 7, 10, 0, 30, 0, TimeSpan.Zero), 2),
        ]);

        Assert.Equal(2, aggregates.Count);
        Assert.Equal(9, aggregates[0].BucketStart.Day);
        Assert.Equal(10, aggregates[1].BucketStart.Day);
    }

    [Fact]
    public void 표시_타임존이_자정_경계를_옮긴다()
    {
        // 🔴 UTC 22:00 은 한국에서는 **다음 날 07:00** 이다.
        //    이 보정이 없으면 "어젯밤 손님"이 "오늘 새벽 손님"으로 잘못 집계된다.
        var lateNightUtc = new DateTimeOffset(2026, 7, 9, 22, 0, 0, TimeSpan.Zero);

        var inUtc = Assert.Single(new HourlyAggregator(TimeSpan.Zero).Aggregate([Event(lateNightUtc)]));
        var inKst = Assert.Single(new HourlyAggregator(Kst).Aggregate([Event(lateNightUtc)]));

        Assert.Equal(9, inUtc.BucketStart.Day);
        Assert.Equal(22, inUtc.BucketStart.Hour);

        Assert.Equal(10, inKst.BucketStart.Day);    // 다음 날
        Assert.Equal(7, inKst.BucketStart.Hour);
    }

    [Fact]
    public void 서머타임_경계에서도_같은_순간은_한_칸이다()
    {
        // 📌 서머타임이 있는 지역은 벽시계가 한 시간 건너뛰거나 되돌아간다.
        //    우리는 **절대 순간**으로 버킷을 정하므로 그 영향을 받지 않는다.
        //
        //    미국 동부 2026-03-08 02:00 에 EST(-5) → EDT(-4) 전환이 일어난다.
        //    전환 직전과 직후를 각각 그 지역 offset 으로 표기해도,
        //    같은 절대 순간이면 같은 칸이어야 한다.
        var beforeDst = new DateTimeOffset(2026, 3, 8, 1, 30, 0, TimeSpan.FromHours(-5));
        var sameMomentInUtc = beforeDst.ToUniversalTime();

        var aggregates = new HourlyAggregator(TimeSpan.Zero)
            .Aggregate([Event(beforeDst, 1, "a"), Event(sameMomentInUtc, 1, "a")]);

        Assert.Single(aggregates);
        Assert.Equal(2, aggregates[0].Count);
    }

    // ── 묶는 기준 ────────────────────────────────────────────────────────

    [Fact]
    public void 센서와_방향이_다르면_다른_칸이다()
    {
        var at = new DateTimeOffset(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

        var aggregates = new HourlyAggregator(TimeSpan.Zero).Aggregate(
        [
            Event(at, 1, "a") with { Direction = "in" },
            Event(at, 2, "a") with { Direction = "out" },
            Event(at, 3, "b") with { Direction = "in" },
        ]);

        Assert.Equal(3, aggregates.Count);
    }

    [Fact]
    public void 방향이_없는_이벤트도_집계된다()
    {
        var at = new DateTimeOffset(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

        var bucket = Assert.Single(new HourlyAggregator(TimeSpan.Zero)
            .Aggregate([Event(at) with { Direction = null }]));

        Assert.Null(bucket.Direction);
    }

    [Fact]
    public void 빈_입력은_빈_결과다()
    {
        Assert.Empty(new HourlyAggregator().Aggregate([]));
    }

    // ── 재집계 동일성 (I2 와 맞물린다) ───────────────────────────────────

    [Fact]
    public void 같은_원본을_다시_집계하면_같은_결과다()
    {
        // 🔑 집계는 언제나 원본에서 파생된다.
        //    원본이 있으면 언제든 다시 계산할 수 있어야 한다.
        var events = Enumerable.Range(0, 50)
            .Select(i => Event(
                new DateTimeOffset(2026, 7, 9, 9, 0, 0, TimeSpan.Zero).AddMinutes(i * 7),
                count: i % 5,
                sensorId: i % 2 == 0 ? "a" : "b"))
            .ToList();

        var aggregator = new HourlyAggregator(Kst);

        var first = aggregator.Aggregate(events);
        var second = aggregator.Aggregate(events);

        Assert.Equal(
            first.Select(a => (a.SensorId, a.Direction, a.BucketStart, a.Count)),
            second.Select(a => (a.SensorId, a.Direction, a.BucketStart, a.Count)));
    }

    [Fact]
    public void 순서를_바꿔도_집계_결과는_같다()
    {
        // 수집 순서에 결과가 의존하면 재현성이 없다.
        var events = Enumerable.Range(0, 30)
            .Select(i => Event(
                new DateTimeOffset(2026, 7, 9, 9, 0, 0, TimeSpan.Zero).AddMinutes(i * 5),
                count: i))
            .ToList();

        var aggregator = new HourlyAggregator(TimeSpan.Zero);

        var inOrder = aggregator.Aggregate(events);
        var reversed = aggregator.Aggregate(Enumerable.Reverse(events).ToList());

        Assert.Equal(
            inOrder.Select(a => (a.BucketStart, a.Count)),
            reversed.Select(a => (a.BucketStart, a.Count)));
    }
}
