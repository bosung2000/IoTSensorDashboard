using IoTSensorDashboard.Mqtt;
using MQTTnet;
using MQTTnet.Client;
using Xunit;

namespace IoTSensorDashboard.Tests.Mqtt;

/// <summary>
/// 재연결 — 이 프로젝트에서 <b>가장 비쌌던 사고</b>의 회귀 방지.
///
/// 📌 무슨 일이 있었나:
///    절전 7시간 복귀 후 수집과 대시보드가 영구 정지했다.
///    센서 팜만 살아 있어 "발행은 되는데 아무도 수신 못 함" 상태였고,
///    화면은 멀쩡해 보여서 "네트워크 문제"로 오진하기 딱 좋았다.
///
///    원인: DisconnectedAsync 는 "연결됐던 세션이 끊길 때"만 발생한다.
///    핸들러에서 ConnectAsync 를 한 번만 호출하고 실패를 삼키면,
///    클라이언트는 (재)연결된 적이 없으므로 그 이벤트가 다시 발생하지 않는다 → 영구 정지.
/// </summary>
[Collection("mqtt-integration")]
public sealed class MqttReconnectTests
{
    /// <summary>테스트에서는 백오프를 짧게 준다. 규약이 아니라 대기 시간만 줄이는 것이다.</summary>
    private static readonly TimeSpan FastBackoff = TimeSpan.FromMilliseconds(200);

    [Fact]
    public void keepalive_는_5초다()
    {
        // 📌 기본값 15초면 좀비 소켓 감지가 늦고, 1~2초면 부하 시 오탐 단절이 난다.
        //    이 값이 되돌아가면 절전 복귀 사고가 그대로 재현된다.
        Assert.Equal(TimeSpan.FromSeconds(5), MqttReconnect.RecommendedKeepAlive);
    }

    [Fact]
    public void 클라이언트_ID_는_고정이고_세션은_유지된다()
    {
        // 📌 WithCleanSession(false) 와 고정 ID 는 짝이다.
        //    브로커가 단절 중 QoS1 메시지를 그 클라이언트 앞으로 큐잉했다가 재연결 시 재전달한다.
        //    ID 가 랜덤이면 재연결할 때마다 다른 클라이언트가 되어 그 큐가 통째로 버려진다.
        var a = MqttClientOptionsFactory.Create("ingest-main", useTls: false);
        var b = MqttClientOptionsFactory.Create("ingest-main", useTls: false);

        Assert.Equal("ingest-main", a.ClientId);
        Assert.Equal(a.ClientId, b.ClientId);
        Assert.False(a.CleanSession);
        Assert.Equal(MqttReconnect.RecommendedKeepAlive, a.KeepAlivePeriod);
    }

    [Fact]
    public void 클라이언트_ID_가_비면_막는다()
    {
        // 빈 ID 는 브로커가 임의로 배정한다 — 그러면 세션 유지가 깨진다.
        Assert.Throws<ArgumentException>(() => MqttClientOptionsFactory.Create(""));
        Assert.Throws<ArgumentNullException>(() => MqttClientOptionsFactory.Create(null!));
    }

    [Fact]
    public async Task 브로커가_죽었다_살아나면_자동으로_다시_붙는다()
    {
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: null);
        await broker.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var client = new MqttFactory().CreateMqttClient();
        var options = MqttClientOptionsFactory.Create("reconnect-client", "127.0.0.1", port, useTls: false);

        client.EnableAutoReconnect(options, cts.Token, FastBackoff);
        await client.ConnectAsync(options, cts.Token);

        Assert.True(client.IsConnected);

        // 브로커를 죽인다.
        await broker.StopAsync();
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => !client.IsConnected, MqttTestHelpers.Timeout),
                    "브로커가 죽었는데 클라이언트가 끊김을 감지하지 못했다");

        // 살린다.
        await broker.StartAsync();

        // 🔴 여기가 핵심이다. while 루프가 아니면 여기서 영원히 안 붙는다.
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => client.IsConnected, TimeSpan.FromSeconds(20)),
                    "브로커가 돌아왔는데 클라이언트가 재연결하지 못했다 — 재연결이 단발 호출로 바뀌었을 가능성");

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task 여러_번_끊겨도_계속_다시_붙는다()
    {
        // 한 번만 되면 "우연히 됐을" 수 있다. 반복해도 되는지가 진짜 확인이다.
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: null);
        await broker.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        using var client = new MqttFactory().CreateMqttClient();
        var options = MqttClientOptionsFactory.Create("reconnect-loop", "127.0.0.1", port, useTls: false);

        client.EnableAutoReconnect(options, cts.Token, FastBackoff);
        await client.ConnectAsync(options, cts.Token);

        for (int round = 0; round < 3; round++)
        {
            await broker.StopAsync();
            Assert.True(await MqttTestHelpers.WaitUntilAsync(() => !client.IsConnected, MqttTestHelpers.Timeout),
                        $"{round + 1}회차: 끊김을 감지하지 못했다");

            await broker.StartAsync();
            Assert.True(await MqttTestHelpers.WaitUntilAsync(() => client.IsConnected, TimeSpan.FromSeconds(20)),
                        $"{round + 1}회차: 재연결하지 못했다");
        }

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task 브로커보다_먼저_떠도_결국_붙는다()
    {
        // 📌 기동 순서(관제실 → 팜 → 대시보드)를 지키면 안 생기는 문제지만,
        //    <b>순서에 의존하는 시스템은 언젠가 깨진다.</b>
        //
        //    그리고 이 경우는 특히 위험하다 — 한 번도 연결된 적이 없으면
        //    DisconnectedAsync 가 아예 발생하지 않아 재연결 루프가 스스로 시작되지 않는다.
        int port = MqttTestHelpers.FreePort();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // 브로커 없이 먼저 띄운다.
        await using var source = new MqttIngestionSource("early-bird", "127.0.0.1", port, useTls: false);
        var run = source.RunAsync(_ => Task.CompletedTask, cts.Token);

        await Task.Delay(500, cts.Token);
        Assert.False(source.IsConnected);

        // 이제 브로커를 띄운다.
        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: null);
        await broker.StartAsync();

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => source.IsConnected, TimeSpan.FromSeconds(20)),
                    "브로커가 나중에 떴을 때 붙지 못했다");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task 재연결_후_구독이_복원된다()
    {
        // 🔑 다시 붙기만 하고 재구독을 안 하면 "연결은 됐는데 아무것도 안 오는" 상태가 된다.
        //    화면의 연결 칩은 초록인데 데이터가 멈춘다 — 가장 찾기 어려운 부류다.
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: null);
        await broker.StartAsync();

        int received = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        await using var source = new MqttIngestionSource("resub-ingest", "127.0.0.1", port, useTls: false);
        var run = source.RunAsync(_ =>
        {
            Interlocked.Increment(ref received);
            return Task.CompletedTask;
        }, cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => source.IsConnected, MqttTestHelpers.Timeout));

        // 끊었다 붙인다.
        await broker.StopAsync();
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => !source.IsConnected, MqttTestHelpers.Timeout));
        await broker.StartAsync();
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => source.IsConnected, TimeSpan.FromSeconds(25)));

        // 재연결 후에도 메시지가 와야 한다.
        await using var publisher = new MqttSensorPublisher("resub-farm", "127.0.0.1", port, useTls: false);
        await publisher.ConnectAsync(cts.Token);
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => publisher.IsConnected, MqttTestHelpers.Timeout));

        await publisher.PublishAsync("flir", "g1-s0", "flir-0001",
            Core.Simulation.VendorPayloadFactory.Build("flir", "flir-0001", DateTimeOffset.UtcNow, 1, 1),
            cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => Volatile.Read(ref received) > 0,
                                                          TimeSpan.FromSeconds(15)),
                    "재연결 후 구독이 복원되지 않았다 — 연결은 됐는데 데이터가 안 온다");

        await cts.CancelAsync();
        await run;
    }
}
