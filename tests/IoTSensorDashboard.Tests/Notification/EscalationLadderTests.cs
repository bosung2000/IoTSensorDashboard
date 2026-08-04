using IoTSensorDashboard.Core.Notification;
using Xunit;

namespace IoTSensorDashboard.Tests.Notification;

/// <summary>
/// 사다리 자체의 검사 — 생성자가 막아 준다.
///
/// 📌 「새 단계 추가 = 한 줄」이 목표인데, <b>그 한 줄이 틀렸을 때 여기서 잡혀야</b> 한다.
///    안 잡으면 사다리가 <b>조용히 끊긴다</b> — 통지가 안 나가는데 오류도 없다.
/// </summary>
public sealed class EscalationLadderTests
{
    private static EscalationStage Stage(int level, int afterSec) =>
        new(level, $"{level}차", TimeSpan.FromSeconds(afterSec),
            EscalationSeverity.Warning, EscalationRole.StoreManager);

    [Fact]
    public void 단계가_없으면_막는다()
    {
        // 아무도 통지받지 못한다.
        Assert.Throws<ArgumentException>(() =>
            new EscalationLadder([], TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void 레벨이_1부터_연속이_아니면_막는다()
    {
        // 📌 정책이 deliveredLevel + 1 로 다음 칸을 찾으므로,
        //    번호가 비면 **다음 칸을 못 찾아 멈춘다.**
        Assert.Throws<ArgumentException>(() =>
            new EscalationLadder([Stage(1, 10), Stage(3, 20)], TimeSpan.FromSeconds(60)));

        Assert.Throws<ArgumentException>(() =>
            new EscalationLadder([Stage(0, 10)], TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void 임계가_단조_증가하지_않으면_막는다()
    {
        // 📌 뒤 칸이 더 빨리 오면 **앞 칸이 영원히 발사되지 않는다**(한 칸씩 올라가므로).
        Assert.Throws<ArgumentException>(() =>
            new EscalationLadder([Stage(1, 60), Stage(2, 30)], TimeSpan.FromSeconds(60)));

        Assert.Throws<ArgumentException>(() =>
            new EscalationLadder([Stage(1, 60), Stage(2, 60)], TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void 반복_간격이_0_이하면_막는다()
    {
        // 무한 발사가 된다.
        Assert.Throws<ArgumentException>(() =>
            new EscalationLadder([Stage(1, 10)], TimeSpan.Zero));

        Assert.Throws<ArgumentException>(() =>
            new EscalationLadder([Stage(1, 10)], TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void 정상_사다리는_만들어진다()
    {
        var ladder = new EscalationLadder([Stage(1, 10), Stage(2, 20), Stage(3, 30)],
                                          TimeSpan.FromSeconds(60));

        Assert.Equal(3, ladder.MaxLevel);
        Assert.Equal(3, ladder.Final.Level);
        Assert.Equal(2, ladder.ByLevel(2)!.Level);
        Assert.Null(ladder.ByLevel(0));
        Assert.Null(ladder.ByLevel(4));
    }

    [Fact]
    public void 이번_범위의_사다리가_명세와_일치한다()
    {
        // ⚠️ 시연용으로 짧게 잡은 값이다(3단계가 1분 안에 다 보이도록).
        //    실운영 권장은 5분 → 15분 → 30분, 반복 30분.
        var ladder = EscalationLadder.Demo;

        Assert.Equal(3, ladder.Stages.Count);

        Assert.Equal(TimeSpan.FromSeconds(25), ladder.Stages[0].After);
        Assert.Equal(EscalationRole.StoreManager, ladder.Stages[0].Role);
        Assert.Equal(EscalationSeverity.Warning, ladder.Stages[0].Severity);

        Assert.Equal(TimeSpan.FromSeconds(60), ladder.Stages[1].After);
        Assert.Equal(EscalationRole.GroupManager, ladder.Stages[1].Role);

        Assert.Equal(TimeSpan.FromSeconds(110), ladder.Stages[2].After);
        Assert.Equal(EscalationRole.HeadquartersDuty, ladder.Stages[2].Role);

        Assert.Equal(TimeSpan.FromSeconds(60), ladder.RepeatFinalEvery);
    }

    [Fact]
    public void 연락처는_어떤_경우에도_null_이_아니다()
    {
        // 🔒 「연락처가 없어서 안 보냄」이 되면 안 된다. 그건 조용한 실패다.
        var withoutStore = Core.Provisioning.DutyRoster.For(EscalationRole.StoreManager, null, null);
        var withoutGroup = Core.Provisioning.DutyRoster.For(EscalationRole.GroupManager, null, null);
        var hq = Core.Provisioning.DutyRoster.For(EscalationRole.HeadquartersDuty, null, null);

        Assert.True(withoutStore.IsFallback);
        Assert.True(withoutGroup.IsFallback);
        Assert.False(hq.IsFallback);       // 본사 당직은 항상 존재한다

        Assert.All([withoutStore, withoutGroup, hq],
            c => Assert.False(string.IsNullOrWhiteSpace(c.Phone)));
    }
}
