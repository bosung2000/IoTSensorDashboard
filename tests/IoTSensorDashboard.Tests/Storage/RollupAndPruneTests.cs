using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.Storage;

/// <summary>
/// 롤업 · 프룬 — 오래된 원본을 집계로 <b>승격</b>하고 지운다(I2).
///
/// 이건 "데이터를 버리는 것"이 아니라 "정보를 보존하면서 자리를 줄이는 것"이다.
/// 그래서 총계는 절대 변하지 않아야 한다.
///
/// 📌 왜 필요한가: 보존·롤업 없이 7시간 돌렸더니 DB 가 778MB 로 불어나
///    시스템이 스스로 느려졌다. "데이터를 다 보관한다"가 자가 다운의 원인이었다.
/// </summary>
public sealed class RollupAndPruneTests
{
    /// <summary>기준 시각. 09:00 UTC.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    private static CountEvent Event(DateTimeOffset at, int count = 1,
                                    string sensorId = "flir-0001", string? direction = "in")
        => new() { SensorId = sensorId, OccurredAt = at, Count = count, Direction = direction };

    // ── 기본 동작 ────────────────────────────────────────────────────────

    [Fact]
    public void 보존창을_지난_원본은_집계로_접히고_삭제된다()
    {
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        // 09:00 대에 3건
        store.TryAppend(Event(T0, 5));
        store.TryAppend(Event(T0.AddMinutes(10), 3));
        store.TryAppend(Event(T0.AddMinutes(20), 2));

        var pruned = store.RollupAndPrune(T0.AddHours(1), RetentionPolicy.PruneChunkRows);

        Assert.Equal(3, pruned);
        Assert.Empty(store.Snapshot());          // 원본은 사라졌지만
        Assert.Equal(3L, store.Count);           // 총계는 그대로다 (raw + 롤업)
    }

    [Fact]
    public void 총계가_보존된다()
    {
        // 🔴 이게 이 기능의 존재 이유다.
        //    raw 만 세면 롤업이 돌 때마다 총계가 줄어드는 것처럼 보이고, 그건 거짓말이다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        for (int i = 0; i < 100; i++)
            store.TryAppend(Event(T0.AddSeconds(i)));

        var before = store.Count;
        store.RollupAndPrune(T0.AddHours(1), RetentionPolicy.PruneChunkRows);

        Assert.Equal(before, store.Count);
        Assert.Equal(100L, store.Count);
    }

    [Fact]
    public void 보존창_안의_행은_지워지지_않는다()
    {
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        store.TryAppend(Event(T0, 1));                    // 오래된 것
        store.TryAppend(Event(T0.AddHours(2), 2));        // 보존창 안

        store.RollupAndPrune(T0.AddHours(1), RetentionPolicy.PruneChunkRows);

        var remaining = Assert.Single(store.Snapshot());
        Assert.Equal(2, remaining.Count);
        Assert.Equal(2L, store.Count);
    }

    [Fact]
    public void 지울_것이_없으면_0을_반환한다()
    {
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        store.TryAppend(Event(T0.AddHours(5)));

        Assert.Equal(0, store.RollupAndPrune(T0, RetentionPolicy.PruneChunkRows));
        Assert.Single(store.Snapshot());
    }

    // ── 시간 버킷 (I3) ───────────────────────────────────────────────────

    [Fact]
    public void 같은_시간대는_한_칸으로_접힌다()
    {
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        // 09:00 ~ 09:59 → 한 칸
        store.TryAppend(Event(T0.AddMinutes(0), 1));
        store.TryAppend(Event(T0.AddMinutes(30), 2));
        store.TryAppend(Event(T0.AddMinutes(59), 3));
        // 10:00 → 다른 칸
        store.TryAppend(Event(T0.AddMinutes(60), 4));

        store.RollupAndPrune(T0.AddHours(5), RetentionPolicy.PruneChunkRows);

        var buckets = store.SumBySensor(DateTimeOffset.MinValue);
        var bucket = Assert.Single(buckets);           // 센서 1대 · 방향 1개

        Assert.Equal(10L, bucket.Sum);                 // 1+2+3+4
        Assert.Equal(4L, bucket.Rows);
    }

    [Fact]
    public void 방향이_다르면_다른_칸이다()
    {
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        store.TryAppend(Event(T0, 5, direction: "in"));
        store.TryAppend(Event(T0, 3, direction: "out"));

        store.RollupAndPrune(T0.AddHours(1), RetentionPolicy.PruneChunkRows);

        var buckets = store.SumBySensor(DateTimeOffset.MinValue);
        Assert.Equal(2, buckets.Count);
        Assert.Equal(5L, buckets.Single(b => b.Direction == "in").Sum);
        Assert.Equal(3L, buckets.Single(b => b.Direction == "out").Sum);
    }

    [Fact]
    public void 방향이_없는_이벤트는_빈_문자열로_접힌다()
    {
        // events 는 NULL 허용, events_hourly 는 복합 PK 라 NULL 대신 빈 문자열을 쓴다.
        // SQL 에서 NULL 은 비교가 애매해 PK 로 쓰기 곤란하기 때문이다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        store.TryAppend(Event(T0, 7, direction: null));
        store.RollupAndPrune(T0.AddHours(1), RetentionPolicy.PruneChunkRows);

        var bucket = Assert.Single(store.SumBySensor(DateTimeOffset.MinValue));
        Assert.Equal("", bucket.Direction);
        Assert.Equal(7L, bucket.Sum);
    }

    [Fact]
    public void 두_번_롤업하면_같은_칸에_누적된다()
    {
        // UPSERT — 같은 (센서, 시간, 방향) 칸이 이미 있으면 더한다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        store.TryAppend(Event(T0.AddMinutes(0), 5));
        store.RollupAndPrune(T0.AddHours(1), RetentionPolicy.PruneChunkRows);

        store.TryAppend(Event(T0.AddMinutes(10), 3));    // 같은 09시 대
        store.RollupAndPrune(T0.AddHours(1), RetentionPolicy.PruneChunkRows);

        var bucket = Assert.Single(store.SumBySensor(DateTimeOffset.MinValue));
        Assert.Equal(8L, bucket.Sum);
        Assert.Equal(2L, bucket.Rows);
        Assert.Equal(2L, store.Count);
    }

    // ── 조각으로 나눠 처리 ───────────────────────────────────────────────

    [Fact]
    public void 조각_상한을_넘지_않는다()
    {
        // 📌 락 점유 시간을 제한하는 것이 목적이다.
        //    한 번에 수십만 행을 처리하면 그동안 수집 워커가 통째로 멈춘다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        for (int i = 0; i < 50; i++)
            store.TryAppend(Event(T0.AddSeconds(i)));

        Assert.Equal(20, store.RollupAndPrune(T0.AddHours(1), maxRows: 20));
        Assert.Equal(30, store.Snapshot().Count);
    }

    [Fact]
    public void 잘라서_처리해도_합계가_같다()
    {
        // 🔴 조각으로 나누는 것이 결과를 바꾸면 안 된다.
        //    한 번에 처리한 것과 나눠 처리한 것이 한 글자도 달라선 안 된다.
        using var tempA = new TempDb();
        using var atOnce = tempA.OpenStore();

        using var tempB = new TempDb();
        using var inChunks = tempB.OpenStore();

        for (int i = 0; i < 100; i++)
        {
            var e = Event(T0.AddSeconds(i), count: i % 7);
            atOnce.TryAppend(e);
            inChunks.TryAppend(e);
        }

        atOnce.RollupAndPrune(T0.AddHours(1), 1000);

        int guard = 0;
        while (inChunks.RollupAndPrune(T0.AddHours(1), 7) > 0)
            if (++guard > 100) break;   // 무한 루프 방어

        Assert.Equal(atOnce.Count, inChunks.Count);
        Assert.Equal(
            atOnce.SumBySensor(DateTimeOffset.MinValue).Select(b => (b.Key, b.Direction, b.Sum, b.Rows)),
            inChunks.SumBySensor(DateTimeOffset.MinValue).Select(b => (b.Key, b.Direction, b.Sum, b.Rows)));
    }

    [Fact]
    public void 조각_상한이_0_이하이면_막는다()
    {
        // 0 이면 아무것도 처리하지 못하는데 호출은 성공한다 —
        // 유지보수 루프가 영원히 헛돌고 DB 는 계속 커진다. 그런 상태를 만들지 않는다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => store.RollupAndPrune(T0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.RollupAndPrune(T0, -1));
    }

    // ── 재시작을 넘어서 ──────────────────────────────────────────────────

    [Fact]
    public void 롤업_결과는_재시작_후에도_남는다()
    {
        using var temp = new TempDb();

        using (var store = temp.OpenStore())
        {
            for (int i = 0; i < 10; i++) store.TryAppend(Event(T0.AddSeconds(i), 2));
            store.RollupAndPrune(T0.AddHours(1), RetentionPolicy.PruneChunkRows);
        }

        using (var reopened = temp.OpenStore())
        {
            Assert.Equal(10L, reopened.Count);
            Assert.Empty(reopened.Snapshot());
            Assert.Equal(20L, Assert.Single(reopened.SumBySensor(DateTimeOffset.MinValue)).Sum);
        }
    }
}
