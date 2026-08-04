using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.InvariantGates;

/// <summary>
/// G-I7 · 물리적으로 불가능한 값은 격리한다
///
/// Given  MaxPlausibleCountPerReading = 100
/// When   Count = 500 인 이벤트 유입
/// Then   Implausible 로 격리 · 저장·집계 미포함 · 계수 증가
///
/// 추가: 경계값 100 은 통과(&gt; 비교) · Rejected 와 구분된다.
///
/// 깨지면: 센서 글리치 한 건이 하루 통계를 통째로 망친다. 그리고 아무도 모른다.
/// </summary>
public sealed class G_I7_ImplausibleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    private static CountEvent Event(int count) =>
        new() { SensorId = "flir-0001", OccurredAt = T0.AddMilliseconds(count), Count = count, Direction = "in" };

    [Fact]
    public void 불가능한_스파이크는_격리되고_저장되지_않는다()
    {
        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store);

        Assert.Equal(IngestResult.Implausible, pipeline.Ingest(Event(500)));
        Assert.Equal(0L, store.Count);
    }

    [Fact]
    public void 경계값_100은_통과한다()
    {
        // 📌 비교가 '>' 인지 '>=' 인지가 여기서 갈린다.
        //    100 을 막으면 "물리적 한계 100" 이라는 말과 코드가 어긋난다.
        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store);

        Assert.Equal(IngestResult.Appended, pipeline.Ingest(Event(100)));
        Assert.Equal(IngestResult.Implausible, pipeline.Ingest(Event(101)));
        Assert.Equal(1L, store.Count);
    }

    [Fact]
    public void 격리는_거부와_구분된다()
    {
        // 📌 Rejected = "데이터가 망가졌다"(발신 측 버그)
        //    Implausible = "데이터는 멀쩡한데 현실적으로 불가능하다"(센서 글리치)
        //    원인이 다르므로 대응도 다르고, 화면에도 각각 표시한다.
        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store);

        Assert.Equal(IngestResult.Rejected, pipeline.Ingest(Event(-1)));                      // 음수
        Assert.Equal(IngestResult.Rejected, pipeline.Ingest(Event(IngestionPipeline.MaxCount + 1)));  // 오버플로 가드
        Assert.Equal(IngestResult.Implausible, pipeline.Ingest(Event(500)));                  // 물리 정합

        Assert.Equal(0L, store.Count);
    }

    [Fact]
    public void 오버플로_가드_경계도_통과한다()
    {
        // MaxCount 자체는 거부 대상이 아니다. 다만 정합 한계를 넉넉히 올려야 여기까지 온다.
        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store, maxPlausibleCountPerReading: IngestionPipeline.MaxCount);

        Assert.Equal(IngestResult.Appended, pipeline.Ingest(Event(IngestionPipeline.MaxCount)));
        Assert.Equal(IngestResult.Rejected, pipeline.Ingest(Event(IngestionPipeline.MaxCount + 1)));
    }

    [Fact]
    public void 정합_한계는_주입할_수_있다()
    {
        // 실운영에서는 매장·측정 간격별로 보정할 값이다. 검증이 경계를 바꿔 볼 수 있어야 한다.
        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store, maxPlausibleCountPerReading: 10);

        Assert.Equal(10, pipeline.MaxPlausibleCountPerReading);
        Assert.Equal(IngestResult.Appended, pipeline.Ingest(Event(10)));
        Assert.Equal(IngestResult.Implausible, pipeline.Ingest(Event(11)));
    }

    [Fact]
    public void 정합_한계가_0_이하이면_생성자가_막는다()
    {
        // 📌 0 이면 모든 이벤트가 격리되어 수집이 통째로 멈춘다 — 그런데 오류는 안 난다.
        //    "조용히 전부 버리는" 상태를 만들 바에는 생성 시점에 터지는 게 낫다.
        var store = new InMemoryEventStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestionPipeline(store, maxPlausibleCountPerReading: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IngestionPipeline(store, maxPlausibleCountPerReading: -1));
    }

    [Fact]
    public void 격리된_건도_수집_요약에_남는다()
    {
        // 🔒 조용히 버리지 말 것. 격리 건수가 화면에 보여야 한다.
        var store = new InMemoryEventStore();
        var metrics = new PipelineMetrics();
        var pipeline = new IngestionPipeline(store, metrics);

        pipeline.Ingest(Event(1));     // Appended
        pipeline.Ingest(Event(500));   // Implausible
        pipeline.Ingest(Event(-1));    // Rejected

        var snapshot = metrics.Snapshot();
        Assert.Equal(3, snapshot.Received);
        Assert.Equal(1, snapshot.Appended);
        Assert.Equal(1, snapshot.Implausible);
        Assert.Equal(1, snapshot.Rejected);
    }
}
