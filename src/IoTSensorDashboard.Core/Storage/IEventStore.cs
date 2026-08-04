using IoTSensorDashboard.Core.Domain;

namespace IoTSensorDashboard.Core.Storage;

/// <summary>
/// 이벤트 저장 계약.
///
/// 🔒 Update · Delete 메서드가 여기 없는 것은 실수가 아니라 설계다(I2).
///    계약에 수정·삭제 표면이 없으므로 소비 코드가 append-only 를 어길 방법이 없다.
///    "하지 말자"는 약속이 아니라 구조적으로 못 하게 만든 것이다. 추가하지 말 것.
///
///    롤업·프룬은 저장소 구현 클래스의 메서드이지 인터페이스 표면이 아니다.
///    개별 레코드 수정이 아니라 "오래된 것 정리"이므로 append-only 와 어긋나지 않는다.
/// </summary>
public interface IEventStore
{
    /// <summary>새 이벤트면 true, 이미 있는 정체성이면 false(멱등, I1).</summary>
    bool TryAppend(CountEvent e);

    /// <summary>
    /// 기본 구현 = 단순 루프. 영속 저장소만 트랜잭션으로 재정의한다.
    ///
    /// 🔒 절대 규칙 — 배치 판정 ≡ 건별 판정.
    ///    묶음으로 처리한 결과가 하나씩 처리한 것과 한 글자도 달라선 안 된다.
    ///    결과 배열은 입력과 같은 순서다.
    /// </summary>
    IReadOnlyList<bool> TryAppendBatch(IReadOnlyList<CountEvent> events)
    {
        var results = new bool[events.Count];
        for (int i = 0; i < events.Count; i++) results[i] = TryAppend(events[i]);
        return results;
    }

    /// <summary>저장된 이벤트를 삽입 순서대로.</summary>
    IReadOnlyList<CountEvent> Snapshot();

    /// <summary>
    /// 총 이벤트 수.
    ///
    /// 영속 구현에서는 raw + 롤업의 합이어야 한다 — raw 만 세면
    /// 롤업이 돌 때마다 총계가 줄어드는 것처럼 보이고, 그건 거짓말이다.
    /// </summary>
    long Count { get; }

    bool Contains(string dedupKey);
}
