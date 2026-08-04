using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.Ingestion;

/// <summary>
/// 관측은 판정에 절대 영향을 주지 않는다.
///
/// 관측이 판정을 바꿀 수 있으면 "지표를 켰더니 숫자가 달라지는" 시스템이 된다.
/// 그러면 지표를 믿을 수 없고, 지표를 못 믿으면 관측 자체가 무의미하다.
/// </summary>
public sealed class ObserverTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    private static CountEvent Event(int i) =>
        new() { SensorId = "flir-0001", OccurredAt = T0.AddSeconds(i), Count = 1, Direction = "in" };

    [Fact]
    public void 던지는_관측자도_판정과_저장을_바꾸지_않는다()
    {
        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store, new ThrowingObserver());

        for (int i = 0; i < 5; i++)
            Assert.Equal(IngestResult.Appended, pipeline.Ingest(Event(i)));

        Assert.Equal(5L, store.Count);
    }

    [Fact]
    public void 관측_실패는_삼키되_반드시_센다()
    {
        // 🔒 화면의 "관측 실패 0"은 이 카운터가 근거다.
        //    카운터 없이 "관측 정상"이라고 쓰면 그게 금지된 안심 문구다.
        var pipeline = new IngestionPipeline(new InMemoryEventStore(), new ThrowingObserver());

        for (int i = 0; i < 3; i++) pipeline.Ingest(Event(i));

        Assert.Equal(3, pipeline.ObserverFailures);
    }

    [Fact]
    public void 배치에서도_관측_실패가_판정을_막지_않는다()
    {
        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store, new ThrowingObserver());

        var results = pipeline.IngestBatch([Event(0), Event(1), null]);

        Assert.Equal(IngestResult.Appended, results[0]);
        Assert.Equal(IngestResult.Appended, results[1]);
        Assert.Equal(IngestResult.Rejected, results[2]);
        Assert.Equal(3, pipeline.ObserverFailures);
    }

    [Fact]
    public void 지표는_판정_결과를_그대로_누적한다()
    {
        var metrics = new PipelineMetrics();
        var pipeline = new IngestionPipeline(new InMemoryEventStore(), metrics);

        pipeline.Ingest(Event(0));                                                    // Appended
        pipeline.Ingest(Event(0));                                                    // Duplicate
        pipeline.Ingest(null);                                                        // Rejected
        pipeline.Ingest(Event(1) with { Count = 500 });                               // Implausible

        var s = metrics.Snapshot();
        Assert.Equal(4, s.Received);
        Assert.Equal(1, s.Appended);
        Assert.Equal(1, s.Duplicate);
        Assert.Equal(1, s.Rejected);
        Assert.Equal(1, s.Implausible);
    }

    [Fact]
    public void 지연은_음수가_되지_않는다()
    {
        // 📌 일부 하드웨어에서 Stopwatch 가 비단조일 수 있다.
        //    음수 delta 는 계측을 왜곡하고 테스트를 flaky 하게 만든다.
        var metrics = new PipelineMetrics();
        var pipeline = new IngestionPipeline(new InMemoryEventStore(), metrics);

        for (int i = 0; i < 50; i++) pipeline.Ingest(Event(i));

        var s = metrics.Snapshot();
        Assert.True(s.TotalLatencyMicros >= 0);
        Assert.True(s.MaxLatencyMicros >= 0);
        Assert.True(s.AvgLatencyMicros >= 0);
    }

    [Fact]
    public void 수신이_없으면_평균_지연은_0이다()
    {
        // 나눗셈 방어. 0 으로 나눠 NaN 이 화면에 뜨면 그것도 "모르는 것을 아는 척"이다.
        Assert.Equal(0, new PipelineMetrics().Snapshot().AvgLatencyMicros);
    }

    [Fact]
    public void 동시_관측에서도_누적이_새지_않는다()
    {
        // Interlocked 를 쓰지 않으면 여기서 값이 빈다.
        var metrics = new PipelineMetrics();
        var pipeline = new IngestionPipeline(new InMemoryEventStore(), metrics);

        const int Total = 1_000;
        Parallel.For(0, Total, i => pipeline.Ingest(Event(i)));

        Assert.Equal(Total, metrics.Snapshot().Received);
    }

    private sealed class ThrowingObserver : IPipelineObserver
    {
        public void OnIngested(in PipelineEvent ev) =>
            throw new InvalidOperationException("관측자 폭발(모의)");
    }
}
