using IoTSensorDashboard.Core.Provisioning;
using Xunit;

namespace IoTSensorDashboard.Tests.Provisioning;

/// <summary>
/// 있어야 할 명부 — <b>I5 의 분모가 나오는 곳</b>.
///
/// 📌 분모를 관측 기반으로 뒀다가 난 사고:
///    1,000대 중 50대가 무응답인데 화면은 950/950 = 100% 를 띄웠다.
///    처음부터 죽어 있던 센서가 분모에서 통째로 빠졌기 때문이다.
/// </summary>
public sealed class SiteProvisioningTests
{
    [Fact]
    public void 조직은_본사_1_본부_2_매장_12_다()
    {
        var p = new SiteProvisioning();

        Assert.Equal(15, p.Sites.Count);                                  // 1 + 2 + 12
        Assert.Single(p.Sites, s => s.ParentId is null);                  // 루트는 하나
        Assert.Equal(2, p.Sites.Count(s => s.ParentId == "hq"));
        Assert.Equal(12, p.StoreIds.Count);
    }

    [Fact]
    public void 센서는_1000대다()
    {
        Assert.Equal(1_000, new SiteProvisioning().Sensors.Count);
    }

    [Fact]
    public void 센서_ID_는_결정적이다()
    {
        // 🔑 같은 입력이면 항상 같은 ID. 난수나 시각이 섞이면
        //    재시작할 때마다 "새 센서 1,000대"가 나타나고 옛 센서는 영원히 오프라인으로 남는다.
        var first = new SiteProvisioning();
        var second = new SiteProvisioning();

        Assert.Equal(first.SensorIds, second.SensorIds);
    }

    [Theory]
    [InlineData(0, "flir-0000")]
    [InlineData(1, "milesight-0001")]
    [InlineData(42, "flir-0042")]
    [InlineData(859, "milesight-0859")]
    [InlineData(999, "milesight-0999")]
    public void 센서_ID_규약은_벤더_네자리다(int index, string expected)
    {
        Assert.Equal(expected, SiteProvisioning.SensorIdFor(index));
    }

    [Fact]
    public void 벤더는_짝홀_교대다()
    {
        var p = new SiteProvisioning();

        Assert.Equal(500, p.Sensors.Count(s => s.Vendor == "flir"));
        Assert.Equal(500, p.Sensors.Count(s => s.Vendor == "milesight"));
    }

    [Fact]
    public void 매장_배정은_라운드로빈이라_고르게_나뉜다()
    {
        var p = new SiteProvisioning();

        var perStore = p.StoreIds.Select(p.SensorCountOf).ToList();

        // 1,000 / 12 = 83.3 → 앞 4개가 84, 나머지 8개가 83
        Assert.Equal(1_000, perStore.Sum());
        Assert.Equal(4, perStore.Count(c => c == 84));
        Assert.Equal(8, perStore.Count(c => c == 83));
    }

    [Fact]
    public void 본부별_센서_수는_502_대_498_이다()
    {
        // 라운드로빈 순서가 바뀌면 이 값이 달라진다.
        // 화면에 그대로 나오는 숫자라 여기서 잠가 둔다.
        var p = new SiteProvisioning();

        int metro = p.StoreIds.Where(id => id.StartsWith("g1", StringComparison.Ordinal))
                              .Sum(p.SensorCountOf);
        int south = p.StoreIds.Where(id => id.StartsWith("g2", StringComparison.Ordinal))
                              .Sum(p.SensorCountOf);

        Assert.Equal(502, metro);
        Assert.Equal(498, south);
        Assert.Equal(1_000, metro + south);
    }

    [Fact]
    public void 모든_매장에_센서가_최소_한_대씩_있다()
    {
        // 🔴 센서가 0대인 매장이 생기면 그 매장은 "측정 불가"인데,
        //    분모 계산에서 빠지면 화면에서 아예 사라진다.
        var p = new SiteProvisioning();

        Assert.All(p.StoreIds, id => Assert.True(p.SensorCountOf(id) > 0, $"{id} 에 센서가 없다"));
    }

    [Fact]
    public void 센서에서_지점을_찾을_수_있다()
    {
        // 스트림에서 사이트를 못 받았을 때 쓰는 폴백의 출처.
        var p = new SiteProvisioning();

        var siteId = p.SiteOf("flir-0000");
        Assert.NotNull(siteId);
        Assert.Equal("강남점", p.SiteName(siteId!));
    }

    [Fact]
    public void 모르는_센서는_null_이다()
    {
        // 🔒 모르면 지어내지 않는다. 소비 측이 "스코프 밖"으로 처리한다.
        Assert.Null(new SiteProvisioning().SiteOf("axis-9999"));
    }

    [Fact]
    public void 명부의_모든_센서가_지점을_갖는다()
    {
        // 배정 없는 센서가 있으면 그 센서의 데이터는 어느 매장에도 안 잡힌다 —
        // 수집은 되는데 화면 어디에도 안 나오는 상태가 된다.
        var p = new SiteProvisioning();

        Assert.All(p.SensorIds, id => Assert.NotNull(p.SiteOf(id)));
    }

    [Fact]
    public void 센서_수를_바꿔도_구조는_유지된다()
    {
        var p = new SiteProvisioning(sensorCount: 24);

        Assert.Equal(24, p.Sensors.Count);
        Assert.Equal(12, p.StoreIds.Count);
        Assert.All(p.StoreIds, id => Assert.Equal(2, p.SensorCountOf(id)));
    }

    [Fact]
    public void 센서가_0대여도_생성은_된다()
    {
        // 조직 구조는 센서와 무관하게 존재한다.
        // "센서 0대인 매장"이 목록에 남는지를 확인하는 것이 I5 의 요구다.
        var p = new SiteProvisioning(sensorCount: 0);

        Assert.Empty(p.Sensors);
        Assert.Equal(12, p.StoreIds.Count);
    }
}
