using System.Collections.Concurrent;
using IoTSensorDashboard.Core.Codecs;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Simulation;
using IoTSensorDashboard.Core.Storage;
using IoTSensorDashboard.Mqtt;
using Xunit;

namespace IoTSensorDashboard.Tests.Mqtt;

/// <summary>
/// 실제 브로커를 띄워서 하는 왕복 검증.
///
/// 여기부터는 순수 함수가 아니라 <b>진짜 네트워크</b>다.
/// 단위 테스트가 전부 통과해도 여기서 막히는 경우가 있고, 그게 이 테스트가 있는 이유다.
/// </summary>
[Collection("mqtt-integration")]
public sealed class MqttRoundTripTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task 발행한_것이_코덱을_거쳐_표준_이벤트로_도착한다()
    {
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: null);
        await broker.StartAsync();

        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store);
        var registry = new CodecRegistry(new FlirCodec(), new MilesightCodec());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var source = new MqttIngestionSource("test-ingest", "127.0.0.1", port, useTls: false);
        var run = source.RunAsync(raw =>
        {
            foreach (var e in registry.Decode(raw)) pipeline.Ingest(e);
            return Task.CompletedTask;
        }, cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => source.IsConnected, MqttTestHelpers.Timeout),
                    "수집 채널이 브로커에 붙지 못했다");

        await using var publisher = new MqttSensorPublisher("test-farm", "127.0.0.1", port, useTls: false);
        await publisher.ConnectAsync(cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => publisher.IsConnected, MqttTestHelpers.Timeout),
                    "발행자가 브로커에 붙지 못했다");

        var payload = VendorPayloadFactory.Build("flir", "flir-0001", T0, inCount: 3, outCount: 2);
        await publisher.PublishAsync("flir", "g1-s0", "flir-0001", payload, cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => store.Count == 2, MqttTestHelpers.Timeout),
                    $"이벤트가 도착하지 않았다 (저장 {store.Count}건)");

        var events = store.Snapshot();
        Assert.Equal("flir-0001", events[0].SensorId);
        Assert.Equal(3, events[0].Count);
        Assert.Equal("in", events[0].Direction);
        Assert.Equal(2, events[1].Count);
        Assert.Equal("out", events[1].Direction);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task 이기종을_섞어_보내도_같은_저장소로_들어간다()
    {
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: null);
        await broker.StartAsync();

        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store);
        var registry = new CodecRegistry(new FlirCodec(), new MilesightCodec());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var source = new MqttIngestionSource("test-ingest-2", "127.0.0.1", port, useTls: false);
        var run = source.RunAsync(raw =>
        {
            foreach (var e in registry.Decode(raw)) pipeline.Ingest(e);
            return Task.CompletedTask;
        }, cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => source.IsConnected, MqttTestHelpers.Timeout));

        await using var publisher = new MqttSensorPublisher("test-farm-2", "127.0.0.1", port, useTls: false);
        await publisher.ConnectAsync(cts.Token);
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => publisher.IsConnected, MqttTestHelpers.Timeout));

        for (int i = 0; i < 10; i++)
        {
            var vendor = i % 2 == 0 ? "flir" : "milesight";
            var sensorId = $"{vendor}-{i:D4}";
            var body = VendorPayloadFactory.Build(vendor, sensorId, T0.AddSeconds(i), 1, 1);
            await publisher.PublishAsync(vendor, "g1-s0", sensorId, body, cts.Token);
        }

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => store.Count == 20, MqttTestHelpers.Timeout),
                    $"이기종 관통 실패 (저장 {store.Count}건)");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task 토픽의_사이트가_스트림을_타고_흐른다()
    {
        // 📌 소비 측이 스트림에서 받은 사이트로 집계할 수 있어야 별도 조회가 필요 없다.
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: null);
        await broker.StartAsync();

        var sources = new ConcurrentBag<string?>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var source = new MqttIngestionSource("test-ingest-3", "127.0.0.1", port, useTls: false);
        var run = source.RunAsync(raw =>
        {
            sources.Add(raw.Source);
            return Task.CompletedTask;
        }, cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => source.IsConnected, MqttTestHelpers.Timeout));

        await using var publisher = new MqttSensorPublisher("test-farm-3", "127.0.0.1", port, useTls: false);
        await publisher.ConnectAsync(cts.Token);
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => publisher.IsConnected, MqttTestHelpers.Timeout));

        await publisher.PublishAsync("milesight", "g2-s3", "milesight-0007",
            VendorPayloadFactory.Build("milesight", "milesight-0007", T0, 1, 1), cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => !sources.IsEmpty, MqttTestHelpers.Timeout));

        var topic = sources.First();
        Assert.NotNull(topic);
        Assert.Contains("g2-s3", topic!, StringComparison.Ordinal);
        Assert.Equal("g2-s3", SensorTopic.SiteOf(topic!.Replace("mqtt:", "", StringComparison.Ordinal)));

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task retain_을_쓰지_않으므로_새_구독자는_옛_값을_받지_않는다()
    {
        // 🚫 retain 을 쓰면 죽은 센서의 마지막 값이 새로 접속한 대시보드에
        //    "방금 온 값"처럼 보인다. "모르는 것을 아는 것처럼 그리지 마라"에 정면으로 어긋난다.
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: null);
        await broker.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // 먼저 발행해 둔다 — 구독자가 아직 없다.
        await using var publisher = new MqttSensorPublisher("test-farm-4", "127.0.0.1", port, useTls: false);
        await publisher.ConnectAsync(cts.Token);
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => publisher.IsConnected, MqttTestHelpers.Timeout));

        await publisher.PublishAsync("flir", "g1-s0", "flir-0001",
            VendorPayloadFactory.Build("flir", "flir-0001", T0, 9, 9), cts.Token);

        // 그 뒤에 구독자가 붙는다.
        int received = 0;
        await using var source = new MqttIngestionSource("test-ingest-4", "127.0.0.1", port, useTls: false);
        var run = source.RunAsync(_ =>
        {
            Interlocked.Increment(ref received);
            return Task.CompletedTask;
        }, cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => source.IsConnected, MqttTestHelpers.Timeout));

        // 잠깐 기다려도 옛 값은 오지 않아야 한다.
        await Task.Delay(500, cts.Token);
        Assert.Equal(0, Volatile.Read(ref received));

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task 헬스_핑에_살아있는_센서만_응답한다()
    {
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: null);
        await broker.StartAsync();

        var engine = new SensorFarmEngine(new Core.Provisioning.SiteProvisioning(4));
        var deadSensor = Core.Provisioning.SiteProvisioning.SensorIdFor(1);
        engine.SetOffline(deadSensor, true);

        var acked = new ConcurrentBag<string>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var probe = new MqttHealthProbe("test-probe", "127.0.0.1", port, useTls: false);
        probe.AckReceived += id => acked.Add(id);
        await probe.ConnectAsync(cts.Token);
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => probe.IsConnected, MqttTestHelpers.Timeout));

        await using var farm = new MqttSensorPublisher("test-farm-5", "127.0.0.1", port, useTls: false);
        farm.PingReceived += async body =>
        {
            foreach (var id in engine.AckTargets(body))
                await farm.PublishAckAsync(id, cts.Token);
        };
        await farm.ConnectAsync(cts.Token);
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => farm.IsConnected, MqttTestHelpers.Timeout));

        await probe.PingAsync(null, cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => acked.Count == 3, MqttTestHelpers.Timeout),
                    $"ACK 수가 맞지 않는다 ({acked.Count}건)");

        // 🔒 죽은 센서가 응답하면 관제실이 그 센서를 영원히 못 찾는다.
        Assert.DoesNotContain(deadSensor, acked);

        await cts.CancelAsync();
    }
}
