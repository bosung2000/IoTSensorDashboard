using IoTSensorDashboard.Core.Health;
using Xunit;

namespace IoTSensorDashboard.Tests.Health;

/// <summary>
/// 사이트 건강 등급 — <b>「아직 못 봄」과 「끊김」을 섞지 않는다</b>.
///
/// 🔴 왜 테스트로 못박는가: 이 판정을 화면 네 곳(카드·상태표·토폴로지·게이지)이 공유한다.
///    합쳐 버리면 기동 직후 정상 상황이 <b>대량 장애</b>로 보인다 —
///    센서 1,000대를 17건/s 로 돌리면 한 바퀴에 약 59초, 침묵 허용치가
///    관측된 주기에서 나오므로 자리잡기까지 약 2분이 걸린다.
///    그 2분 내내 화면이 빨개지면, 정작 진짜 장애가 났을 때 눈에 띄지 않는다.
/// </summary>
public sealed class SiteHealthRuleTests
{
    [Fact]
    public void 명부에_센서가_없으면_없음()
    {
        Assert.Equal(SiteHealth.None, SiteHealthRule.Of(total: 0, online: 0, unknown: 0));
        Assert.Equal("센서 없음", SiteHealthRule.Describe(0, 0, 0));
    }

    /// <summary>🔑 전부 미관측 = 아직 못 본 것. <b>장애가 아니다.</b></summary>
    [Fact]
    public void 전부_한번도_못_봤으면_미관측()
    {
        Assert.Equal(SiteHealth.Unknown, SiteHealthRule.Of(total: 84, online: 0, unknown: 84));

        var text = SiteHealthRule.Describe(84, 0, 84);
        Assert.Contains("미관측", text);
        Assert.DoesNotContain("측정 불가", text);
    }

    /// <summary>
    /// 🔴 같은 「온라인 0」이라도 <b>관측한 적이 있으면</b> 진짜 장애다.
    ///    이 두 케이스가 갈리는 것이 이 등급의 존재 이유다.
    /// </summary>
    [Fact]
    public void 봤다가_전부_끊겼으면_무응답()
    {
        Assert.Equal(SiteHealth.Down, SiteHealthRule.Of(total: 84, online: 0, unknown: 0));
        Assert.Contains("측정 불가", SiteHealthRule.Describe(84, 0, 0));
    }

    /// <summary>🔑 미관측을 장애보다 <b>먼저</b> 본다 — 순서가 뒤집히면 헛걸음이 생긴다.</summary>
    [Fact]
    public void 미관측이_장애_판정보다_우선한다()
    {
        // online 0 · unknown 전부 → Down 이 아니라 Unknown 이어야 한다.
        Assert.Equal(SiteHealth.Unknown, SiteHealthRule.Of(total: 10, online: 0, unknown: 10));
    }

    [Theory]
    [InlineData(84, 70, 0)]
    [InlineData(84, 70, 10)]
    [InlineData(84, 83, 1)]
    public void 일부만_살아있으면_부분(int total, int online, int unknown) =>
        Assert.Equal(SiteHealth.Partial, SiteHealthRule.Of(total, online, unknown));

    /// <summary>부분 상태에서는 끊김과 미관측을 <b>따로</b> 적는다 — 할 일이 다르므로.</summary>
    [Fact]
    public void 부분_상태는_끊김과_미관측을_나눠_적는다()
    {
        var text = SiteHealthRule.Describe(total: 84, online: 70, unknown: 10);

        Assert.Contains("오프라인 4", text);    // 84 − 70 − 10
        Assert.Contains("미관측 10", text);
    }

    [Fact]
    public void 전부_온라인이면_정상()
    {
        Assert.Equal(SiteHealth.Ok, SiteHealthRule.Of(total: 84, online: 84, unknown: 0));
        Assert.Equal("정상", SiteHealthRule.Describe(84, 84, 0));
    }

    /// <summary>
    /// 합이 어긋나도 <b>문구가 음수를 뱉지 않는다</b>.
    /// 표시 코드는 이상한 입력에도 읽을 수 있는 것을 내놓아야 한다.
    /// </summary>
    [Fact]
    public void 합이_어긋나도_음수를_적지_않는다()
    {
        var text = SiteHealthRule.Describe(total: 10, online: 8, unknown: 5);

        Assert.DoesNotContain("-", text);
        Assert.Contains("오프라인 0", text);
    }
}
