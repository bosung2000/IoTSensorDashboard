namespace IoTSensorDashboard.Core.Notification;

/// <summary>보낼 통지 한 건.</summary>
public readonly record struct Notification(
    string SensorId, string Store, string Contact, string Message, DateTimeOffset At,
    int Level = 1, EscalationSeverity Severity = EscalationSeverity.Warning);

/// <summary>
/// 통지 결과.
///
/// 🔴 이 타입이 존재하는 것 자체가 설계다 — 아래 <see cref="INotifier"/> 주석 참조.
/// </summary>
public readonly record struct NotifyResult(bool Delivered, string? Error)
{
    public static NotifyResult Ok() => new(true, null);

    public static NotifyResult Fail(string error) => new(false, error);
}

/// <summary>
/// 통지 채널 플러그인.
///
/// 🔴 <see cref="Notify"/> 가 <c>void</c> 가 아닌 이유:
///
/// 📌 실제 사건 — 화면은 「⚠ 미확인 → 자동 통지됨」이라 쓰는데 담당자는 아무것도 못 받았다.
///    `Notify` 가 `void` 이고 구현이 예외를 삼키고 있어서
///    <b>호출자가 실패를 알 방법이 없었다.</b>
///    통지는 드물게 일어나 주기 하트비트로도 못 잡는다.
///
/// > <b>장애를 알리는 장치가 조용히 실패하면, 장애가 두 번 일어나는 셈이다.</b>
/// > <b>드물게 일어나는 채널은 카운터가 유일한 관측점이다.</b>
///
/// 일반화하면: <b>밖으로 나가는 호출은 결과를 돌려주게 만든다.</b>
/// `void` 면 삼킴이 기본값이 된다.
/// </summary>
public interface INotifier
{
    /// <summary>보낸다. 실패해도 던지지 않고 결과로 알린다.</summary>
    NotifyResult Notify(in Notification n);

    /// <summary>전달 성공 누적. 화면에 표시한다.</summary>
    long Sent { get; }

    /// <summary>전달 실패 누적. <b>「통지됨」으로 뭉개지 말고 빨간색으로 명시</b>한다.</summary>
    long Failed { get; }

    /// <summary>무엇이 실패했는지.</summary>
    string? LastError { get; }

    /// <summary>언제 실패했는지.</summary>
    DateTimeOffset? LastFailureAt { get; }
}
