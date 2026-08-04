namespace IoTSensorDashboard.Core.Notification;

/// <summary>
/// 통지 궤적 한 줄.
///
/// 🔑 <see cref="Delivered"/> 가 false 면 <b>시도했으나 담당자에게 닿지 못한</b> 건이다.
///    「보낸 것」과 「닿은 것」을 다른 상태로 남긴다.
/// </summary>
public readonly record struct EscalationRecord(
    DateTimeOffset At, string Store, string SensorId, string Contact, string Message,
    bool Delivered, string? Error,
    int Level = 1, EscalationSeverity Severity = EscalationSeverity.Warning);

/// <summary>
/// 통지 로그 조회 결과.
///
/// 🔴 표시 건수만 주면 화면이 <b>「이게 전부」라고 오해</b>하게 만든다.
///    전체 건수와 <b>해석 실패한 줄 수</b>까지 실어,
///    잘렸는지·깨졌는지를 화면이 말할 수 있게 한다.
/// </summary>
/// <param name="Records">최신순 표시 대상.</param>
/// <param name="Total">로그에 존재하는 전체 건수(미전달 포함).</param>
/// <param name="Malformed">형식이 깨져 해석하지 못한 줄 수 — <b>0 이 아니면 로그가 손상된 것</b>.</param>
/// <param name="Undelivered">전달 실패 건수.</param>
/// <param name="Critical">
/// 치명 통지 건수 — <b>표시 상한과 무관한 전체 기준</b>.
///
/// 🔑 최근 500건에 치명이 없다고 「치명 0건」이라 쓰면 거짓말이다.
/// </param>
public sealed record EscalationLogView(
    IReadOnlyList<EscalationRecord> Records,
    int Total,
    int Malformed,
    int Undelivered,
    int Critical);
