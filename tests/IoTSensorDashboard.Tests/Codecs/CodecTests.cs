using IoTSensorDashboard.Core.Codecs;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Simulation;
using Xunit;

namespace IoTSensorDashboard.Tests.Codecs;

/// <summary>
/// 코덱 — 이기종 흡수와 무-throw 계약.
///
/// 코덱은 신뢰 경계 밖의 데이터를 다루는 유일한 지점이다.
/// 여기서 예외가 새면 한 건의 망가진 payload 가 수집 루프 전체를 멈춘다.
/// </summary>
public sealed class CodecTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.FromHours(9));

    private static RawPayload Raw(string vendor, string body) =>
        new() { Vendor = vendor, Body = body, ReceivedAt = DateTimeOffset.UtcNow };

    // ── 정상 경로 ────────────────────────────────────────────────────────────

    [Fact]
    public void FLIR_는_라인_수만큼_이벤트를_만든다()
    {
        var body = VendorPayloadFactory.Build("flir", "flir-0001", T0, inCount: 3, outCount: 2);

        var events = new FlirCodec().Decode(Raw("flir", body));

        Assert.Equal(2, events.Count);
        Assert.Equal("flir-0001", events[0].SensorId);
        Assert.Equal("in", events[0].Direction);
        Assert.Equal(3, events[0].Count);
        Assert.Equal("out", events[1].Direction);
        Assert.Equal(2, events[1].Count);
    }

    [Fact]
    public void Milesight_는_in_out_두_건을_만든다()
    {
        var body = VendorPayloadFactory.Build("milesight", "milesight-0002", T0, inCount: 5, outCount: 4);

        var events = new MilesightCodec().Decode(Raw("milesight", body));

        Assert.Equal(2, events.Count);
        Assert.Equal("milesight-0002", events[0].SensorId);
        Assert.Equal("in", events[0].Direction);
        Assert.Equal(5, events[0].Count);
        Assert.Equal("out", events[1].Direction);
        Assert.Equal(4, events[1].Count);
    }

    [Fact]
    public void 형식이_전혀_달라도_같은_표준_이벤트로_접힌다()
    {
        // 🔑 이것이 "이기종 흡수"다. 새 기종을 코덱 하나로 붙일 수 있는 이유.
        var flir = new FlirCodec().Decode(Raw("flir",
            VendorPayloadFactory.Build("flir", "s-1", T0, 7, 1)));
        var mile = new MilesightCodec().Decode(Raw("milesight",
            VendorPayloadFactory.Build("milesight", "s-2", T0, 7, 1)));

        Assert.Equal(flir[0].Count, mile[0].Count);
        Assert.Equal(flir[0].Direction, mile[0].Direction);
        Assert.Equal(flir[0].OccurredAt, mile[0].OccurredAt);
    }

    // ── 시간 규약(I3) ────────────────────────────────────────────────────────

    [Fact]
    public void offset_이_없는_타임스탬프는_UTC_로_간주한다()
    {
        // 📌 기본 파싱은 offset 이 없으면 호스트 로컬 시각으로 본다.
        //    그러면 관제실을 어느 시간대의 PC 에서 돌리느냐에 따라 같은 데이터가 다른 시각으로 저장된다.
        var body = """{"sensorId":"flir-0001","timestamp":"2026-07-09T09:00:00","lines":[{"direction":"in","count":1}]}""";

        var e = Assert.Single(new FlirCodec().Decode(Raw("flir", body)));

        Assert.Equal(TimeSpan.Zero, e.OccurredAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 7, 9, 9, 0, 0, TimeSpan.Zero), e.OccurredAt);
    }

    [Fact]
    public void 표기가_달라도_같은_절대_순간이면_같은_멱등키다()
    {
        var utcBody = """{"sensorId":"s","timestamp":"2026-07-09T00:00:00Z","lines":[{"direction":"in","count":1}]}""";
        var kstBody = """{"sensorId":"s","timestamp":"2026-07-09T09:00:00+09:00","lines":[{"direction":"in","count":1}]}""";

        var codec = new FlirCodec();
        var a = Assert.Single(codec.Decode(Raw("flir", utcBody)));
        var b = Assert.Single(codec.Decode(Raw("flir", kstBody)));

        Assert.Equal(a.DedupKey, b.DedupKey);
    }

    // ── 무-throw 계약 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("이건 JSON 이 아니다")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"sensorId":"s"}""")]
    [InlineData("""{"timestamp":"2026-07-09T09:00:00Z","lines":[]}""")]
    [InlineData("""{"sensorId":"s","timestamp":"이건 시각이 아니다","lines":[]}""")]
    public void FLIR_코덱은_어떤_쓰레기_입력에도_던지지_않는다(string body)
    {
        var events = new FlirCodec().Decode(Raw("flir", body));
        Assert.Empty(events);
    }

    [Theory]
    [InlineData("")]
    [InlineData("깨진 값")]
    [InlineData("{}")]
    [InlineData("""{"deviceId":"s"}""")]
    [InlineData("""{"deviceId":"","time":"2026-07-09T09:00:00Z","periodIn":1,"periodOut":1}""")]
    public void Milesight_코덱은_어떤_쓰레기_입력에도_던지지_않는다(string body)
    {
        var events = new MilesightCodec().Decode(Raw("milesight", body));
        Assert.Empty(events);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 센서를_모르는_페이로드는_빈_리스트다(string? sensorId)
    {
        // 센서를 모르는 이벤트는 저장할 곳이 없다 — 라인이 아무리 멀쩡해도 버린다.
        var idJson = sensorId is null ? "null" : $"\"{sensorId}\"";
        var body = $$"""{"sensorId":{{idJson}},"timestamp":"2026-07-09T09:00:00Z","lines":[{"direction":"in","count":1}]}""";

        Assert.Empty(new FlirCodec().Decode(Raw("flir", body)));
    }

    // ── 형제 보존 ────────────────────────────────────────────────────────────

    [Fact]
    public void 한_라인이_깨져도_정상_형제_라인은_보존된다()
    {
        // 📌 in/out 중 out 만 깨졌다고 in 까지 버리면, 데이터가 있는데 안 세게 된다.
        var body = """
        {"sensorId":"flir-0001","timestamp":"2026-07-09T09:00:00Z",
         "lines":[{"direction":"in","count":3},{"direction":"out"},{"direction":"in","count":9}]}
        """;

        var events = new FlirCodec().Decode(Raw("flir", body));

        Assert.Equal(2, events.Count);
        Assert.Equal(3, events[0].Count);
        Assert.Equal(9, events[1].Count);
    }

    [Fact]
    public void Milesight_도_한쪽_방향만_깨지면_나머지를_살린다()
    {
        var body = """{"deviceId":"milesight-0002","time":"2026-07-09T09:00:00Z","periodIn":5}""";

        var e = Assert.Single(new MilesightCodec().Decode(Raw("milesight", body)));

        Assert.Equal("in", e.Direction);
        Assert.Equal(5, e.Count);
    }

    // ── 코덱은 판정하지 않는다 ───────────────────────────────────────────────

    [Fact]
    public void 코덱은_값을_판정하지_않고_그대로_넘긴다()
    {
        // 🔑 코덱은 "파싱만" 한다. 음수든 불가능한 값이든 판정은 파이프라인 몫이다.
        //    코덱에 판정 책임을 주면 새 기종을 추가할 때마다 불변식이 깨질 기회가 생긴다.
        var body = """{"sensorId":"s","timestamp":"2026-07-09T09:00:00Z","lines":[{"direction":"in","count":-5},{"direction":"out","count":99999}]}""";

        var events = new FlirCodec().Decode(Raw("flir", body));

        Assert.Equal(2, events.Count);
        Assert.Equal(-5, events[0].Count);
        Assert.Equal(99999, events[1].Count);
    }

    // ── 발행 측 단일 소스 ────────────────────────────────────────────────────

    [Fact]
    public void 발행_포맷과_파싱은_같은_소스에서_나온다()
    {
        // 📌 발행 코드와 파싱 코드가 각각 포맷을 알고 있으면 한쪽만 바뀌었을 때 조용히 어긋난다.
        //    발행은 되는데 파싱이 빈 리스트를 돌려주고, 아무 오류도 안 난다.
        foreach (var vendor in VendorPayloadFactory.KnownVendors)
        {
            var body = VendorPayloadFactory.Build(vendor, $"{vendor}-0001", T0, 11, 22);
            var registry = new CodecRegistry(new FlirCodec(), new MilesightCodec());

            var events = registry.Decode(Raw(vendor, body));

            Assert.Equal(2, events.Count);
            Assert.Equal(11, events[0].Count);
            Assert.Equal(22, events[1].Count);
        }
    }

    [Fact]
    public void 발행_측은_미지_벤더에_대해_즉시_터진다()
    {
        // 🔒 여기서만은 관대하지 않다. 발행 측은 우리가 만든 코드이므로 오타는 즉시 드러나야 한다.
        Assert.Throws<ArgumentException>(() =>
            VendorPayloadFactory.Build("axis", "axis-0001", T0, 1, 1));
    }
}
