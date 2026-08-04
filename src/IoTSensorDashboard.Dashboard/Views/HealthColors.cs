using System.Windows.Media;
using IoTSensorDashboard.Core.Health;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Dashboard.Views;

/// <summary>
/// 건강 등급 → 색. <b>단일 출처</b>.
///
/// 🔑 이 판정이 필요한 패널이 넷이다(카드 · 상태표 · 토폴로지 · 게이지).
///    각자 <c>if</c> 를 쓰면 한 곳만 고치고 나머지를 잊어, <b>같은 매장이 화면마다
///    다른 색</b>으로 보인다. 그건 사용자에게 「둘 중 하나가 고장 났다」로 읽힌다.
///
/// 🔴 <b>미관측(<see cref="SiteHealth.Unknown"/>)은 빨강이 아니다.</b>
///    아직 못 본 것을 장애색으로 칠하면 기동 직후 화면이 온통 빨개지고,
///    그 뒤로 진짜 장애가 나도 눈에 띄지 않는다 —
///    <b>경보를 남발하면 경보가 죽는다.</b>
/// </summary>
public static class HealthColors
{
    public static Brush Of(SiteHealth health) => health switch
    {
        SiteHealth.Ok => HudPalette.In,
        SiteHealth.Partial => HudPalette.Warn,
        SiteHealth.Down => HudPalette.Down,

        // 「모름」의 색 — 0 도 100 도 아닌 회색.
        _ => HudPalette.Unknown
    };

    /// <summary>
    /// 가동률 게이지용 — <b>비율이 없으면(관측 못 했으면) 회색</b>.
    /// </summary>
    public static Brush ForUptime(double? uptime) => uptime switch
    {
        null => HudPalette.Unknown,
        >= 0.999 => HudPalette.In,
        >= 0.95 => HudPalette.Warn,
        _ => HudPalette.Down
    };
}
