using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.Storage;

/// <summary>
/// 영속 저장소의 기본 계약 — 인메모리 구현과 <b>똑같이 동작해야</b> 한다.
/// 두 구현이 다르게 굴면 갈아 끼울 수 없고, 그러면 플러그인 축이 거짓이 된다.
/// </summary>
public sealed class SqliteEventStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    private static CountEvent Event(string sensorId = "flir-0001", int count = 3,
                                    string? direction = "in", DateTimeOffset? at = null)
        => new() { SensorId = sensorId, OccurredAt = at ?? T0, Count = count, Direction = direction };

    // ── 연결 설정 ────────────────────────────────────────────────────────

    [Fact]
    public void 신규_DB_는_INCREMENTAL_모드다()
    {
        // 📌 auto_vacuum 은 빈 DB 에서만 설정이 먹는다.
        //    테이블을 만든 뒤에 걸면 조용히 무시되고, 오류도 안 난다.
        //    그러면 나중에 "지우고 있는데 파일이 안 줄어드는" 상태가 된다.
        //
        //    모드 값: 0=NONE, 1=FULL, 2=INCREMENTAL
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        Assert.Equal(2, store.Stats().AutoVacuumMode);
    }

    [Fact]
    public void 저장은_실제_파일에_남는다()
    {
        using var temp = new TempDb();

        using (var store = temp.OpenStore())
            store.TryAppend(Event());

        Assert.True(temp.FileBytes > 0);
    }

    // ── I1 · 정확히 1회 (영속 구현) ──────────────────────────────────────

    [Fact]
    public void 같은_이벤트를_재전송해도_한_건만_저장된다()
    {
        // 🔑 INSERT OR IGNORE 가 곧 I1 이다. 애플리케이션이 판정하지 않는다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        Assert.True(store.TryAppend(Event()));
        Assert.False(store.TryAppend(Event()));
        Assert.False(store.TryAppend(Event()));

        Assert.Equal(1L, store.Count);
    }

    [Fact]
    public void 동시_저장에서도_정확히_한_건이다()
    {
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        var e = Event();
        int appended = 0;

        Parallel.For(0, 32, _ =>
        {
            if (store.TryAppend(e)) Interlocked.Increment(ref appended);
        });

        Assert.Equal(1, appended);
        Assert.Equal(1L, store.Count);
    }

    [Fact]
    public void 다른_값으로_재전송해도_최초본이_남는다()
    {
        // I2 — append-only. INSERT OR IGNORE 는 덮어쓰지 않는다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        store.TryAppend(Event(count: 3));
        store.TryAppend(Event(count: 99));

        Assert.Equal(3, Assert.Single(store.Snapshot()).Count);
    }

    [Fact]
    public void 방향이_없는_이벤트도_저장된다()
    {
        // events 테이블의 direction 은 NULL 을 허용한다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        Assert.True(store.TryAppend(Event(direction: null)));
        Assert.Null(Assert.Single(store.Snapshot()).Direction);
    }

    [Fact]
    public void 스냅샷은_삽입_순서를_보존한다()
    {
        // SQLite 내장 rowid 가 삽입 순서를 보존하므로 별도 seq 컬럼이 필요 없다.
        // (예전에 그 컬럼이 있었을 때 삽입마다 MAX(seq) 전체 스캔이 일어나 O(n²) 였다.)
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        for (int i = 0; i < 30; i++)
            store.TryAppend(Event(at: T0.AddSeconds(i), count: i));

        Assert.Equal(Enumerable.Range(0, 30).ToArray(),
                     store.Snapshot().Select(e => e.Count).ToArray());
    }

    [Fact]
    public void Contains_는_멱등키로_판정한다()
    {
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        var e = Event();
        Assert.False(store.Contains(e.DedupKey));

        store.TryAppend(e);
        Assert.True(store.Contains(e.DedupKey));
    }

    // ── 배치 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 묶음_판정이_건별_판정과_완전히_같다()
    {
        // 🔒 배치는 성능을 위해 있지만, 빨리 틀리면 의미가 없다.
        var input = new List<CountEvent>
        {
            Event(),
            Event(direction: "out"),
            Event(count: 99),                        // 같은 정체성 → 중복
            Event(sensorId: "flir-0002"),
            Event(at: T0.AddSeconds(1)),
        };

        using var tempA = new TempDb();
        using var oneByOne = tempA.OpenStore();
        var expected = input.Select(oneByOne.TryAppend).ToArray();

        using var tempB = new TempDb();
        using var batch = tempB.OpenStore();
        var actual = batch.TryAppendBatch(input).ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(oneByOne.Count, batch.Count);
    }

    [Fact]
    public void 배치_안의_중복도_접힌다()
    {
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        var e = Event();
        var results = store.TryAppendBatch([e, e, e]);

        Assert.True(results[0]);
        Assert.False(results[1]);
        Assert.False(results[2]);
        Assert.Equal(1L, store.Count);
    }

    [Fact]
    public void 빈_배치와_한_건_배치도_동작한다()
    {
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        Assert.Empty(store.TryAppendBatch([]));
        Assert.True(Assert.Single(store.TryAppendBatch([Event()])));
    }

    [Fact]
    public void 파이프라인을_통과한_결과도_인메모리와_같다()
    {
        // 저장소를 갈아 끼워도 판정 결과가 같아야 한다 — 그게 플러그인 축의 의미다.
        var input = new List<CountEvent?>
        {
            Event(),
            null,
            Event(count: -1),
            Event(count: 500),
            Event(count: 99),      // 중복
        };

        var memory = new IngestionPipeline(new InMemoryEventStore());
        var expected = memory.IngestBatch(input).ToArray();

        using var temp = new TempDb();
        using var store = temp.OpenStore();
        var persistent = new IngestionPipeline(store);

        Assert.Equal(expected, persistent.IngestBatch(input).ToArray());
    }
}
