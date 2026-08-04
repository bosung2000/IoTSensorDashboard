namespace IoTSensorDashboard.Core.Reporting;

/// <summary>
/// 매장 하나의 가동 실적.
/// </summary>
/// <param name="Uptime">
/// 🔴 <b>nullable 인 것이 핵심이다.</b> null = <b>측정 불가</b>
/// (관측 창 동안 이 매장의 데이터를 한 번도 못 받음).
///
/// 📌 근거 — 실제로 무슨 일이 있었나:
///    종전에는 <b>장애 이력이 없으면 무조건 가동률 100%</b> 였다.
///    그래서 <b>관제실이 꺼져 있었거나 센서가 처음부터 죽어 있던 매장</b>도 100.000% 로 나왔다.
///
///    화면이 스스로 「구매 판단 근거」라고 부르는 숫자가 <b>가장 크게 거짓말</b>한 것이다.
///
/// > <b>「장애가 없었다」와 「지켜보지 못했다」는 다른 사실이다.</b>
/// > 같은 숫자(100%)로 뭉개지 않는다.
/// </param>
public readonly record struct SlaStoreStat(
    string Store,
    double? Uptime,
    int Incidents,
    double DowntimeSec,
    double MttrSec);

/// <summary>
/// 여러 매장의 요약.
/// </summary>
/// <param name="AverageUptime">
/// 측정 가능한 매장들의 평균. 전부 측정 불가면 null.
///
/// ❌ null 을 0 으로 보고 평균 → 실제보다 낮게 나온다
/// ❌ null 을 1(100%)로 보고 평균 → 실제보다 높게 나온다 ← <b>이게 원래 버그였다</b>
/// ✅ 아예 <b>빼고</b> 계산 + 화면에 「측정 불가 N곳」을 함께 표시
/// </param>
/// <param name="UnmeasurableCount">
/// 🔑 <b>반드시 화면에 적을 것.</b>
///    평균만 보여주면 몇 곳이 빠졌는지 알 수 없고, 그러면 평균이 다시 거짓말한다.
/// </param>
public readonly record struct SlaSummary(
    double? AverageUptime,
    int UnmeasurableCount,
    int MeasuredCount,
    int TotalIncidents);
