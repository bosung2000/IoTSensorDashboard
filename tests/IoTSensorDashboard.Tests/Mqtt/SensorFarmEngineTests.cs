using System.Diagnostics;
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

        var window = TimeSpan.FromMilliseconds(50);
        var readings = engine.Tick(T0, readingCount: 5, window);

        Assert.Equal(5, readings.Count);
        Assert.All(readings, r =>
        {
            // 🔴 예전에는 여기서 `Assert.Equal(T0, r.At)` 로 **모든 관측이 같은 시각**임을
            //    고정하고 있었다. 그게 실제 결함의 원인이었다 —
            //    틱이 밀려 같은 센서가 두 번 나오면 같은 시각이라 멱등 키가 겹쳐
            //    수신 측이 전부 중복으로 접었다(「수신은 되는데 저장이 0」).
            //
            // 🔑 이제는 틱이 대표하는 **구간 안**에 흩어져야 한다.
            //    수백 건이 같은 밀리초에 일어날 수는 없다.
            Assert.True(r.At <= T0 && r.At >= T0 - window, $"구간 밖: {r.At}");

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

        // 🔑 한 틱에는 센서당 한 번만 관측된다(명부 한 바퀴가 물리적 상한).
        //    그래서 3건을 쌓으려면 **세 틱**이 필요하다.
        var readings = engine.Tick(T0, readingCount: 1);
        engine.Tick(T0.AddSeconds(1), readingCount: 1);
        engine.Tick(T0.AddSeconds(2), readingCount: 1);

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

        // 센서 1대는 한 틱에 한 번만 관측된다 → 2건을 쌓으려면 두 틱.
        engine.Tick(T0, 1);
        engine.Tick(T0.AddSeconds(1), 1);

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

    // ── 🔴 화면이 엔진을 훑는 비용 (실제 결함의 회귀 방지) ──────────────

    [Fact]
    public void 상태를_배열로_한_번에_복사한다()
    {
        var engine = NewEngine(8);
        engine.SetOfflineAt(1, true);
        engine.SetOfflineAt(5, true);

        var states = new bool[8];
        engine.CopyOnlineStates(states);

        Assert.True(states[0]);
        Assert.False(states[1]);
        Assert.True(states[4]);
        Assert.False(states[5]);
    }

    [Fact]
    public void 복사본은_이후_변경에_영향받지_않는다()
    {
        // 화면은 프레임 시작에 찍은 스냅샷으로 한 프레임을 그린다.
        // 그리는 도중에 값이 바뀌면 한 화면 안에서 서로 안 맞는 상태가 그려진다.
        var engine = NewEngine(4);

        var states = new bool[4];
        engine.CopyOnlineStates(states);

        engine.SetOfflineAt(0, true);   // 복사 뒤에 바꾼다

        Assert.True(states[0]);          // 스냅샷은 그대로
        Assert.False(engine.IsOnlineAt(0));
    }

    [Fact]
    public void 짧은_배열을_줘도_터지지_않는다()
    {
        // 창 크기가 바뀌는 도중 배열이 아직 안 늘어난 순간이 있을 수 있다.
        var engine = NewEngine(10);
        var small = new bool[3];

        var exception = Record.Exception(() => engine.CopyOnlineStates(small));

        Assert.Null(exception);
        Assert.All(small, Assert.True);
    }

    [Fact]
    public void 인덱스로_묻는_것이_ID로_묻는_것과_같은_답을_준다()
    {
        var engine = NewEngine(12);
        engine.SetOfflineAt(3, true);

        for (int i = 0; i < 12; i++)
        {
            var id = SiteProvisioning.SensorIdFor(i);
            Assert.Equal(engine.IsOnline(id), engine.IsOnlineAt(i));
        }
    }

    [Fact]
    public void 전체_복사가_하나씩_묻는_것보다_훨씬_싸다()
    {
        // 🔴 이것이 결함의 핵심이었다.
        //
        //    화면이 타일마다 IsOnline(문자열) 을 불렀고, 그 안에서
        //      · 선형 탐색으로 인덱스를 다시 찾고
        //      · 락을 잡았다.
        //    타일 1,000개 × 30fps 면 초당 최대 3,000만 회 문자열 비교 + 락 3만 회다.
        //
        //    CPU 낭비보다 **락 경합**이 더 치명적이었다 —
        //    같은 락을 쓰는 발행 스레드가 함께 느려졌다.
        var engine = NewEngine(1_000);
        var states = new bool[1_000];

        // 예열
        for (int i = 0; i < 1_000; i++) engine.IsOnline(SiteProvisioning.SensorIdFor(i));
        engine.CopyOnlineStates(states);

        var oneByOne = Stopwatch.StartNew();
        for (int frame = 0; frame < 30; frame++)
            for (int i = 0; i < 1_000; i++)
                engine.IsOnline(SiteProvisioning.SensorIdFor(i));   // 옛 방식
        oneByOne.Stop();

        var bulk = Stopwatch.StartNew();
        for (int frame = 0; frame < 30; frame++)
            engine.CopyOnlineStates(states);                        // 지금 방식
        bulk.Stop();

        // 30프레임(1초 분량) 기준.
        Assert.True(bulk.Elapsed < oneByOne.Elapsed,
            $"전체 복사가 더 싸야 한다 (하나씩 {oneByOne.ElapsedMilliseconds}ms / 복사 {bulk.ElapsedMilliseconds}ms)");

        // 🔒 화면 한 프레임이 이 정도면 30fps 예산(33ms)에 아무 영향이 없다.
        Assert.True(bulk.ElapsedMilliseconds < 30,
            $"30프레임 분량 복사에 {bulk.ElapsedMilliseconds}ms — 너무 느리다");
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
