namespace IoTSensorDashboard.Core.Notification;

/// <summary>
/// 심각도.
///
/// 🔑 <see cref="Critical"/> 의 뜻이 「장애가 더 심해졌다」가 아니라
///    <b>「아무도 안 보고 있다가 확인됐다」</b>이다.
///    같은 장애라도 <b>방치된 시간</b>이 길수록 심각해진다.
/// </summary>
public enum EscalationSeverity
{
    /// <summary>1차 통지. 담당자가 아직 볼 기회가 있는 단계.</summary>
    Warning = 0,

    /// <summary>1차가 무응답이라 조직 위로 올라간 단계. "아무도 안 보고 있다"가 확인된 상태.</summary>
    Critical = 1,
}

/// <summary>통지를 받을 사람.</summary>
public enum EscalationRole
{
    StoreManager,
    GroupManager,
    HeadquartersDuty,
}

/// <summary>사다리의 한 칸 — "발생 후 After 가 지나도록 미확인이면 이 역할에게 이 심각도로 알린다".</summary>
public sealed record EscalationStage(
    int Level, string Name, TimeSpan After, EscalationSeverity Severity, EscalationRole Role);

/// <summary>
/// 통지 사다리.
///
/// 🔴 소멸 문제: 1차 통지 한 번으로 끝나면, 점장이 폰을 못 보는 순간
///    그 장애는 <b>영원히 방치</b>된다.
///    화면에는 「통지됨」이라 적혀 있어 <b>조치가 진행 중인 것처럼 보이고</b>,
///    아무도 다시 확인하지 않는다.
/// </summary>
public sealed class EscalationLadder
{
    /// <summary>
    /// 이번 범위의 값.
    ///
    /// ⚠️ <b>시연용으로 짧게 잡은 값</b>이다 — 3단계가 1분 안에 다 보이도록.
    ///    실운영이라면 말이 안 되는 간격이다.
    ///
    ///    실운영 권장: <b>5분(점장) → 15분(본부) → 30분(본사), 반복 30분</b>
    /// </summary>
    public static EscalationLadder Demo { get; } = new(
    [
        new EscalationStage(1, "1차 통지", TimeSpan.FromSeconds(25),
                            EscalationSeverity.Warning, EscalationRole.StoreManager),
        new EscalationStage(2, "본부 승격", TimeSpan.FromSeconds(60),
                            EscalationSeverity.Critical, EscalationRole.GroupManager),
        new EscalationStage(3, "본사 승격", TimeSpan.FromSeconds(110),
                            EscalationSeverity.Critical, EscalationRole.HeadquartersDuty),
    ], repeatFinalEvery: TimeSpan.FromSeconds(60));

    private readonly IReadOnlyList<EscalationStage> _stages;

    /// <summary>
    /// 🔑 생성자가 <b>검사</b>한다.
    ///
    /// 「새 단계 추가 = 한 줄」이 목표인데, <b>그 한 줄이 틀렸을 때 여기서 잡혀야</b> 한다.
    /// 안 잡으면 사다리가 <b>조용히 끊긴다</b> — 통지가 안 나가는데 오류도 없다.
    /// </summary>
    public EscalationLadder(IReadOnlyList<EscalationStage> stages, TimeSpan repeatFinalEvery)
    {
        ArgumentNullException.ThrowIfNull(stages);

        if (stages.Count == 0)
            throw new ArgumentException(
                "사다리에 단계가 하나도 없으면 아무도 통지받지 못한다.", nameof(stages));

        for (int i = 0; i < stages.Count; i++)
        {
            // 정책이 deliveredLevel+1 로 다음 칸을 찾으므로,
            // 레벨이 1부터 연속이 아니면 사다리가 조용히 끊긴다.
            if (stages[i].Level != i + 1)
                throw new ArgumentException(
                    $"단계 레벨은 1부터 연속이어야 한다. {i} 번째 = {stages[i].Level}", nameof(stages));

            // 뒤 칸이 더 빨리 오면 앞 칸은 영원히 발사되지 않는다(한 칸씩 올라가므로).
            if (i > 0 && stages[i].After <= stages[i - 1].After)
                throw new ArgumentException(
                    $"단계 임계는 단조 증가해야 한다: {stages[i - 1].After} → {stages[i].After}", nameof(stages));
        }

        if (repeatFinalEvery <= TimeSpan.Zero)
            throw new ArgumentException(
                "최상위 반복 간격이 0 이하면 무한 발사가 된다.", nameof(repeatFinalEvery));

        _stages = stages;
        RepeatFinalEvery = repeatFinalEvery;
    }

    public TimeSpan RepeatFinalEvery { get; }

    public int MaxLevel => _stages[^1].Level;

    public EscalationStage Final => _stages[^1];

    public IReadOnlyList<EscalationStage> Stages => _stages;

    public EscalationStage? ByLevel(int level) =>
        level >= 1 && level <= _stages.Count ? _stages[level - 1] : null;
}
