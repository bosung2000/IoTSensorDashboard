namespace IoTSensorDashboard.Core.Domain;

/// <summary>
/// 이 시스템의 원자 — 센서 한 대가 한 순간에 센 인원 수.
///
/// 왜 record + init(불변)인가: 수집 파이프라인은 여러 워커 스레드가 동시에 돈다.
/// 이벤트 객체가 나중에 수정될 수 있으면 "어느 스레드가 언제 바꿨는지"에 따라 저장 결과가 달라지고,
/// 그러면 I1(정확히 1회)을 증명할 방법이 없다. 바꿔야 하면 with 로 새 객체를 만든다.
/// </summary>
public sealed record CountEvent
{
    /// <summary>센서 식별자.</summary>
    public required string SensorId { get; init; }

    /// <summary>이벤트 발생 시각. 원본 타임존 보존을 위해 DateTimeOffset(I3).</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>카운트 값. 음수는 파이프라인이 거부.</summary>
    public required int Count { get; init; }

    /// <summary>방향("in"/"out"). 없으면 null.</summary>
    public string? Direction { get; init; }

    /// <summary>
    /// 멱등 키(I1) — 정체성 = SensorId + OccurredAt(절대 순간) + Direction.
    ///
    /// 🔒 이 문자열 포맷(구분자 '|', 밀리초 단위)을 바꾸지 말 것. DB 의 PRIMARY KEY 값이다.
    ///
    /// Count 가 키에 없는 이유:
    ///   같은 순간의 재전송은 값이 달라도 같은 논리 이벤트다. 저장이 append-only 이므로 최초본이 권위를 갖는다.
    ///   Count 를 키에 넣으면 값이 흔들린 재전송이 새 이벤트로 들어와 같은 순간을 두 번 세게 된다.
    ///
    /// Direction 이 키에 있는 이유:
    ///   한 센서가 같은 순간에 in 과 out 을 각각 보고한다. 방향이 키에 없으면 둘 중 하나가 중복으로 접혀 사라진다.
    ///
    /// ToUnixTimeMilliseconds() 를 쓰는 이유:
    ///   절대 순간으로 비교한다. "+09:00" 표기와 "Z" 표기가 같은 순간이면 같은 이벤트다.
    /// </summary>
    public string DedupKey => $"{SensorId}|{OccurredAt.ToUnixTimeMilliseconds()}|{Direction ?? ""}";
}
