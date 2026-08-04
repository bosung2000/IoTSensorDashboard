using IoTSensorDashboard.Core.Health;
using Xunit;

namespace IoTSensorDashboard.Tests.Health;

/// <summary>
/// 적응 임계 — 침묵의 뜻은 센서마다 다르다.
///
/// 📌 근거 — 고정 임계가 무너진 실측:
///    임계가 12초 고정이었다. 1분마다 묶어 보내는 센서에게 12초 침묵은 완전히 정상인데
///    계속 오프라인으로 잡혔고, 핑 대상이 <b>5대 → 322~742대</b>로 폭증했다.
///
///    「데이터가 곧 생존 증거」라는 설계가, 데이터가 드물어지는 순간
///    <b>상시 최대 비용</b>으로 뒤집힌 것이다.
/// </summary>
public sealed class AdaptiveThresholdTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    // ── 정책 계산 ────────────────────────────────────────────────────────

    [Fact]
    public void 주기를_모르면_바닥값을_쓴다()
    {
        Assert.Equal(HealthPolicy.Offline.Floor, HealthPolicy.Offline.For(null));
        Assert.Equal(HealthPolicy.Offline.Floor, HealthPolicy.Offline.For(TimeSpan.Zero));
        Assert.Equal(HealthPolicy.Offline.Floor, HealthPolicy.Offline.For(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void 짧은_주기는_바닥값으로_올려_준다()
    {
        // 1초마다 쏘는 센서라도 2.5초 만에 죽었다고 하면 오탐이 폭주한다.
        var threshold = HealthPolicy.Offline.For(TimeSpan.FromSeconds(1));

        Assert.Equal(HealthPolicy.Offline.Floor, threshold);
    }

    [Fact]
    public void 긴_주기에는_그만큼_넉넉히_기다린다()
    {
        // 1분마다 묶어 보내는 센서 → 12초가 아니라 2.5분을 기다린다.
        var threshold = HealthPolicy.Offline.For(TimeSpan.FromSeconds(60));

        Assert.Equal(TimeSpan.FromSeconds(150), threshold);   // 60 × 2.5
    }

    [Fact]
    public void 천장이_반드시_있어야_한다()
    {
        // 🔑 주기를 **도착에서 배우므로**, 점점 느려지는(죽어가는) 센서는
        //    「나는 원래 이만큼 조용하다」고 **스스로를 가르칠 수 있다.**
        //    천장이 없으면 센서가 자기 자신에게 면죄부를 준다.
        var threshold = HealthPolicy.Offline.For(TimeSpan.FromMinutes(10));

        Assert.Equal(HealthPolicy.Offline.Ceiling, threshold);
        Assert.Equal(TimeSpan.FromMinutes(3), threshold);

        // 1분 배치 센서가 죽어도 3분 안에는 반드시 드러난다.
    }

    [Fact]
    public void 핑_정책이_오프라인_정책보다_짧다()
    {
        // 오프라인으로 판정하기 전에 먼저 물어보려고.
        Assert.True(HealthPolicy.Probe.Floor < HealthPolicy.Offline.Floor);
        Assert.True(HealthPolicy.Probe.Ceiling < HealthPolicy.Offline.Ceiling);
        Assert.True(HealthPolicy.Probe.Multiplier < HealthPolicy.Offline.Multiplier);
    }

    [Fact]
    public void 정책_수치가_명세와_일치한다()
    {
        Assert.Equal(TimeSpan.FromSeconds(12), HealthPolicy.Offline.Floor);
        Assert.Equal(2.5, HealthPolicy.Offline.Multiplier);
        Assert.Equal(TimeSpan.FromMinutes(3), HealthPolicy.Offline.Ceiling);

        Assert.Equal(TimeSpan.FromSeconds(6), HealthPolicy.Probe.Floor);
        Assert.Equal(1.5, HealthPolicy.Probe.Multiplier);
        Assert.Equal(TimeSpan.FromMinutes(2), HealthPolicy.Probe.Ceiling);
    }

    // ── 주기 학습 ────────────────────────────────────────────────────────

    [Fact]
    public void 도착_간격에서_주기를_배운다()
    {
        var tracker = new SensorHealthTracker();

        for (int i = 0; i < 10; i++)
            tracker.Observe("flir-0001", T0.AddSeconds(i * 60));   // 1분 간격

        var cadence = tracker.Cadence("flir-0001");

        Assert.NotNull(cadence);
        Assert.InRange(cadence!.Value.TotalSeconds, 50, 61);
    }

    [Fact]
    public void 배치_센서는_오프라인으로_잡히지_않는다()
    {
        // 🔴 이게 적응 임계가 존재하는 이유다.
        //    고정 12초였다면 이 센서는 매 주기마다 오프라인으로 잡힌다.
        var tracker = new SensorHealthTracker();

        // 1분마다 보내는 센서
        for (int i = 0; i < 5; i++)
            tracker.Observe("batch-0001", T0.AddSeconds(i * 60));

        var lastSent = T0.AddSeconds(4 * 60);

        // 30초 침묵 — 이 센서에게는 완전히 정상이다.
        Assert.Equal(SensorStatus.Online,
            tracker.Status("batch-0001", lastSent.AddSeconds(30), HealthPolicy.Offline));

        // 그래도 천장(3분)을 넘으면 드러난다.
        Assert.Equal(SensorStatus.Offline,
            tracker.Status("batch-0001", lastSent.AddMinutes(4), HealthPolicy.Offline));
    }

    [Fact]
    public void 침묵에서는_배우지_않는다()
    {
        // 🔒 배우면 죽어가는 센서가 **자기 죽음을 정상으로** 만든다.
        //    도착에서만 배우면 죽은 센서의 주기 추정치는 죽기 직전 값에 멈춘다.
        var tracker = new SensorHealthTracker();

        tracker.Observe("flir-0001", T0);
        tracker.Observe("flir-0001", T0.AddSeconds(2));

        var learned = tracker.Cadence("flir-0001");

        // 아무 도착 없이 시간만 흐른다 — Status 를 여러 번 물어봐도 주기는 안 변한다.
        for (int i = 0; i < 100; i++)
            tracker.Status("flir-0001", T0.AddMinutes(i), HealthPolicy.Offline);

        Assert.Equal(learned, tracker.Cadence("flir-0001"));
    }

    [Fact]
    public void 공백이_300초를_넘으면_주기_표본이_아니다()
    {
        // 📌 한 번의 긴 공백(재연결·백필)이 추정치를 통째로 밀어 올리지 않게.
        //    그렇게 되면 그 센서는 그 뒤로 아주 오래 조용해도 정상으로 보인다.
        var tracker = new SensorHealthTracker();

        tracker.Observe("flir-0001", T0);
        tracker.Observe("flir-0001", T0.AddSeconds(2));
        var beforeGap = tracker.Cadence("flir-0001");

        tracker.Observe("flir-0001", T0.AddHours(1));   // 1시간 공백 — 사고다

        Assert.Equal(beforeGap, tracker.Cadence("flir-0001"));
    }

    [Fact]
    public void 한_번_튄_값에_크게_흔들리지_않는다()
    {
        // 지수평활 α=0.3 — 빠르게 따라가되 튀지 않게.
        var tracker = new SensorHealthTracker();

        for (int i = 0; i < 20; i++)
            tracker.Observe("flir-0001", T0.AddSeconds(i * 2));   // 2초 주기로 안정

        var before = tracker.Cadence("flir-0001")!.Value.TotalSeconds;

        // 한 번 20초 벌어진다.
        tracker.Observe("flir-0001", T0.AddSeconds(20 * 2 + 20));
        var after = tracker.Cadence("flir-0001")!.Value.TotalSeconds;

        Assert.True(after > before, "새 값을 반영은 해야 한다");
        Assert.True(after < 10, $"한 번 튄 값에 통째로 끌려가면 안 된다 (실제 {after:F1}초)");
    }

    [Fact]
    public void 주기를_밖에서_읽을_수_있다()
    {
        // 🔑 임계가 센서마다 다르므로, 진단 화면이 **관측된 주기**를 보여줄 수 있어야 한다.
        //    근거를 못 보여주면 판정을 믿을 수 없다.
        var tracker = new SensorHealthTracker();

        Assert.Null(tracker.Cadence("flir-0001"));   // 아직 모른다

        tracker.Observe("flir-0001", T0);
        tracker.Observe("flir-0001", T0.AddSeconds(3));

        Assert.NotNull(tracker.Cadence("flir-0001"));
    }

    // ── 스레드 안전 ──────────────────────────────────────────────────────

    [Fact]
    public void 동시_관측에서도_깨지지_않는다()
    {
        // 수집 콜백이 동시에 관측한다. Dictionary 는 읽기 중 쓰기가 일어나면 깨진다.
        var tracker = new SensorHealthTracker();
        tracker.Expect(Enumerable.Range(0, 100).Select(i => $"s-{i:D3}"));

        Parallel.For(0, 100, i =>
        {
            var id = $"s-{i:D3}";
            for (int k = 0; k < 20; k++)
            {
                tracker.Observe(id, T0.AddSeconds(k));
                tracker.Status(id, T0.AddSeconds(k), HealthPolicy.Offline);
                tracker.Cadence(id);
            }
        });

        Assert.Equal(100, tracker.Summary(T0.AddSeconds(19), HealthPolicy.Offline).Total);
    }
}
