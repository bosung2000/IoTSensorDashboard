using IoTSensorDashboard.Core.Rendering;
using Xunit;

namespace IoTSensorDashboard.Tests.Rendering;

/// <summary>
/// 렌더 빈도 정책 — 「부하가 없으면 CPU 도 안 써야 한다」.
///
/// 📌 근거: 부하가 15 msg/s 뿐인데 세 앱이 CPU 314% 를 쓰고 있었다.
///    원인은 데이터가 아니라 그리기였다 — 부하와 무관하게 항상 33ms 마다 다시 그렸다.
/// </summary>
public sealed class FramePolicyTests
{
    [Fact]
    public void 보고_있고_값도_움직이면_가장_자주_그린다()
    {
        Assert.Equal(FramePolicy.Active,
            FramePolicy.IntervalFor(windowActive: true, busy: true, animationsOn: true));
    }

    [Fact]
    public void 보고_있어도_조용하면_덜_그린다()
    {
        Assert.Equal(FramePolicy.Idle,
            FramePolicy.IntervalFor(windowActive: true, busy: false, animationsOn: true));
    }

    [Fact]
    public void 창이_뒤에_있으면_더_덜_그린다()
    {
        // 아무도 안 보는 화면에 CPU 를 쓰지 않는다.
        Assert.Equal(FramePolicy.Background,
            FramePolicy.IntervalFor(windowActive: false, busy: true, animationsOn: true));
    }

    [Fact]
    public void 애니메이션을_끄면_모든_상황보다_느리다()
    {
        // 사용자가 명시적으로 끈 것이므로 다른 조건보다 우선한다.
        foreach (var active in new[] { true, false })
            foreach (var busy in new[] { true, false })
                Assert.Equal(FramePolicy.AnimationsOff,
                    FramePolicy.IntervalFor(active, busy, animationsOn: false));
    }

    [Fact]
    public void 애니메이션을_꺼도_완전히_멈추지는_않는다()
    {
        // ⚠️ 세우면 창 크기 변경·데이터 갱신 후 다시 그릴 사람이 없어
        //    화면이 낡은 채로 남는다.
        Assert.True(FramePolicy.AnimationsOff > TimeSpan.Zero);
        Assert.True(FramePolicy.AnimationsOff < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void 부하가_없을수록_간격이_길어진다()
    {
        // 🔑 이 순서가 깨지면 「부하가 없으면 CPU 도 내려간다」는 주장이 거짓이 된다.
        Assert.True(FramePolicy.Active < FramePolicy.Idle);
        Assert.True(FramePolicy.Idle < FramePolicy.Background);
        Assert.True(FramePolicy.Background < FramePolicy.AnimationsOff);
    }

    [Fact]
    public void 간격이_명세와_일치한다()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(33), FramePolicy.Active);       // 30fps
        Assert.Equal(TimeSpan.FromMilliseconds(125), FramePolicy.Idle);        // 8fps
        Assert.Equal(TimeSpan.FromMilliseconds(500), FramePolicy.Background);  // 2fps
        Assert.Equal(TimeSpan.FromMilliseconds(1000), FramePolicy.AnimationsOff); // 1fps
    }
}
