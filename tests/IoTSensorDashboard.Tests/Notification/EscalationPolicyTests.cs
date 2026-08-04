using IoTSensorDashboard.Core.Notification;
using Xunit;

namespace IoTSensorDashboard.Tests.Notification;

/// <summary>
/// 사다리 판정 — 아무도 안 볼 때 어디까지 올라가는가.
///
/// 🔴 소멸 문제: 1차 통지 한 번으로 끝나면, 점장이 폰을 못 보는 순간
///    그 장애는 <b>영원히 방치</b>된다.
///    화면에는 「통지됨」이라 적혀 있어 조치가 진행 중인 것처럼 보이고,
///    아무도 다시 확인하지 않는다.
///
/// 이 판정은 순수 함수이고 <b>시간도 인자로 받는다</b> — 시계 조작 없이 결정적으로 검증된다.
/// </summary>
public sealed class EscalationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);
    private static readonly EscalationLadder Ladder = EscalationLadder.Demo;

    private static EscalationStage? Next(
        TimeSpan sinceBorn,
        int deliveredLevel = 0,
        bool resolved = false,
        bool acked = false,
        bool inFlight = false,
        DateTimeOffset? nextAttemptAt = null,
        DateTimeOffset? lastDeliveredAt = null)
        => EscalationPolicy.NextNotification(
            Ladder, resolved, acked, inFlight, deliveredLevel, sinceBorn, Now,
            nextAttemptAt, lastDeliveredAt);

    // ── 승격 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 임계_전에는_아무것도_안_보낸다()
    {
        Assert.Null(Next(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void 임계를_넘으면_1차_통지가_나간다()
    {
        var stage = Next(TimeSpan.FromSeconds(30));

        Assert.NotNull(stage);
        Assert.Equal(1, stage!.Level);
        Assert.Equal(EscalationSeverity.Warning, stage.Severity);
        Assert.Equal(EscalationRole.StoreManager, stage.Role);
    }

    [Fact]
    public void 오래_방치돼도_중간_단계를_건너뛰지_않는다()
    {
        // 🔑 <b>다음 한 칸만</b> 본다.
        //
        // 📌 오래 방치됐다고 중간을 건너뛰면
        //    **사다리를 밟은 궤적이 로그에서 사라진다.**
        //    나중에 "왜 본사까지 갔나"를 설명할 수 없게 된다.
        var stage = Next(TimeSpan.FromHours(1), deliveredLevel: 0);

        Assert.Equal(1, stage!.Level);   // 1시간이 지났어도 1차부터
    }

    [Fact]
    public void 한_칸씩_올라간다()
    {
        Assert.Equal(2, Next(TimeSpan.FromSeconds(70), deliveredLevel: 1)!.Level);
        Assert.Equal(3, Next(TimeSpan.FromSeconds(120), deliveredLevel: 2)!.Level);
    }

    [Fact]
    public void 승격_단계는_치명이다()
    {
        // 🔑 Critical 의 뜻은 「장애가 더 심해졌다」가 아니라
        //    **「아무도 안 보고 있다가 확인됐다」**이다.
        Assert.Equal(EscalationSeverity.Critical, Next(TimeSpan.FromSeconds(70), 1)!.Severity);
        Assert.Equal(EscalationSeverity.Critical, Next(TimeSpan.FromSeconds(120), 2)!.Severity);
    }

    [Fact]
    public void 다음_칸_임계_전이면_기다린다()
    {
        // 1차는 전달됐고, 2차 임계(60초)는 아직 안 됐다.
        Assert.Null(Next(TimeSpan.FromSeconds(40), deliveredLevel: 1));
    }

    // ── 중단 조건 ────────────────────────────────────────────────────────

    [Fact]
    public void 복구되면_즉시_중단한다()
    {
        Assert.Null(Next(TimeSpan.FromHours(1), resolved: true));
    }

    [Fact]
    public void 사람이_확인하면_즉시_중단한다()
    {
        // 🔑 사람이 이미 붙었다 — 사다리를 더 올릴 이유가 없다.
        Assert.Null(Next(TimeSpan.FromHours(1), acked: true));
    }

    [Fact]
    public void 전송_중이면_겹쳐_쏘지_않는다()
    {
        // 겹쳐 쏘면 같은 장애로 여러 건이 나간다.
        Assert.Null(Next(TimeSpan.FromHours(1), inFlight: true));
    }

    [Fact]
    public void 백오프_전에는_재시도하지_않는다()
    {
        // 매 틱 재시도하면 폭주한다.
        Assert.Null(Next(TimeSpan.FromSeconds(30), nextAttemptAt: Now.AddSeconds(10)));

        // 백오프가 풀리면 나간다.
        Assert.NotNull(Next(TimeSpan.FromSeconds(30), nextAttemptAt: Now.AddSeconds(-1)));
    }

    // ── 최상위 반복 ──────────────────────────────────────────────────────

    [Fact]
    public void 최상위에_도달하면_주기적으로_반복한다()
    {
        // 🔑 끝이 있으면 그 뒤로는 다시 조용해진다.
        //    아무도 안 보고 있다는 사실이 확인된 상태에서 조용해지는 것은 최악이다.
        var justDelivered = Next(TimeSpan.FromHours(1),
            deliveredLevel: Ladder.MaxLevel, lastDeliveredAt: Now.AddSeconds(-10));
        Assert.Null(justDelivered);   // 아직 반복 간격이 안 찼다

        var repeat = Next(TimeSpan.FromHours(1),
            deliveredLevel: Ladder.MaxLevel, lastDeliveredAt: Now - Ladder.RepeatFinalEvery);
        Assert.NotNull(repeat);
        Assert.Equal(Ladder.MaxLevel, repeat!.Level);
    }

    [Fact]
    public void 최상위인데_전달_시각을_모르면_보내지_않는다()
    {
        // 언제부터 세야 할지 모르는 상태에서 쏘면 폭주할 수 있다.
        Assert.Null(Next(TimeSpan.FromHours(1),
            deliveredLevel: Ladder.MaxLevel, lastDeliveredAt: null));
    }

    // ── 🔴 실패는 승격이 아니다 ──────────────────────────────────────────

    [Fact]
    public void 전달에_실패하면_같은_칸을_다시_시도한다()
    {
        // 🔴 deliveredLevel 은 **전달에 성공한** 최고 단계다.
        //    시도만 한 것은 포함하지 않는다.
        //
        // 📌 시도를 성공으로 세면, 통지가 계속 실패하는데도 사다리가 위로 올라가
        //    **아무도 못 받은 채 끝난다.**
        //
        //    1차 전송을 세 번 시도해서 세 번 다 실패한 상황.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var stage = Next(TimeSpan.FromSeconds(30), deliveredLevel: 0);
            Assert.Equal(1, stage!.Level);   // 계속 1차다. 올라가지 않는다.
        }
    }
}
