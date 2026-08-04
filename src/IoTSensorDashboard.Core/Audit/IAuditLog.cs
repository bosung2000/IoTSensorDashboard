namespace IoTSensorDashboard.Core.Audit;

/// <summary>누가 · 언제 · 무엇을 · 어디에.</summary>
public readonly record struct AuditEntry(
    string Actor, string Role, string Action, string Target, string Scope, DateTimeOffset At);

/// <summary>
/// 감사 로그 — 추적 가능성이 존재 이유인 기록.
/// </summary>
public interface IAuditLog
{
    void Record(in AuditEntry e);

    /// <summary>최근 것부터 최대 max 건.</summary>
    IReadOnlyList<AuditEntry> Recent(int max = 500);

    /// <summary>
    /// 로그에 있는 전체 건수. 🔴 표시 건수와 별개로 계약에 있어야 한다.
    ///
    /// 📌 근거 — 실제 사건: 감사 로그 로드가 예외를 삼켜 못 읽어도 빈 목록이 남았다.
    ///    그 화면은 **「아무도 아무것도 안 했다」**로 읽힌다.
    ///    추적 가능성이 존재 이유인 창에서 이건 최악이다.
    ///
    /// 전체 건수를 따로 알 수 있으면 화면이 「전체 N건 중 최근 M건」이라고 말할 수 있고,
    /// 목록이 빈 이유가 "없어서"인지 "못 읽어서"인지도 구분할 수 있다.
    /// </summary>
    long Count { get; }
}
