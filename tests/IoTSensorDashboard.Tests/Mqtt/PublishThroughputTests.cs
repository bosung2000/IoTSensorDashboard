using System.Diagnostics;
using IoTSensorDashboard.Core.Provisioning;
using IoTSensorDashboard.Core.Simulation;
using IoTSensorDashboard.Mqtt;
using Xunit;
using Xunit.Abstractions;

namespace IoTSensorDashboard.Tests.Mqtt;

/// <summary>
/// 발행 처리량 — <b>순차 대기가 왜 병목인가</b>.
///
/// 📌 근거 — 실측 결함:
///    센서 팜이 발행을 <b>UI 스레드에서 순차로 await</b> 했다.
///    극한(20,000/s) 프리셋에서
///      · 화면이 통째로 멈췄고
///      · 실제 발행량이 목표의 2.4%(483/s)에 그쳤고
///      · 일부 센서가 임계 안에 발행을 못 해 관제실 온라인 수가 널뛰었다.
///
/// 여기서 확인하는 것: <b>여러 건을 동시에 띄워 두면 처리량이 오른다.</b>
/// QoS1 은 발행마다 확인 응답(PUBACK)을 기다리므로,
/// 하나씩 기다리면 그 왕복이 그대로 상한이 된다.
/// </summary>
[Collection("mqtt-integration")]
public sealed class PublishThroughputTests
{
    private const int Messages = 400;
    private const int MaxInFlight = 64;

    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public PublishThroughputTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 🔴 <b>이 테스트가 내 가설을 반증했다.</b>
    ///
    /// 처음에는 「QoS1 확인 응답 왕복이 병목이니 묶어서 보내면 빨라진다」고 가정했다.
    /// 재 보니 <b>루프백에서는 순차가 오히려 두 배 빨랐다</b>(23,901 vs 11,951 msg/s).
    /// 브로커가 같은 프로세스 안에 있어 왕복이 40µs 수준이라, 묶는 비용이 이득보다 컸다.
    ///
    /// 그래서 이 테스트는 「묶음이 빠르다」를 주장하지 않는다.
    /// <b>실제 조건(구독자가 붙어 브로커가 바쁜 상태)에서 두 방식을 재고 기록만 한다.</b>
    ///
    /// 🧭 교훈: <b>「빠를 것 같다」로 구조를 바꾸지 말 것.</b>
    ///    재 보고 나서 바꾼다. 이 프로젝트에서 그러지 않아 생긴 결함이 여러 건이다.
    /// </summary>
    [Fact]
    public async Task 실제_조건에서_두_발행_방식을_잰다()
    {
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: null);
        await broker.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // 🔑 실제 조건 — 구독자 둘(관제실 · 상황판)이 붙어 있다.
        //    구독자가 없으면 브로커가 라우팅할 일이 없어 비현실적으로 빨라진다.
        long receivedByA = 0;
        long receivedByB = 0;

        await using var subscriberA = new MqttIngestionSource("sub-a", "127.0.0.1", port, useTls: false);
        var runA = subscriberA.RunAsync(_ => { Interlocked.Increment(ref receivedByA); return Task.CompletedTask; }, cts.Token);

        await using var subscriberB = new MqttIngestionSource("sub-b", "127.0.0.1", port, useTls: false);
        var runB = subscriberB.RunAsync(_ => { Interlocked.Increment(ref receivedByB); return Task.CompletedTask; }, cts.Token);

        Assert.True(await MqttTestHelpers.WaitUntilAsync(
            () => subscriberA.IsConnected && subscriberB.IsConnected, MqttTestHelpers.Timeout));

        await using var publisher = new MqttSensorPublisher("throughput", "127.0.0.1", port, useTls: false);
        await publisher.ConnectAsync(cts.Token);
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => publisher.IsConnected, MqttTestHelpers.Timeout));

        // 예열 — 첫 발행에는 연결 협상 비용이 섞인다.
        await PublishSequentialAsync(publisher, 40, cts.Token);

        var sequential = Stopwatch.StartNew();
        await PublishSequentialAsync(publisher, Messages, cts.Token);
        sequential.Stop();

        var batched = Stopwatch.StartNew();
        await PublishBatchedAsync(publisher, Messages, cts.Token);
        batched.Stop();

        double sequentialRate = Messages / sequential.Elapsed.TotalSeconds;
        double batchedRate = Messages / batched.Elapsed.TotalSeconds;

        _output.WriteLine($"구독자 2개가 붙은 상태 · {Messages}건씩");
        _output.WriteLine($"  순차 : {sequential.ElapsedMilliseconds,6} ms → {sequentialRate,10:N0} msg/s");
        _output.WriteLine($"  묶음 : {batched.ElapsedMilliseconds,6} ms → {batchedRate,10:N0} msg/s  (in-flight {MaxInFlight})");
        _output.WriteLine($"  비율 : 묶음/순차 = {batchedRate / sequentialRate:F2}");

        // 🔒 어느 쪽이 이기는지는 단정하지 않는다 — 기계와 조건에 따라 달라진다.
        //    확인하는 것은 **둘 다 실제로 동작하고, 터무니없이 느리지 않다**는 것뿐이다.
        Assert.True(sequentialRate > 500, $"순차 발행이 비정상적으로 느리다 ({sequentialRate:N0} msg/s)");
        Assert.True(batchedRate > 500, $"묶음 발행이 비정상적으로 느리다 ({batchedRate:N0} msg/s)");

        await cts.CancelAsync();
        await runA;
        await runB;
    }

    [Fact]
    public async Task 발행_스레드는_호출자를_막지_않는다()
    {
        // 🔴 이게 결함의 본질이었다.
        //    발행이 UI 스레드에서 돌면 그동안 화면이 아무것도 못 한다.
        //
        //    여기서는 "발행이 도는 동안 다른 스레드가 계속 일할 수 있는가"를 본다.
        int port = MqttTestHelpers.FreePort();

        await using var broker = new EmbeddedMqttBroker(port, tlsCertificate: null);
        await broker.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using var publisher = new MqttSensorPublisher("nonblocking", "127.0.0.1", port, useTls: false);
        await publisher.ConnectAsync(cts.Token);
        Assert.True(await MqttTestHelpers.WaitUntilAsync(() => publisher.IsConnected, MqttTestHelpers.Timeout));

        long observerTicks = 0;
        using var observerStop = new CancellationTokenSource();

        // 화면 갱신을 흉내 내는 스레드 — 발행 중에도 계속 돌아야 한다.
        var observer = new Thread(() =>
        {
            while (!observerStop.IsCancellationRequested)
            {
                Interlocked.Increment(ref observerTicks);
                Thread.Sleep(10);
            }
        })
        { IsBackground = true };

        observer.Start();

        var publishing = Stopwatch.StartNew();
        await PublishBatchedAsync(publisher, Messages, cts.Token);
        publishing.Stop();

        observerStop.Cancel();
        observer.Join(TimeSpan.FromSeconds(2));

        long ticks = Interlocked.Read(ref observerTicks);
        double expected = publishing.Elapsed.TotalMilliseconds / 10.0;

        _output.WriteLine($"발행 {publishing.ElapsedMilliseconds} ms 동안 관측 스레드 {ticks} 틱 (기대 ~{expected:F0})");

        // 관측 스레드가 절반 이상 돌았으면 굶지 않은 것이다.
        Assert.True(ticks > expected * 0.5,
            $"발행이 다른 스레드를 굶기고 있다 (틱 {ticks}, 기대 ~{expected:F0})");
    }

    [Fact]
    public async Task 팜_엔진의_한_틱은_UI_없이도_돈다()
    {
        // 엔진은 MQTT 를 모르므로 브로커 없이도 계산할 수 있다.
        // 발행 계획과 실제 발행을 나눈 것이 이 검증을 가능하게 한다.
        var engine = new SensorFarmEngine(new SiteProvisioning(1_000));

        var sw = Stopwatch.StartNew();
        var readings = engine.Tick(T0, readingCount: 1_000);
        sw.Stop();

        Assert.Equal(1_000, readings.Count);

        // 계산 자체는 발행에 비하면 무시할 만해야 한다.
        Assert.True(sw.ElapsedMilliseconds < 200,
            $"1,000건 계산에 {sw.ElapsedMilliseconds}ms — 너무 느리다");

        await Task.CompletedTask;
    }

    // ── 두 가지 발행 방식 ────────────────────────────────────────────────

    /// <summary>하나 보내고 답을 기다린 뒤 다음 것 — 결함이 있던 방식.</summary>
    private static async Task PublishSequentialAsync(
        MqttSensorPublisher publisher, int count, CancellationToken ct)
    {
        for (int i = 0; i < count; i++)
        {
            var id = SiteProvisioning.SensorIdFor(i % 1_000);
            var vendor = SiteProvisioning.VendorFor(i % 1_000);
            var body = VendorPayloadFactory.Build(vendor, id, T0.AddSeconds(i), 1, 1);

            await publisher.PublishAsync(vendor, "g1-s0", id, body, ct).ConfigureAwait(false);
        }
    }

    /// <summary>여러 건을 동시에 띄워 두고 한꺼번에 기다린다 — 고친 방식.</summary>
    private static async Task PublishBatchedAsync(
        MqttSensorPublisher publisher, int count, CancellationToken ct)
    {
        var pending = new List<Task>(MaxInFlight);

        for (int i = 0; i < count; i++)
        {
            var id = SiteProvisioning.SensorIdFor(i % 1_000);
            var vendor = SiteProvisioning.VendorFor(i % 1_000);
            var body = VendorPayloadFactory.Build(vendor, id, T0.AddSeconds(i), 1, 1);

            pending.Add(publisher.PublishAsync(vendor, "g1-s0", id, body, ct));

            if (pending.Count < MaxInFlight) continue;

            await Task.WhenAll(pending).ConfigureAwait(false);
            pending.Clear();
        }

        if (pending.Count > 0) await Task.WhenAll(pending).ConfigureAwait(false);
    }
}
