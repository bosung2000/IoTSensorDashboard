using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.Ingestion;

/// <summary>
/// 판정 순서와 정규화 — "돌아가는데 틀린"이 나오는 자리.
/// </summary>
public sealed class IngestionPipelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    private static IngestionPipeline NewPipeline(out InMemoryEventStore store)
    {
        store = new InMemoryEventStore();
        return new IngestionPipeline(store);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 센서를_모르는_이벤트는_거부된다(string? sensorId)
    {
        var pipeline = NewPipeline(out var store);

        var result = pipeline.Ingest(new CountEvent
        {
            SensorId = sensorId!,
            OccurredAt = T0,
            Count = 1
        });

        Assert.Equal(IngestResult.Rejected, result);
        Assert.Equal(0L, store.Count);
    }

    [Fact]
    public void null_이벤트는_거부된다()
    {
        var pipeline = NewPipeline(out _);
        Assert.Equal(IngestResult.Rejected, pipeline.Ingest(null));
    }

    [Theory]
    [InlineData("in", "in")]
    [InlineData("IN", "in")]
    [InlineData("  Out  ", "out")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void 방향_정규화는_소비_측과_같은_함수를_쓴다(string? input, string? expected)
    {
        // 🔑 public static 이어야 대시보드의 in/out 분기가 같은 규칙을 쓸 수 있다.
        Assert.Equal(expected, IngestionPipeline.NormalizeDirection(input));
    }

    [Fact]
    public void 저장된_이벤트의_방향은_정규화된_값이다()
    {
        var pipeline = NewPipeline(out var store);

        pipeline.Ingest(new CountEvent { SensorId = "flir-0001", OccurredAt = T0, Count = 1, Direction = " IN " });

        Assert.Equal("in", Assert.Single(store.Snapshot()).Direction);
    }

    [Fact]
    public void 판정_순서는_거부가_격리보다_먼저다()
    {
        // 📌 오버플로 가드를 먼저 통과시켜야 "망가진 데이터"와 "불가능한 데이터"가 섞이지 않는다.
        //    MaxCount 를 넘는 값은 정합 한계도 넘지만, 결과는 Rejected 여야 한다.
        var pipeline = NewPipeline(out _);

        var huge = new CountEvent
        {
            SensorId = "flir-0001",
            OccurredAt = T0,
            Count = IngestionPipeline.MaxCount + 1
        };

        Assert.Equal(IngestResult.Rejected, pipeline.Ingest(huge));
    }

    [Fact]
    public void 저장소가_null_이면_생성자가_막는다()
    {
        Assert.Throws<ArgumentNullException>(() => new IngestionPipeline(null!));
    }

    [Fact]
    public void 관측자를_주지_않아도_동작한다()
    {
        // NullPipelineObserver 가 null 검사를 코드 전체에 흩뿌리지 않게 해 준다.
        var pipeline = NewPipeline(out var store);

        Assert.Equal(IngestResult.Appended,
            pipeline.Ingest(new CountEvent { SensorId = "flir-0001", OccurredAt = T0, Count = 1 }));
        Assert.Equal(1L, store.Count);
        Assert.Equal(0, pipeline.ObserverFailures);
    }
}
