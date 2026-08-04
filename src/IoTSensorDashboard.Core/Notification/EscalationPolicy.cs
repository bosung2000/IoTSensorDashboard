namespace IoTSensorDashboard.Core.Notification;

/// <summary>
/// 「지금 통지를 보내야 하는가, 보낸다면 어느 칸인가」.
///
/// 🔒 이 판정을 UI 코드에 흩뿌리지 말 것.
///
/// 📌 근거: 조건이 여기저기 흩어지면
///    「재시도가 폭주한다 / 해소됐는데 계속 보낸다 / 단계를 건너뛴다」 같은 회귀를
///    <b>테스트로 잡을 수 없다.</b>
///
/// 이 함수는:
///   · <b>상태를 바꾸지 않는다</b>(순수)
///   · <b>시간도 인자로 받는다</b> → 테스트에서 시계 조작이 필요 없다(결정적)
/// </summary>
public static class EscalationPolicy
{
    /// <param name="deliveredLevel">
    /// 🔑 <b>전달에 성공한</b> 최고 단계. <b>시도만 한 것은 포함하지 않는다.</b>
    ///
    /// 📌 시도를 성공으로 세면, 통지가 계속 실패하는데도 사다리가 위로 올라가
    ///    <b>아무도 못 받은 채 끝난다.</b>
    /// </param>
    /// <param name="sinceBorn">장애가 발생한 뒤 흐른 시간.</param>
    /// <param name="nextAttemptAt">직전 실패의 백오프가 풀리는 시각.</param>
    /// <param name="lastDeliveredAt">마지막으로 전달에 성공한 시각(최상위 반복 판정용).</param>
    /// <returns>보낼 단계. 보낼 필요가 없으면 null.</returns>
    public static EscalationStage? NextNotification(
        EscalationLadder ladder,
        bool resolved,
        bool acked,
        bool inFlight,
        int deliveredLevel,
        TimeSpan sinceBorn,
        DateTimeOffset now,
        DateTimeOffset? nextAttemptAt,
        DateTimeOffset? lastDeliveredAt)
    {
        ArgumentNullException.ThrowIfNull(ladder);

        // 복구됐으면 통지할 이유가 사라졌다.
        if (resolved) return null;

        // 사람이 이미 붙었다 — 사다리를 더 올릴 이유가 없다.
        if (acked) return null;

        // 전송 중 — 겹쳐 쏘면 같은 장애로 여러 건이 나간다.
        if (inFlight) return null;

        // 직전 실패의 백오프를 지킨다(매 틱 재시도 = 폭주).
        if (nextAttemptAt is { } next && now < next) return null;

        // 최상위까지 갔으면 더 올릴 곳이 없다 → 주기 반복만.
        //
        // 🔑 끝이 있으면 그 뒤로는 다시 조용해진다.
        //    아무도 안 보고 있다는 사실이 확인된 상태에서 조용해지는 것은 최악이다.
        if (deliveredLevel >= ladder.MaxLevel)
        {
            if (lastDeliveredAt is not { } last) return null;
            return now - last >= ladder.RepeatFinalEvery ? ladder.Final : null;
        }

        // 🔑 다음 <b>한 칸만</b> 본다.
        //
        // 📌 오래 방치됐다고 중간을 건너뛰면
        //    <b>사다리를 밟은 궤적이 로그에서 사라진다.</b>
        //    나중에 "왜 본사까지 갔나"를 설명할 수 없게 된다.
        var stage = ladder.ByLevel(deliveredLevel + 1);
        if (stage is null) return null;

        return sinceBorn > stage.After ? stage : null;
    }
}
