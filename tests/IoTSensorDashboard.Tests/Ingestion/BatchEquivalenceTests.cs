using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.Ingestion;

/// <summary>
/// 🔒 절대 규칙 — 묶음 판정 ≡ 건별 판정.
///
/// 배치는 성능을 위해 존재하지만(건별 대비 16배), 빨리 틀리면 의미가 없다.
/// 저장만 한 번에 위임하고 판정은 이벤트마다 그대로 해야 한다.
/// </summary>
public sealed class BatchEquivalenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    /// <summary>판정 5종이 전부 섞인 입력 — 중복·거부·격리·정상이 한 배치에 들어온 상황.</summary>
    private static List<CountEvent?> MixedInput()
    {
        var normal = new CountEvent { SensorId = "flir-0001", OccurredAt = T0, Count = 3, Direction = "in" };

        return
        [
            normal,                                                                                      // Appended
            null,                                                                                        // Rejected
            new CountEvent { SensorId = "  ", OccurredAt = T0, Count = 1 },                               // Rejected
            new CountEvent { SensorId = "flir-0002", OccurredAt = T0, Count = -1 },                       // Rejected
            new CountEvent { SensorId = "flir-0003", OccurredAt = T0, Count = 500 },                      // Implausible
            normal with { Count = 99 },                                                                   // Duplicate (같은 정체성)
            new CountEvent { SensorId = "flir-0004", OccurredAt = T0, Count = 100, Direction = " OUT " }, // Appended (경계값)
            new CountEvent { SensorId = "flir-0005", OccurredAt = T0, Count = IngestionPipeline.MaxCount + 1 }, // Rejected
        ];
    }

    [Fact]
    public void 묶음_판정이_건별_판정과_완전히_같다()
    {
        var input = MixedInput();

        var oneByOneStore = new InMemoryEventStore();
        var oneByOne = new IngestionPipeline(oneByOneStore);
        var expected = input.Select(oneByOne.Ingest).ToArray();

        var batchStore = new InMemoryEventStore();
        var batch = new IngestionPipeline(batchStore);
        var actual = batch.IngestBatch(input).ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(oneByOneStore.Count, batchStore.Count);
    }

    [Fact]
    public void 결과_배열은_입력과_같은_순서다()
    {
        // 🔒 걸러낸 항목의 자리를 당기지 말 것.
        //    자리를 당기면 호출자가 "몇 번째 이벤트가 왜 거부됐는지"를 영원히 알 수 없다.
        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store);

        var input = MixedInput();
        var results = pipeline.IngestBatch(input);

        Assert.Equal(input.Count, results.Count);
        Assert.Equal(IngestResult.Appended, results[0]);
        Assert.Equal(IngestResult.Rejected, results[1]);
        Assert.Equal(IngestResult.Implausible, results[4]);
        Assert.Equal(IngestResult.Duplicate, results[5]);
        Assert.Equal(IngestResult.Appended, results[6]);
    }

    [Fact]
    public void 배치_안의_중복도_접힌다()
    {
        // 같은 배치 안에 같은 정체성이 두 번 들어온 경우 — 저장소가 한 트랜잭션 안에서도 판정해야 한다.
        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store);

        var e = new CountEvent { SensorId = "flir-0001", OccurredAt = T0, Count = 1, Direction = "in" };
        var results = pipeline.IngestBatch([e, e, e]);

        Assert.Equal(IngestResult.Appended, results[0]);
        Assert.Equal(IngestResult.Duplicate, results[1]);
        Assert.Equal(IngestResult.Duplicate, results[2]);
        Assert.Equal(1L, store.Count);
    }

    [Fact]
    public void 빈_배치는_빈_결과다()
    {
        var pipeline = new IngestionPipeline(new InMemoryEventStore());
        Assert.Empty(pipeline.IngestBatch([]));
    }

    [Fact]
    public void 배치_저장이_실패하면_건별로_구제하고_못_살린_것만_센다()
    {
        // 📌 배치는 원자적이라 한 행이 실패하면 배치 전체(최대 512건)가 롤백된다.
        //    건별이었다면 살아남았을 것들이므로, 실패 시 건별로 다시 시도해 구제한다.
        var store = new FlakyStore(failBatch: true, failSingleFor: "flir-0002");
        var pipeline = new IngestionPipeline(store);

        var results = pipeline.IngestBatch(
        [
            new CountEvent { SensorId = "flir-0001", OccurredAt = T0, Count = 1 },
            new CountEvent { SensorId = "flir-0002", OccurredAt = T0, Count = 1 },
            new CountEvent { SensorId = "flir-0003", OccurredAt = T0, Count = 1 },
        ]);

        Assert.Equal(IngestResult.Appended, results[0]);   // 구제됨
        Assert.Equal(IngestResult.Appended, results[2]);   // 구제됨
        Assert.Equal(1, pipeline.StoreFailures);           // 끝내 못 저장한 한 건만 계수
        Assert.Equal(2L, store.Count);
    }

    [Fact]
    public void 저장_실패는_중복으로_보고되지_않는다()
    {
        // 🔴 회귀 방지 — 실제로 이 구현에 있던 결함이다.
        //
        //    AppendWithRescue 가 bool 하나로 결과를 돌려주면
        //    "저장 실패"가 false 로 접히고 호출부가 그걸 Duplicate 로 읽는다.
        //    그러면 화면에는 "중복 N건"으로 뜨는데 실제로는 유실이다.
        //
        //    "이미 세어 둔 값이라 안 넣었다"와 "넣지 못했다"는 정반대 사실이다.
        //    전자는 정상 동작이고 후자는 데이터 손실인데, 화면이 둘을 같은 색으로 그리면
        //    아무도 손실을 눈치채지 못한다.
        var store = new FlakyStore(failBatch: true, failSingleFor: "flir-0002");
        var pipeline = new IngestionPipeline(store);

        var results = pipeline.IngestBatch(
        [
            new CountEvent { SensorId = "flir-0002", OccurredAt = T0, Count = 1 },
        ]);

        Assert.NotEqual(IngestResult.Duplicate, results[0]);
        Assert.NotEqual(IngestResult.Appended, results[0]);
        Assert.Equal(IngestResult.Rejected, results[0]);
        Assert.Equal(1, pipeline.StoreFailures);
        Assert.Equal(0L, store.Count);
    }

    [Fact]
    public void 저장소가_정상이면_저장_실패_카운터는_0이다()
    {
        // 카운터가 "정상일 때 0"이어야 화면의 표시가 근거를 갖는다.
        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store);

        pipeline.IngestBatch(MixedInput());

        Assert.Equal(0, pipeline.StoreFailures);
    }

    /// <summary>배치는 무조건 실패하고, 특정 센서의 건별 저장도 실패하는 저장소.</summary>
    private sealed class FlakyStore(bool failBatch, string failSingleFor) : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();

        public bool TryAppend(CountEvent e)
        {
            if (e.SensorId == failSingleFor) throw new InvalidOperationException("저장 실패(모의)");
            return _inner.TryAppend(e);
        }

        public IReadOnlyList<bool> TryAppendBatch(IReadOnlyList<CountEvent> events)
        {
            if (failBatch) throw new InvalidOperationException("배치 실패(모의)");
            return _inner.TryAppendBatch(events);
        }

        public IReadOnlyList<CountEvent> Snapshot() => _inner.Snapshot();
        public long Count => _inner.Count;
        public bool Contains(string dedupKey) => _inner.Contains(dedupKey);
    }
}
