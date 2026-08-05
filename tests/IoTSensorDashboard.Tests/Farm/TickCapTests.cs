using IoTSensorDashboard.Core.Provisioning;
using IoTSensorDashboard.Mqtt;
using Xunit;

namespace IoTSensorDashboard.Tests.Farm;

/// <summary>
/// 한 틱에 <b>같은 센서를 두 번 세지 않는다</b>.
///
/// 🔴 <b>실측 결함</b>: 부하가 크거나 디버거가 붙어 틱이 밀리면 요청 건수가
///    센서 수를 넘는다(속도 × 밀린 시간). 그러면 커서가 명부를 한 바퀴 넘게 돌아
///    <b>같은 센서가 같은 시각으로</b> 다시 발행됐다.
///
///    수신 측 멱등 키는 <c>SensorId|밀리초|Direction</c> 이라 그건 같은 이벤트다.
///    → I1 이 전부 중복으로 접었고, 화면에는 <b>「수신은 되는데 저장이 0」</b> 으로 나타났다.
///    백로그도 안 쌓였다(버려진 게 아니라 접힌 것) — 그래서 진단이 어려웠다.
///
/// 📌 실측(20,000/s): 수정 전 5초 만에 중복 22,426건 → 수정 후 <b>0건</b>.
///    저장 처리량도 25% 올랐다(중복 판정에 쓰던 비용이 사라져서).
/// </summary>
public sealed class TickCapTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 10, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void 요청이_센서_수를_넘어도_한_바퀴만_돈다()
    {
        var engine = new SensorFarmEngine(new SiteProvisioning(100));

        // 명부의 10배를 요청한다 — 틱이 크게 밀린 상황.
        var readings = engine.Tick(Now, readingCount: 1_000, window: TimeSpan.FromMilliseconds(500));

        Assert.True(readings.Count <= 100,
            $"{readings.Count}건이 나왔다. 명부(100대) 한 바퀴가 물리적 상한이다.");
    }

    /// <summary>
    /// 🔑 핵심 불변식: 한 틱의 결과에 <b>같은 센서가 두 번 나오지 않는다</b>.
    ///    이게 깨지면 그 순간 수신 측에서 중복으로 접힌다.
    /// </summary>
    [Fact]
    public void 한_틱에_같은_센서가_두_번_나오지_않는다()
    {
        var engine = new SensorFarmEngine(new SiteProvisioning(100));

        var readings = engine.Tick(Now, readingCount: 5_000, window: TimeSpan.FromSeconds(1));

        var distinct = readings.Select(r => r.SensorId).Distinct().Count();
        Assert.Equal(readings.Count, distinct);
    }

    /// <summary>
    /// 관측 시각을 구간에 흩는다 — 수백 건이 <b>같은 밀리초</b>에 일어날 수는 없다.
    /// (멱등 키가 밀리초 해상도라, 이게 뭉치면 중복 위험이 커진다.)
    /// </summary>
    [Fact]
    public void 관측_시각을_구간에_흩는다()
    {
        var engine = new SensorFarmEngine(new SiteProvisioning(100));

        var readings = engine.Tick(Now, readingCount: 100, window: TimeSpan.FromMilliseconds(100));

        var distinctMillis = readings.Select(r => r.At.ToUnixTimeMilliseconds()).Distinct().Count();

        Assert.True(distinctMillis > 1,
            "모든 관측이 같은 밀리초에 찍혔다 — 구간에 흩어지지 않았다.");

        // 구간을 벗어나지는 않는다(미래로 새거나 너무 과거로 가지 않는다).
        Assert.All(readings, r =>
        {
            Assert.True(r.At <= Now);
            Assert.True(r.At >= Now - TimeSpan.FromMilliseconds(100));
        });
    }

    /// <summary>커서는 틱 사이에 이어진다 — 매번 같은 센서만 돌면 나머지는 영원히 침묵한다.</summary>
    [Fact]
    public void 커서는_틱_사이에_이어진다()
    {
        var engine = new SensorFarmEngine(new SiteProvisioning(100));

        var first = engine.Tick(Now, 40, TimeSpan.FromMilliseconds(50));
        var second = engine.Tick(Now.AddMilliseconds(50), 40, TimeSpan.FromMilliseconds(50));

        var overlap = first.Select(r => r.SensorId).Intersect(second.Select(r => r.SensorId)).Count();

        Assert.Equal(0, overlap);
    }
}
