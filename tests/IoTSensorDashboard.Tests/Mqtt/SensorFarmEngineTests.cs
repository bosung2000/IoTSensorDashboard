using IoTSensorDashboard.Core.Provisioning;
using IoTSensorDashboard.Mqtt;
using Xunit;

namespace IoTSensorDashboard.Tests.Mqtt;

/// <summary>
/// 센서 팜 엔진 — 악조건을 <b>의도적으로</b> 만드는 장치.
///
/// 이 앱이 인수 검증의 도구다. 악조건을 만들 수 없으면 신뢰성을 증명할 수 없다.
/// 엔진은 MQTT 를 모르므로 브로커 없이 검증한다.
/// </summary>
public sealed class SensorFarmEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    private static SensorFarmEngine NewEngine(int sensorCount = 12) =>
        new(new SiteProvisioning(sensorCount));

    // ── 기본 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 처음에는_전부_온라인이다()
    {
        var engine = NewEngine();

        Assert.Equal(12, engine.SensorCount);
        Assert.Equal(12, engine.OnlineCount);
        Assert.Equal(0, engine.BufferedCount);
        Assert.Equal(0, engine.DroppedByBufferCap);
    }

    [Fact]
    public void 관측한_것을_발행_목록으로_돌려준다()
    {
        var engine = NewEngine();

        var readings = engine.Tick(T0, readingCount: 5);

        Assert.Equal(5, readings.Count);
        Assert.All(readings, r =>
        {
            Assert.Equal(T0, r.At);
            Assert.False(string.IsNullOrEmpty(r.SensorId));
            Assert.False(string.IsNullOrEmpty(r.SiteId));
        });
    }

    [Fact]
    public void 센서를_고루_돈다()
    {
        // 한 센서만 계속 발행하면 나머지가 전부 오프라인으로 잡힌다.
        var engine = NewEngine(12);

        var readings = engine.Tick(T0, readingCount: 12);

        Assert.Equal(12, readings.Select(r => r.SensorId).Distinct().Count());
    }

    // ── 오프라인 · 버퍼 ──────────────────────────────────────────────────

    [Fact]
    public void 오프라인_센서는_발행되지_않고_버퍼에_쌓인다()
    {
        var engine = NewEngine(1);
        var sensorId = SiteProvisioning.SensorIdFor(0);

        engine.SetOffline(sensorId, true);
        var readings = engine.Tick(T0, readingCount: 3);

        Assert.Empty(readings);              // 침묵한다
        Assert.Equal(3, engine.BufferedCount);
        Assert.Equal(0, engine.OnlineCount);
    }

    [Fact]
    public void 복구하면_원본_시각_그대로_백필된다()
    {
        // 🔑 지금 시각으로 바꾸면 "10분 전 손님"이 "방금 온 손님"이 되어
        //    시간대별 통계가 통째로 어긋난다.
        var engine = NewEngine(1);
        var sensorId = SiteProvisioning.SensorIdFor(0);

        engine.SetOffline(sensorId, true);
        engine.Tick(T0, 1);
        engine.Tick(T0.AddSeconds(10), 1);
        engine.Tick(T0.AddSeconds(20), 1);

        engine.SetOffline(sensorId, false);
        var backfill = engine.DrainBackfill(sensorId);

        Assert.Equal(3, backfill.Count);
        Assert.Equal(T0, backfill[0].At);
        Assert.Equal(T0.AddSeconds(10), backfill[1].At);
        Assert.Equal(T0.AddSeconds(20), backfill[2].At);
        Assert.Equal(0, engine.BufferedCount);
    }

    [Fact]
    public void 백필은_한_번만_나온다()
    {
        // 두 번 비우면 같은 것을 두 번 보내게 된다.
        // (수신 측 멱등이 접긴 하지만, 보내는 쪽이 먼저 정확해야 한다.)
        var engine = NewEngine(1);
        var sensorId = SiteProvisioning.SensorIdFor(0);

        engine.SetOffline(sensorId, true);
        engine.Tick(T0, 2);
        engine.SetOffline(sensorId, false);

        Assert.Equal(2, engine.DrainBackfill(sensorId).Count);
        Assert.Empty(engine.DrainBackfill(sensorId));
    }

    // ── 🔴 능력치 경계 — 유실을 감추지 않는다 ────────────────────────────

    [Fact]
    public void 버퍼_상한까지는_무손실이다()
    {
        var engine = NewEngine(1);
        var sensorId = SiteProvisioning.SensorIdFor(0);

        engine.SetOffline(sensorId, true);
        for (int i = 0; i < SensorFarmEngine.BufferCap; i++)
            engine.Tick(T0.AddSeconds(i), 1);

        Assert.Equal(SensorFarmEngine.BufferCap, engine.BufferedCount);
        Assert.Equal(0, engine.DroppedByBufferCap);   // 아직 하나도 안 버렸다
    }

    [Fact]
    public void 상한을_넘으면_오래된_것부터_버리고_반드시_센다()
    {
        // 🔴 이게 이 클래스에서 가장 중요한 테스트다.
        //
        //    화면이 「유실 0」을 하드코딩된 문자열로 띄우고 있어서
        //    실제로 폐기가 일어나는데도 무손실이라고 말한 적이 있다.
        //
        //    무한 버퍼는 불가능하므로 폐기 자체는 설계상 한계이지 결함이 아니다.
        //    결함이었던 것은 한계가 아니라 **한계를 감춘 것**이다.
        var engine = NewEngine(1);
        var sensorId = SiteProvisioning.SensorIdFor(0);

        engine.SetOffline(sensorId, true);

        const int Overflow = 50;
        for (int i = 0; i < SensorFarmEngine.BufferCap + Overflow; i++)
            engine.Tick(T0.AddSeconds(i), 1);

        Assert.Equal(SensorFarmEngine.BufferCap, engine.BufferedCount);   // 버퍼는 상한을 안 넘고
        Assert.Equal(Overflow, engine.DroppedByBufferCap);               // 넘친 만큼 세어졌다
    }

    [Fact]
    public void 버린_것은_오래된_쪽이다()
    {
        // 최신 데이터를 살린다 — 오래된 것일수록 이미 다른 경로로 반영됐을 가능성이 높고,
        // 복구 직후 화면이 보여줘야 하는 것은 "지금에 가까운" 값이다.
        var engine = NewEngine(1);
        var sensorId = SiteProvisioning.SensorIdFor(0);

        engine.SetOffline(sensorId, true);
        for (int i = 0; i < SensorFarmEngine.BufferCap + 10; i++)
            engine.Tick(T0.AddSeconds(i), 1);

        engine.SetOffline(sensorId, false);
        var backfill = engine.DrainBackfill(sensorId);

        Assert.Equal(T0.AddSeconds(10), backfill[0].At);                                  // 앞의 10건이 사라졌다
        Assert.Equal(T0.AddSeconds(SensorFarmEngine.BufferCap + 9), backfill[^1].At);     // 최신은 남았다
    }

    [Fact]
    public void 버퍼_용량을_밖에서_읽을_수_있다()
    {
        // 화면이 "약 10분치"를 스스로 계산할 수 있어야 한다 —
        // 그 숫자를 화면에 하드코딩하면 상수가 바뀔 때 조용히 거짓말이 된다.
        Assert.Equal(600, SensorFarmEngine.BufferCapacity);
    }

    // ── 생존 응답 ────────────────────────────────────────────────────────

    [Fact]
    public void 죽은_센서는_ACK_를_보내지_않는다()
    {
        // 📌 죽은 센서가 응답하면 관제실이 그 센서를 영원히 못 찾는다.
        var engine = NewEngine(4);
        var dead = SiteProvisioning.SensorIdFor(1);

        engine.SetOffline(dead, true);
        var targets = engine.AckTargets("*");

        Assert.Equal(3, targets.Count);
        Assert.DoesNotContain(dead, targets);
    }

    [Fact]
    public void 지정된_센서에게만_묻는_핑도_처리한다()
    {
        // 📌 1,000대에게 2.5초마다 물으면 그 트래픽이 실제 데이터를 압도한다.
        //    데이터가 오는 센서는 그 데이터가 생존 증거이므로, 모르는 것만 묻는다.
        var engine = NewEngine(6);
        var asked = $"{SiteProvisioning.SensorIdFor(0)}\n{SiteProvisioning.SensorIdFor(2)}";

        var targets = engine.AckTargets(asked);

        Assert.Equal(2, targets.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    public void 빈_핑_본문은_전체를_뜻한다(string? body)
    {
        Assert.Equal(6, NewEngine(6).AckTargets(body).Count);
    }

    [Fact]
    public void 모르는_센서_ID_를_물으면_아무도_응답하지_않는다()
    {
        Assert.Empty(NewEngine(4).AckTargets("axis-9999"));
    }

    // ── 이상치 주입 ──────────────────────────────────────────────────────

    [Fact]
    public void 이상치는_물리_정합_한계를_넘는다()
    {
        // 🔑 화면만 바꾸는 게 아니라 진짜로 발행해야 관제실이 격리하는지 증명된다.
        var anomaly = NewEngine().CreateAnomaly(T0);

        Assert.True(anomaly.In > Core.Ingestion.IngestionPipeline.DefaultMaxPlausibleCountPerReading);
        Assert.False(string.IsNullOrEmpty(anomaly.SensorId));
    }

    [Fact]
    public void 정상_관측은_정합_한계를_넘지_않는다()
    {
        // 평상시 값이 한계를 넘으면 격리 카운터가 항상 올라가 경고가 의미를 잃는다.
        var engine = NewEngine(20);
        var readings = engine.Tick(T0, 100);

        Assert.All(readings, r =>
        {
            Assert.InRange(r.In, 0, SensorFarmEngine.MaxPeoplePerSecondPerSensor);
            Assert.InRange(r.Out, 0, SensorFarmEngine.MaxPeoplePerSecondPerSensor);
        });
    }

    // ── 결정성 ───────────────────────────────────────────────────────────

    [Fact]
    public void 같은_시드면_같은_결과가_나온다()
    {
        // 검증이 성립하려면 같은 조건에서 같은 결과가 나와야 한다.
        var a = new SensorFarmEngine(new SiteProvisioning(8), randomSeed: 42).Tick(T0, 20);
        var b = new SensorFarmEngine(new SiteProvisioning(8), randomSeed: 42).Tick(T0, 20);

        Assert.Equal(a.Select(r => (r.SensorId, r.In, r.Out)),
                     b.Select(r => (r.SensorId, r.In, r.Out)));
    }
}
