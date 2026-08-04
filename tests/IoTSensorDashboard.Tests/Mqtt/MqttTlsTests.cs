using IoTSensorDashboard.Mqtt;
using MQTTnet;
using MQTTnet.Client;
using Xunit;

namespace IoTSensorDashboard.Tests.Mqtt;

/// <summary>
/// TLS — 켤 거면 제대로 켠다.
/// </summary>
[Collection("mqtt-integration")]
public sealed class MqttTlsTests
{
    [Fact]
    public void 자체서명_인증서에_개인키가_들어_있다()
    {
        // ⚠️ 여기가 함정이었다.
        //    메모리 전용 키(EphemeralKeySet)를 쓰면 Windows 의 TLS 구현이 서버 쪽에서 그 키를 못 쓴다.
        //    증상이 오류가 아니라 "연결이 그냥 끊김"이라 방화벽·포트를 한참 뒤지게 된다.
        //    PFX 로 한 번 내보냈다 다시 읽으면 서버가 쓸 수 있는 형태가 보장된다.
        using var cert = DevTls.CreateSelfSigned();

        Assert.True(cert.HasPrivateKey, "개인키가 없으면 서버가 TLS 핸드셰이크를 완료할 수 없다");
    }

    [Fact]
    public void 인증서_사양이_명세와_일치한다()
    {
        using var cert = DevTls.CreateSelfSigned();

        Assert.Equal("CN=localhost", cert.Subject);
        Assert.Equal(cert.Subject, cert.Issuer);                    // 자체서명 = 발급자가 자기 자신
        Assert.True(cert.NotAfter > DateTime.Now.AddYears(4));      // 유효기간 5년
        Assert.True(cert.NotBefore < DateTime.Now);                 // 시계 오차 대비로 하루 앞당김
    }

    [Fact]
    public async Task TLS_브로커에_TLS_클라이언트가_붙는다()
    {
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: DevTls.CreateSelfSigned());
        await broker.StartAsync();

        Assert.True(broker.IsEncrypted);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var client = new MqttFactory().CreateMqttClient();
        var options = MqttClientOptionsFactory.Create("tls-client", "127.0.0.1", port, useTls: true);

        var result = await client.ConnectAsync(options, cts.Token);

        Assert.Equal(MqttClientConnectResultCode.Success, result.ResultCode);
        Assert.True(client.IsConnected);

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task TLS_브로커는_평문_접속을_거부한다()
    {
        // 🔒 TLS 를 켜면 평문 포트를 같이 열지 않는다.
        //    둘 다 열면 TLS 는 장식이다 — 아무나 평문으로 붙으면 그만이다.
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: DevTls.CreateSelfSigned());
        await broker.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var client = new MqttFactory().CreateMqttClient();
        var plainOptions = MqttClientOptionsFactory.Create("plain-client", "127.0.0.1", port, useTls: false);

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(plainOptions, cts.Token));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task TLS_위에서도_왕복이_된다()
    {
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: DevTls.CreateSelfSigned());
        await broker.StartAsync();

        int received = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var source = new MqttIngestionSource("tls-ingest", "127.0.0.1", port, useTls: true);
        var run = source.RunAsync(_ =>
        {
            Interlocked.Increment(ref received);
            return Task.CompletedTask;
        }, cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => source.IsConnected, MqttTestHelpers.Timeout));

        await using var publisher = new MqttSensorPublisher("tls-farm", "127.0.0.1", port, useTls: true);
        await publisher.ConnectAsync(cts.Token);
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => publisher.IsConnected, MqttTestHelpers.Timeout));

        await publisher.PublishAsync("flir", "g1-s0", "flir-0001",
            Core.Simulation.VendorPayloadFactory.Build("flir", "flir-0001", DateTimeOffset.UtcNow, 1, 1),
            cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => Volatile.Read(ref received) > 0,
                                                          MqttTestHelpers.Timeout),
                    "TLS 경로로 메시지가 도착하지 않았다");

        await cts.CancelAsync();
        await run;
    }
}
