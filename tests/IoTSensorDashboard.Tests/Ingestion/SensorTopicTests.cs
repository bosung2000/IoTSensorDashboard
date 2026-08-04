using IoTSensorDashboard.Core.Ingestion;
using Xunit;

namespace IoTSensorDashboard.Tests.Ingestion;

/// <summary>
/// 토픽 규약 — 세 앱이 서로를 아는 유일한 통로.
///
/// 🔴 여기 적힌 문자열이 한 글자라도 다르면 앱은 각각 잘 뜨는데
///    데이터가 흐르지 않는다 — 오류 메시지도 없이.
/// </summary>
public sealed class SensorTopicTests
{
    [Fact]
    public void 발행_토픽은_네_세그먼트다()
    {
        Assert.Equal("sensors/flir/g1-s0/flir-0001",
                     SensorTopic.For("flir", "g1-s0", "flir-0001"));
    }

    [Fact]
    public void 세그먼트_위치가_계약이다()
    {
        const string topic = "sensors/milesight/g2-s3/milesight-0007";

        Assert.Equal("milesight", SensorTopic.VendorOf(topic));    // [1] 코덱 라우팅 키
        Assert.Equal("g2-s3", SensorTopic.SiteOf(topic));          // [2] 매장 맥락
        Assert.Equal("milesight-0007", SensorTopic.SensorIdOf(topic));
    }

    [Fact]
    public void 옛_세_세그먼트_형태에서도_벤더는_읽힌다()
    {
        // 📌 하위 호환 — vendor 는 어느 쪽이든 [1] 이다.
        //    파싱은 관대하게 두되, 발행은 반드시 4세그먼트로 한다.
        const string old = "sensors/flir/flir-0001";

        Assert.Equal("flir", SensorTopic.VendorOf(old));
        Assert.Equal("flir-0001", SensorTopic.SiteOf(old));   // 자리가 밀린다 — 그래서 발행은 4세그먼트
        Assert.Null(SensorTopic.SensorIdOf(old));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sensors")]
    [InlineData("sensors/")]
    [InlineData("sensors//g1-s0/x")]
    public void 망가진_토픽에서는_null_을_돌려준다(string? topic)
    {
        // 밖에서 오는 것은 불신한다. 예외를 던지면 수신 루프가 멈춘다.
        Assert.Null(SensorTopic.VendorOf(topic));
    }

    [Fact]
    public void 사이트가_없으면_null_이고_폴백은_소비_측_몫이다()
    {
        // 🔑 null 을 "건너뛰기"로 처리하면 그 센서가 속한 매장이 화면에서 통째로 사라진다.
        //    소비 측은 프로비저닝 명부로 폴백해야 한다.
        Assert.Null(SensorTopic.SiteOf("sensors/flir"));
    }

    [Fact]
    public void ACK_토픽에서_센서_ID_를_뽑는다()
    {
        Assert.Equal("flir-0001", SensorTopic.SensorIdOfAck("health/ack/flir-0001"));
    }

    [Theory]
    [InlineData("health/ack/")]
    [InlineData("health/ping")]
    [InlineData("sensors/flir/g1-s0/flir-0001")]
    [InlineData(null)]
    public void ACK_가_아닌_토픽은_null_이다(string? topic)
    {
        Assert.Null(SensorTopic.SensorIdOfAck(topic));
    }

    [Theory]
    [InlineData("sensors/flir/g1-s0/flir-0001", true)]
    [InlineData("health/ping", false)]
    [InlineData("health/ack/flir-0001", false)]
    [InlineData(null, false)]
    public void 센서_데이터_토픽을_구분한다(string? topic, bool expected)
    {
        Assert.Equal(expected, SensorTopic.IsSensorData(topic));
    }

    [Fact]
    public void 토픽_상수가_명세와_일치한다()
    {
        // 이 문자열들이 곧 앱 간 계약이다. 바뀌면 데이터가 조용히 안 흐른다.
        Assert.Equal("sensors/#", SensorTopic.SensorFilter);
        Assert.Equal("health/ping", SensorTopic.HealthPing);
        Assert.Equal("health/ack/", SensorTopic.HealthAckPrefix);
        Assert.Equal("*", SensorTopic.PingAll);
    }
}
