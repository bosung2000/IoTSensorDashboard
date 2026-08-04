using IoTSensorDashboard.Core.Provisioning;
using IoTSensorDashboard.Core.Reporting;
using IoTSensorDashboard.Core.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.Reporting;

/// <summary>
/// SLA — 「측정 불가」를 100% 로 쓰지 않는다.
///
/// 🔴 이 화면은 스스로를 「구매 판단 근거」라고 부른다.
///    그런데 이 시스템에서 <b>가장 크게 거짓말한 숫자</b>가 바로 여기서 나왔다.
/// </summary>
public sealed class SlaCalculatorTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = WindowStart.AddHours(24);

    private static OutageRecord Outage(string store, int startHour, int durationMinutes) =>
        new("flir-0001", store,
            WindowStart.AddHours(startHour),
            WindowStart.AddHours(startHour).AddMinutes(durationMinutes));

    // ── 🔴 미관측 ≠ 정상 ─────────────────────────────────────────────────

    [Fact]
    public void 관측하지_못한_매장은_100퍼센트가_아니라_측정_불가다()
    {
        // 🔴 이게 이 파일에서 가장 중요한 테스트다.
        //
        // 📌 종전에는 **장애 이력이 없으면 무조건 가동률 100%** 였다.
        //    그래서 관제실이 꺼져 있었거나 센서가 처음부터 죽어 있던 매장도 100.000% 로 나왔다.
        //
        // > **「장애가 없었다」와 「지켜보지 못했다」는 다른 사실이다.**
        var stats = SlaCalculator.Compute(
            outages: [],
            storeNames: ["강남점", "잠실점"],
            WindowStart, Now,
            observedStores: new HashSet<string> { "강남점" });

        var gangnam = stats.Single(s => s.Store == "강남점");
        var jamsil = stats.Single(s => s.Store == "잠실점");

        Assert.Equal(1.0, gangnam.Uptime);      // 지켜봤고 장애 없음 → 100% 는 사실이다
        Assert.Null(jamsil.Uptime);             // ✅ 지켜보지 못함 → 측정 불가
    }

    [Fact]
    public void 측정_불가_매장은_목록에서_사라지지_않는다()
    {
        // 🔑 0 으로 표시되는 것보다 사라지는 게 나쁘다 —
        //    0 은 「손님이 없었구나」지만, 사라지면 「그런 매장이 없구나」가 된다.
        var stats = SlaCalculator.Compute(
            [], ["강남점", "잠실점", "해운대점"], WindowStart, Now,
            new HashSet<string>());

        Assert.Equal(3, stats.Count);
        Assert.All(stats, s => Assert.Null(s.Uptime));
    }

    [Fact]
    public void 측정_불가는_평균에서_제외된다()
    {
        // ❌ null 을 0 으로 보면 평균이 실제보다 낮게 나온다
        // ❌ null 을 1 로 보면 실제보다 높게 나온다  ← 이게 원래 버그였다
        // ✅ 아예 빼고 계산한다
        var stats = SlaCalculator.Compute(
            [Outage("강남점", 0, 60)],                       // 1시간 다운 → 약 95.8%
            ["강남점", "잠실점"],
            WindowStart, Now,
            new HashSet<string> { "강남점" });

        var summary = SlaCalculator.Summarize(stats);

        Assert.Equal(1, summary.MeasuredCount);
        Assert.Equal(1, summary.UnmeasurableCount);

        // 측정 가능한 한 곳의 값 그대로여야 한다.
        Assert.NotNull(summary.AverageUptime);
        Assert.Equal(stats.Single(s => s.Store == "강남점").Uptime!.Value,
                     summary.AverageUptime!.Value, precision: 10);
    }

    [Fact]
    public void 측정_불가_곳수를_함께_돌려준다()
    {
        // 🔑 평균만 보여주면 몇 곳이 빠졌는지 알 수 없고, 그러면 평균이 다시 거짓말한다.
        var stats = SlaCalculator.Compute(
            [], ["a", "b", "c", "d"], WindowStart, Now,
            new HashSet<string> { "a", "b" });

        var summary = SlaCalculator.Summarize(stats);

        Assert.Equal(2, summary.UnmeasurableCount);
        Assert.Equal(2, summary.MeasuredCount);
    }

    [Fact]
    public void 전부_측정_불가면_평균도_null_이다()
    {
        // 0% 나 100% 를 지어내지 않는다.
        var stats = SlaCalculator.Compute([], ["a", "b"], WindowStart, Now, new HashSet<string>());

        Assert.Null(SlaCalculator.Summarize(stats).AverageUptime);
    }

    [Fact]
    public void 관측_집합에_기본값이_없다()
    {
        // 🔴 기본값이 있으면 호출부가 무심코 옛 동작으로 되돌아가 버그가 조용히 재발한다.
        //    필수 인자로 두면 컴파일러가 막는다.
        //
        //    이 테스트는 그 사실을 문서화한다 — 인자를 빼면 컴파일이 안 된다.
        var method = typeof(SlaCalculator).GetMethod(nameof(SlaCalculator.Compute))!;
        var parameter = method.GetParameters().Single(p => p.Name == "observedStores");

        Assert.False(parameter.HasDefaultValue,
            "observedStores 에 기본값이 생기면 미관측 매장이 다시 100% 로 둔갑한다");
    }

    // ── 다운타임 계산 ────────────────────────────────────────────────────

    [Fact]
    public void 가동률은_다운타임_비율로_계산된다()
    {
        // 24시간 창에서 1시간 다운 → 23/24
        var stats = SlaCalculator.Compute(
            [Outage("강남점", 0, 60)], ["강남점"], WindowStart, Now,
            new HashSet<string> { "강남점" });

        var stat = Assert.Single(stats);

        Assert.Equal(23.0 / 24.0, stat.Uptime!.Value, precision: 6);
        Assert.Equal(3600, stat.DowntimeSec);
        Assert.Equal(1, stat.Incidents);
        Assert.Equal(3600, stat.MttrSec);
    }

    [Fact]
    public void 동시_다운은_이중_계산되지_않는다()
    {
        // 📌 한 매장에 센서가 여러 대인데 **동시에** 다운되면,
        //    단순 합산 시 같은 시간을 여러 번 센다 → 가동률이 실제보다 훨씬 낮게 나온다.
        var overlapping = new List<OutageRecord>
        {
            new("s-1", "강남점", WindowStart, WindowStart.AddHours(2)),
            new("s-2", "강남점", WindowStart.AddHours(1), WindowStart.AddHours(3)),  // 1시간 겹침
            new("s-3", "강남점", WindowStart.AddMinutes(30), WindowStart.AddHours(1)), // 완전 포함
        };

        var stat = Assert.Single(SlaCalculator.Compute(
            overlapping, ["강남점"], WindowStart, Now, new HashSet<string> { "강남점" }));

        // 겹침을 병합하면 0시 ~ 3시 = 3시간이다. 단순 합산이면 5.5시간이 된다.
        Assert.Equal(3 * 3600, stat.DowntimeSec);
        Assert.Equal(3, stat.Incidents);      // 장애 건수는 그대로 3건이다
    }

    [Fact]
    public void 떨어져_있는_구간은_각각_더해진다()
    {
        var separate = new List<OutageRecord>
        {
            Outage("강남점", 0, 60),
            Outage("강남점", 5, 60),
        };

        var stat = Assert.Single(SlaCalculator.Compute(
            separate, ["강남점"], WindowStart, Now, new HashSet<string> { "강남점" }));

        Assert.Equal(2 * 3600, stat.DowntimeSec);
    }

    // ── 창 경계 ──────────────────────────────────────────────────────────

    [Fact]
    public void 창_이전에_시작한_장애는_창_시작부터_센다()
    {
        var before = new OutageRecord("s-1", "강남점",
            WindowStart.AddHours(-5), WindowStart.AddHours(1));

        var stat = Assert.Single(SlaCalculator.Compute(
            [before], ["강남점"], WindowStart, Now, new HashSet<string> { "강남점" }));

        Assert.Equal(3600, stat.DowntimeSec);   // 창 안의 1시간만
    }

    [Fact]
    public void 아직_안_끝난_장애는_현재까지_센다()
    {
        var ongoing = new OutageRecord("s-1", "강남점",
            Now.AddHours(-2), Now.AddHours(10));   // 미래에 끝날 예정

        var stat = Assert.Single(SlaCalculator.Compute(
            [ongoing], ["강남점"], WindowStart, Now, new HashSet<string> { "강남점" }));

        Assert.Equal(2 * 3600, stat.DowntimeSec);
    }

    [Fact]
    public void 창_이전에_끝난_장애는_세지_않는다()
    {
        var old = new OutageRecord("s-1", "강남점",
            WindowStart.AddHours(-10), WindowStart.AddHours(-5));

        var stat = Assert.Single(SlaCalculator.Compute(
            [old], ["강남점"], WindowStart, Now, new HashSet<string> { "강남점" }));

        Assert.Equal(0, stat.DowntimeSec);
        Assert.Equal(1.0, stat.Uptime);
    }

    [Fact]
    public void 가동률은_0과_1_사이로_묶인다()
    {
        // 창 밖 시간을 세면 음수가 될 수 있다. Clamp 가 최종 방어다.
        var huge = new OutageRecord("s-1", "강남점",
            WindowStart.AddDays(-10), Now.AddDays(10));

        var stat = Assert.Single(SlaCalculator.Compute(
            [huge], ["강남점"], WindowStart, Now, new HashSet<string> { "강남점" }));

        Assert.InRange(stat.Uptime!.Value, 0, 1);
    }

    // ── 관측 판정 ────────────────────────────────────────────────────────

    [Fact]
    public void 활동한_센서에서_관측된_매장을_뽑는다()
    {
        var prov = new SiteProvisioning(sensorCount: 24);

        var active = new[] { SiteProvisioning.SensorIdFor(0), SiteProvisioning.SensorIdFor(1) };
        var observed = SlaObservation.FromActiveSensors(active, prov);

        Assert.Equal(2, observed.Count);
        Assert.Contains(prov.SiteName(prov.StoreIds[0]), observed);
        Assert.Contains(prov.SiteName(prov.StoreIds[1]), observed);
    }

    [Fact]
    public void 명부에_없는_센서는_관측_근거가_못_된다()
    {
        // 🔒 어느 매장 것인지 말할 수 없으므로 근거가 될 수 없다.
        //    여기서 잘못 넣으면 지켜보지도 않은 매장이 다시 「가동률 100%」로 둔갑한다.
        var prov = new SiteProvisioning(sensorCount: 24);

        Assert.Empty(SlaObservation.FromActiveSensors(["axis-9999"], prov));
    }

    [Fact]
    public void 활동한_센서가_없으면_전부_측정_불가다()
    {
        var prov = new SiteProvisioning(sensorCount: 24);
        var observed = SlaObservation.FromActiveSensors([], prov);

        var stats = SlaCalculator.Compute([], prov.StoreNames, WindowStart, Now, observed);

        Assert.All(stats, s => Assert.Null(s.Uptime));
        Assert.Equal(12, SlaCalculator.Summarize(stats).UnmeasurableCount);
    }
}
