using IoTSensorDashboard.Core.Codecs;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Simulation;
using IoTSensorDashboard.Core.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.Codecs;

/// <summary>
/// 코덱 라우팅 — 새 기종 추가가 "구현 1클래스 + 등록 1줄"로 끝나는지.
/// </summary>
public sealed class CodecRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    private static CodecRegistry NewRegistry() => new(new FlirCodec(), new MilesightCodec());

    private static RawPayload Raw(string vendor, string body) =>
        new() { Vendor = vendor, Body = body, ReceivedAt = DateTimeOffset.UtcNow };

    [Theory]
    [InlineData("flir")]
    [InlineData("FLIR")]
    [InlineData("Flir")]
    public void 벤더_매칭은_대소문자를_가리지_않는다(string vendor)
    {
        var body = VendorPayloadFactory.Build("flir", "flir-0001", T0, 1, 1);

        Assert.Equal(2, NewRegistry().Decode(Raw(vendor, body)).Count);
    }

    [Fact]
    public void 미지_벤더는_예외가_아니라_빈_리스트다()
    {
        // 밖에서 온 토픽 하나가 수집 루프를 멈추게 할 수는 없다.
        var body = VendorPayloadFactory.Build("flir", "flir-0001", T0, 1, 1);

        Assert.Empty(NewRegistry().Decode(Raw("axis", body)));
    }

    [Fact]
    public void 등록_한_줄로_새_기종이_붙는다()
    {
        var registry = NewRegistry();
        Assert.Empty(registry.Decode(Raw("acme", "무엇이든")));

        registry.Register(new AcmeCodec());

        var e = Assert.Single(registry.Decode(Raw("acme", "42")));
        Assert.Equal(42, e.Count);
    }

    [Fact]
    public void 라우팅_키가_비어_있는_코덱은_등록_시점에_막힌다()
    {
        // 📌 Vendor 가 비면 영원히 선택되지 않는다 — 등록은 됐는데 아무 일도 안 일어난다.
        //    "조용히 동작하지 않는" 상태를 만들 바에는 등록 시점에 터지는 게 낫다.
        Assert.Throws<ArgumentException>(() => NewRegistry().Register(new NamelessCodec()));
    }

    [Fact]
    public void 이기종을_섞어_보내도_전부_같은_저장소로_들어간다()
    {
        // 인수 검증 항목: "FLIR·Milesight 를 섞어도 같은 표준 이벤트로".
        var store = new InMemoryEventStore();
        var coordinator = new IngestionCoordinator(NewRegistry(), new IngestionPipeline(store));

        for (int i = 0; i < 10; i++)
        {
            var vendor = i % 2 == 0 ? "flir" : "milesight";
            var body = VendorPayloadFactory.Build(vendor, $"{vendor}-{i:D4}", T0.AddSeconds(i), 1, 1);
            coordinator.Feed(Raw(vendor, body));
        }

        Assert.Equal(20L, store.Count);   // 센서 10대 × in/out 2건
    }

    [Fact]
    public void 미지_벤더가_섞여도_나머지는_정상_수집된다()
    {
        var store = new InMemoryEventStore();
        var coordinator = new IngestionCoordinator(NewRegistry(), new IngestionPipeline(store));

        coordinator.Feed(Raw("flir", VendorPayloadFactory.Build("flir", "flir-0001", T0, 1, 1)));
        coordinator.Feed(Raw("axis", "알 수 없는 형식"));
        coordinator.Feed(Raw("milesight", VendorPayloadFactory.Build("milesight", "milesight-0002", T0, 1, 1)));

        Assert.Equal(4L, store.Count);
    }

    /// <summary>본문을 정수 하나로 보는 최소 코덱 — "등록 1줄"을 증명하기 위한 것.</summary>
    private sealed class AcmeCodec : ISensorCodec
    {
        public string Vendor => "acme";

        public IReadOnlyList<CountEvent> Decode(RawPayload raw)
        {
            if (!int.TryParse(raw.Body, out var count)) return [];

            return [new CountEvent
            {
                SensorId = "acme-0001",
                OccurredAt = T0,
                Count = count,
                Direction = "in"
            }];
        }
    }

    private sealed class NamelessCodec : ISensorCodec
    {
        public string Vendor => "";
        public IReadOnlyList<CountEvent> Decode(RawPayload raw) => [];
    }
}
