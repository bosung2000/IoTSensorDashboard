using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.Storage;

/// <summary>
/// 집계 조회 — 전부 SQL 에서 한다.
///
/// 📌 왜: 리포트가 550만 행을 UI 스레드에서 스캔해 화면이 수십 초 얼어붙은 적이 있다.
///    집계는 DB 가 가장 잘한다.
///
/// 그리고 여기서 가장 자주 틀리는 것: <b>raw 만 보고 롤업을 빼먹는 것.</b>
/// 그러면 보존창 이전 데이터가 통째로 사라진 것처럼 보인다.
/// </summary>
public sealed class AggregationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    private static CountEvent Event(DateTimeOffset at, int count, string sensorId = "flir-0001", string? direction = "in")
        => new() { SensorId = sensorId, OccurredAt = at, Count = count, Direction = direction };

    // ── SQL 집계 == 순진한 집계 ──────────────────────────────────────────

    [Fact]
    public void SQL_집계가_순진한_집계와_같다()
    {
        // 손으로 더한 값과 DB 가 더한 값이 같아야 한다.
        // 이게 어긋나면 SQL 이 뭔가 다른 걸 세고 있다는 뜻이다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        var events = new List<CountEvent>();
        for (int i = 0; i < 40; i++)
        {
            var sensor = i % 2 == 0 ? "flir-0001" : "flir-0002";
            var direction = i % 3 == 0 ? "out" : "in";
            var e = Event(T0.AddSeconds(i), count: i, sensorId: sensor, direction: direction);
            events.Add(e);
            store.TryAppend(e);
        }

        var expected = events
            .GroupBy(e => (e.SensorId, e.Direction))
            .ToDictionary(g => g.Key, g => (Sum: g.Sum(e => (long)e.Count), Rows: (long)g.Count()));

        var actual = store.SumBySensor(DateTimeOffset.MinValue);

        Assert.Equal(expected.Count, actual.Count);
        foreach (var bucket in actual)
        {
            var key = (bucket.Key, bucket.Direction);
            Assert.Equal(expected[key!].Sum, bucket.Sum);
            Assert.Equal(expected[key!].Rows, bucket.Rows);
        }
    }

    // ── raw + 롤업 union ─────────────────────────────────────────────────

    [Fact]
    public void 롤업된_것과_남은_것을_합쳐서_돌려준다()
    {
        // 🔴 두 소스는 보존창을 기준으로 시간상 서로소다. 겹치지 않으므로 더하면 된다.
        //    raw 만 보면 3시간 이전 데이터가 통째로 사라진 것처럼 보인다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        store.TryAppend(Event(T0, 10));                  // 롤업될 것
        store.TryAppend(Event(T0.AddHours(5), 7));       // 보존창 안에 남을 것

        store.RollupAndPrune(T0.AddHours(1), RetentionPolicy.PruneChunkRows);

        var bucket = Assert.Single(store.SumBySensor(DateTimeOffset.MinValue));
        Assert.Equal(17L, bucket.Sum);      // 10(롤업) + 7(raw)
        Assert.Equal(2L, bucket.Rows);
    }

    [Fact]
    public void cutoff_이전_데이터는_제외된다()
    {
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        store.TryAppend(Event(T0, 5));
        store.TryAppend(Event(T0.AddHours(10), 9));

        var recent = store.SumBySensor(T0.AddHours(5));
        Assert.Equal(9L, Assert.Single(recent).Sum);
    }

    [Fact]
    public void 데이터가_없으면_빈_결과다()
    {
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        Assert.Empty(store.SumBySensor(DateTimeOffset.MinValue));
        Assert.Null(store.MinOccurredAt());
    }

    // ── 일자별 · 타임존 (I3) ─────────────────────────────────────────────

    [Fact]
    public void 일자_경계에_타임존_오프셋이_적용된다()
    {
        // 📌 저장은 UTC, 표시할 때 오프셋을 적용한다.
        //    UTC 22:00 은 한국(+9)에서는 다음 날 07:00 이다.
        //    이 보정이 없으면 "어제 밤 손님"이 "오늘 새벽 손님"으로 잘못 집계된다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        var utcLateNight = new DateTimeOffset(2026, 7, 9, 22, 0, 0, TimeSpan.Zero);
        store.TryAppend(Event(utcLateNight, 4));

        var utcDays = store.SumByDay(0, DateTimeOffset.MinValue);
        Assert.Equal("2026-07-09", Assert.Single(utcDays).Key);

        var kstDays = store.SumByDay(540, DateTimeOffset.MinValue);   // +9시간
        Assert.Equal("2026-07-10", Assert.Single(kstDays).Key);
    }

    [Fact]
    public void 음수_오프셋도_처리된다()
    {
        // 뉴욕(-5)에서는 UTC 02:00 이 전날 21:00 이다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        var utcEarly = new DateTimeOffset(2026, 7, 9, 2, 0, 0, TimeSpan.Zero);
        store.TryAppend(Event(utcEarly, 1));

        var days = store.SumByDay(-300, DateTimeOffset.MinValue);
        Assert.Equal("2026-07-08", Assert.Single(days).Key);
    }

    [Fact]
    public void 일자별_집계도_롤업을_포함한다()
    {
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        store.TryAppend(Event(T0, 6));
        store.TryAppend(Event(T0.AddHours(5), 4));
        store.RollupAndPrune(T0.AddHours(1), RetentionPolicy.PruneChunkRows);

        var days = store.SumByDay(0, DateTimeOffset.MinValue);
        Assert.Equal(10L, days.Sum(d => d.Sum));
    }

    // ── 가장 이른 시각 ───────────────────────────────────────────────────

    [Fact]
    public void 가장_이른_시각은_롤업된_것도_본다()
    {
        // raw 만 보면 "데이터가 3시간 전부터 있다"고 잘못 말하게 된다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        store.TryAppend(Event(T0, 1));
        store.TryAppend(Event(T0.AddHours(5), 1));
        store.RollupAndPrune(T0.AddHours(1), RetentionPolicy.PruneChunkRows);

        var min = store.MinOccurredAt();
        Assert.NotNull(min);
        Assert.Equal(T0, min!.Value.ToUniversalTime());
    }

    // ── 방향 정규화가 집계까지 이어지는지 ────────────────────────────────

    [Fact]
    public void 대소문자가_다른_방향은_같은_칸으로_모인다()
    {
        // 파이프라인이 정규화하므로 저장 시점에 이미 "in" 이다.
        // 정규화를 빼먹으면 여기서 "in" 과 "IN" 이 두 칸으로 갈라진다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();
        var pipeline = new Core.Ingestion.IngestionPipeline(store);

        pipeline.Ingest(Event(T0, 1, direction: "in"));
        pipeline.Ingest(Event(T0.AddSeconds(1), 2, direction: "IN"));
        pipeline.Ingest(Event(T0.AddSeconds(2), 3, direction: " In "));

        var bucket = Assert.Single(store.SumBySensor(DateTimeOffset.MinValue));
        Assert.Equal("in", bucket.Direction);
        Assert.Equal(6L, bucket.Sum);
    }
}
