using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.InvariantGates;

/// <summary>
/// G-I1 · 정확히 1회
///
/// Given  파이프라인과 저장소가 준비된 상태
/// When   같은 정체성(센서·시각·방향)의 이벤트를 중복·순단 후 재전송
/// Then   저장 카운트 == 논리적 발생 횟수
///
/// 깨지면: 네트워크 순단·재전송·중복 전송 시 숫자가 새거나 두 번 세진다 → 통계 거짓.
/// </summary>
public sealed class G_I1_ExactlyOnceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    private static (IngestionPipeline Pipeline, InMemoryEventStore Store) NewPipeline()
    {
        var store = new InMemoryEventStore();
        return (new IngestionPipeline(store), store);
    }

    private static CountEvent Event(string sensorId = "flir-0001", int count = 3, string? direction = "in", DateTimeOffset? at = null)
        => new() { SensorId = sensorId, OccurredAt = at ?? T0, Count = count, Direction = direction };

    [Fact]
    public void 같은_이벤트를_다섯_번_재전송해도_저장은_한_건이다()
    {
        var (pipeline, store) = NewPipeline();
        var e = Event();

        var results = Enumerable.Range(0, 5).Select(_ => pipeline.Ingest(e)).ToList();

        Assert.Equal(1L, store.Count);
        Assert.Equal(IngestResult.Appended, results[0]);
        Assert.All(results.Skip(1), r => Assert.Equal(IngestResult.Duplicate, r));
    }

    [Fact]
    public void 값이_흔들린_재전송도_같은_논리_이벤트다_최초본이_권위를_갖는다()
    {
        // 📌 DedupKey 에 Count 가 없는 이유를 못박는 테스트.
        //    Count 를 키에 넣으면 값만 다른 재전송이 새 이벤트로 들어와 같은 순간을 두 번 센다.
        var (pipeline, store) = NewPipeline();

        Assert.Equal(IngestResult.Appended, pipeline.Ingest(Event(count: 3)));
        Assert.Equal(IngestResult.Duplicate, pipeline.Ingest(Event(count: 7)));

        Assert.Equal(1L, store.Count);
        Assert.Equal(3, store.Snapshot()[0].Count);   // 최초본이 남는다(append-only, I2)
    }

    [Fact]
    public void 방향이_다르면_다른_이벤트다()
    {
        // 📌 한 센서가 같은 순간에 in 과 out 을 각각 보고한다.
        //    Direction 이 키에 없으면 둘 중 하나가 중복으로 접혀 사라진다.
        var (pipeline, store) = NewPipeline();

        Assert.Equal(IngestResult.Appended, pipeline.Ingest(Event(direction: "in")));
        Assert.Equal(IngestResult.Appended, pipeline.Ingest(Event(direction: "out")));

        Assert.Equal(2L, store.Count);
    }

    [Fact]
    public void 다른_센서의_같은_시각_이벤트는_다른_이벤트다()
    {
        var (pipeline, store) = NewPipeline();

        pipeline.Ingest(Event(sensorId: "flir-0001"));
        pipeline.Ingest(Event(sensorId: "flir-0002"));

        Assert.Equal(2L, store.Count);
    }

    [Fact]
    public void 표기가_달라도_같은_절대_순간이면_같은_이벤트다()
    {
        // 📌 "+09:00" 표기와 "Z" 표기가 같은 순간이면 같은 이벤트다.
        //    DedupKey 가 ToUnixTimeMilliseconds() 로 비교하는 이유.
        var (pipeline, store) = NewPipeline();

        var utc = new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);
        var kst = new DateTimeOffset(2026, 7, 9, 9, 0, 0, TimeSpan.FromHours(9));
        Assert.Equal(utc.ToUnixTimeMilliseconds(), kst.ToUnixTimeMilliseconds());

        pipeline.Ingest(Event(at: utc));
        pipeline.Ingest(Event(at: kst));

        Assert.Equal(1L, store.Count);
    }

    [Fact]
    public void 대소문자만_다른_방향은_같은_이벤트다()
    {
        // 📌 정규화가 없으면 "IN" 과 "in" 이 다른 멱등키가 되어 같은 순간을 두 번 센다.
        var (pipeline, store) = NewPipeline();

        pipeline.Ingest(Event(direction: "in"));
        pipeline.Ingest(Event(direction: "  IN  "));

        Assert.Equal(1L, store.Count);
    }

    [Fact]
    public void 동시_수집에서도_정확히_한_번만_저장된다()
    {
        // 📌 "먼저 조회하고 없으면 넣기"로 구현하면 여기서 깨진다.
        //    저장소가 원자적으로 판정해야 한다(인메모리는 락, SQLite 는 INSERT OR IGNORE).
        var (pipeline, store) = NewPipeline();
        var e = Event();

        const int Threads = 64;
        var appended = 0;

        Parallel.For(0, Threads, _ =>
        {
            if (pipeline.Ingest(e) == IngestResult.Appended)
                Interlocked.Increment(ref appended);
        });

        Assert.Equal(1L, store.Count);
        Assert.Equal(1, appended);   // 승자는 정확히 하나
    }

    [Fact]
    public void 순단_후_백필_재전송이_섞여도_논리_발생_횟수와_같다()
    {
        // 센서가 10건을 보내다 끊기고, 복구 후 원본 타임스탬프로 전부 다시 보낸 상황.
        // 겹치는 구간은 dedup 이 접어야 한다.
        var (pipeline, store) = NewPipeline();

        var logical = Enumerable.Range(0, 10)
            .Select(i => Event(at: T0.AddSeconds(i), count: 1))
            .ToList();

        foreach (var e in logical.Take(6)) pipeline.Ingest(e);   // 끊기기 전까지 수신
        foreach (var e in logical) pipeline.Ingest(e);           // 복구 후 전량 백필

        Assert.Equal(10L, store.Count);
    }
}
