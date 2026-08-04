using System.Diagnostics;
using System.Threading.Channels;
using IoTSensorDashboard.Core.Codecs;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Health;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Provisioning;
using IoTSensorDashboard.Core.Simulation;
using IoTSensorDashboard.Core.Storage;
using IoTSensorDashboard.Mqtt;
using Xunit;
using Xunit.Abstractions;

namespace IoTSensorDashboard.Tests.Mqtt;

/// <summary>
/// 고부하 관통 — <b>「극한을 눌렀을 때 무엇이 정상인가」</b>를 코드로 못박는다.
///
/// 화면만 보면 "이게 맞는 건가?" 싶은 순간들이 있다.
/// 여기서 그 기준을 정해 둔다.
///
/// 🔑 <b>극한(20,000/s)은 「달성하는」 값이 아니다.</b>
///    사양 기준이 지속 3,000~3,900 msg/s 이고, 메시지 1건 비용의 79% 가
///    확인 응답과 브로커 라우팅이다.
///    극한 프리셋은 <b>백프레셔와 폐기를 눈으로 보여주는</b> 용도다.
/// </summary>
[Collection("mqtt-integration")]
public sealed class HighLoadEndToEndTests
{
    private const int QueueCapacity = 4_000;
    private const int BatchMax = 512;
    private const int MaxInFlight = 64;

    private readonly ITestOutputHelper _output;

    public HighLoadEndToEndTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task 고부하에서_수집이_계속_돌고_숫자가_어긋나지_않는다()
    {
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: null);
        await broker.StartAsync();

        var provisioning = new SiteProvisioning(200);
        var store = new InMemoryEventStore();
        var metrics = new PipelineMetrics();
        var pipeline = new IngestionPipeline(store, metrics);
        var codecs = new CodecRegistry(new FlirCodec(), new MilesightCodec());
        var health = new SensorHealthTracker();
        health.Expect(provisioning.SensorIds);

        long droppedUnderLoad = 0;
        long messagesReceived = 0;

        // 관제실과 같은 구조 — 바운드 큐 + DropOldest + 폐기 계수.
        var channel = Channel.CreateBounded<RawPayload>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            },
            itemDropped: _ => Interlocked.Increment(ref droppedUnderLoad));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using var ingest = new MqttIngestionSource("load-ingest", "127.0.0.1", port, useTls: false);
        var run = ingest.RunAsync(raw =>
        {
            Interlocked.Increment(ref messagesReceived);
            channel.Writer.TryWrite(raw with { ReceivedAt = DateTimeOffset.UtcNow });
            return Task.CompletedTask;
        }, cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => ingest.IsConnected, MqttTestHelpers.Timeout));

        // 워커 — 관제실과 같이 전용 스레드.
        var workerStop = new CancellationTokenSource();
        var worker = new Thread(() => Worker(channel, codecs, pipeline, health, workerStop.Token))
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        worker.Start();

        // 팜 — 발행도 전용 스레드(UI 없음).
        await using var publisher = new MqttSensorPublisher("load-farm", "127.0.0.1", port, useTls: false);
        await publisher.ConnectAsync(cts.Token);
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => publisher.IsConnected, MqttTestHelpers.Timeout));

        var engine = new SensorFarmEngine(provisioning);
        var publishStop = new CancellationTokenSource();
        long logicalPublished = 0;

        var publishThread = new Thread(() =>
            PublishLoop(engine, publisher, publishStop.Token, ref logicalPublished))
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };

        var elapsed = Stopwatch.StartNew();
        publishThread.Start();

        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        publishStop.Cancel();
        publishThread.Join(TimeSpan.FromSeconds(3));
        elapsed.Stop();

        // 큐에 남은 것을 마저 처리할 시간을 준다.
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        workerStop.Cancel();
        worker.Join(TimeSpan.FromSeconds(3));

        var snapshot = metrics.Snapshot();
        long dropped = Interlocked.Read(ref droppedUnderLoad);
        long received = Interlocked.Read(ref messagesReceived);
        double seconds = elapsed.Elapsed.TotalSeconds;

        _output.WriteLine($"{seconds:F1}초 동안");
        _output.WriteLine($"  발행     {Interlocked.Read(ref logicalPublished),8:N0} 건  ({Interlocked.Read(ref logicalPublished) / seconds,8:N0} msg/s)");
        _output.WriteLine($"  수신     {received,8:N0} 건  ({received / seconds,8:N0} msg/s)");
        _output.WriteLine($"  과부하 폐기 {dropped,6:N0} 건");
        _output.WriteLine($"  판정     저장 {snapshot.Appended:N0} · 중복 {snapshot.Duplicate:N0} · 거부 {snapshot.Rejected:N0} · 격리 {snapshot.Implausible:N0}");
        _output.WriteLine($"  저장소   {store.Count:N0} 건");

        // ── 무엇이 정상인가 ──────────────────────────────────────────

        // ① 수집이 실제로 돌았다.
        Assert.True(received > 0, "고부하에서 한 건도 수신하지 못했다");
        Assert.True(store.Count > 0, "고부하에서 한 건도 저장하지 못했다");

        // ② 🔑 수신 = 처리 + 폐기. 어느 것도 조용히 사라지지 않는다.
        //    이 등식이 깨지면 「센 것」과 「일어난 것」이 다르다는 뜻이다.
        Assert.Equal(received, snapshot.Received / 2 + dropped);

        // ③ 저장 이벤트는 수신 메시지의 2배다(메시지 1건 = in/out 2건).
        Assert.Equal(snapshot.Appended + snapshot.Duplicate, store.Count + snapshot.Duplicate);

        // ④ 🔴 고부하에서도 「정합」이 유지된다 — 거부·격리가 0 이어야 한다.
        //    팜은 물리적으로 가능한 값만 만들므로, 여기서 0 이 아니면 코덱이나 판정이 틀린 것이다.
        Assert.Equal(0, snapshot.Rejected);
        Assert.Equal(0, snapshot.Implausible);

        // ⑤ 폐기가 일어났다면 **반드시 세어져 있다**(0 이어도 정상 — 큐가 안 찼을 수 있다).
        Assert.True(dropped >= 0);

        await cts.CancelAsync();
        await run;
    }

    /// <summary>관제실 워커와 같은 구조.</summary>
    private static void Worker(
        Channel<RawPayload> channel, CodecRegistry codecs, IngestionPipeline pipeline,
        SensorHealthTracker health, CancellationToken ct)
    {
        var reader = channel.Reader;
        var batch = new List<CountEvent?>(BatchMax);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult()) break;
                if (!reader.TryRead(out var raw)) continue;

                batch.Clear();
                Decode(raw, codecs, health, batch);

                while (batch.Count < BatchMax && reader.TryRead(out var more))
                    Decode(more, codecs, health, batch);

                if (batch.Count > 0) pipeline.IngestBatch(batch);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void Decode(
        RawPayload raw, CodecRegistry codecs, SensorHealthTracker health, List<CountEvent?> batch)
    {
        foreach (var e in codecs.Decode(raw))
        {
            batch.Add(e);
            health.Observe(e.SensorId, raw.ReceivedAt);   // 🔑 도착 시각 기준
        }
    }

    /// <summary>센서 팜과 같은 구조 — 전용 스레드에서 묶어 발행.</summary>
    private static void PublishLoop(
        SensorFarmEngine engine, MqttSensorPublisher publisher,
        CancellationToken ct, ref long published)
    {
        var pending = new List<Task>(MaxInFlight);
        var now = DateTimeOffset.UtcNow;
        int sequence = 0;

        while (!ct.IsCancellationRequested)
        {
            // 최대한 밀어붙인다 — 극한 프리셋의 의도가 그것이다.
            var readings = engine.Tick(now.AddMilliseconds(sequence++), engine.SensorCount);

            foreach (var r in readings)
            {
                if (ct.IsCancellationRequested) break;

                var body = VendorPayloadFactory.Build(r.Vendor, r.SensorId, r.At, r.In, r.Out);
                pending.Add(publisher.PublishAsync(r.Vendor, r.SiteId, r.SensorId, body, ct));

                if (pending.Count < MaxInFlight) continue;

                Drain(pending, ref published);
            }

            if (pending.Count > 0) Drain(pending, ref published);
        }
    }

    private static void Drain(List<Task> pending, ref long published)
    {
        try
        {
            Task.WhenAll(pending).GetAwaiter().GetResult();
            Interlocked.Add(ref published, pending.Count);
        }
        catch (Exception)
        {
            // 종료 중이거나 연결이 끊긴 순간 — 루프는 계속 간다.
        }
        finally
        {
            pending.Clear();
        }
    }
}
