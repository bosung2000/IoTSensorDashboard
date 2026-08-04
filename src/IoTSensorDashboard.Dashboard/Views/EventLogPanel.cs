using System.Globalization;
using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.Ui.Common.Rendering;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Dashboard.Views;

/// <summary>
/// 상태가 <b>변한</b> 순간들.
///
/// 🔑 <b>모든 이벤트를 흘리지 않는다.</b> 초당 수백 건이 지나가는 화면에서 전건 로그는
///    아무도 못 읽고, 못 읽는 로그는 없는 것과 같다. 여기 남는 건
///    <b>연결·오프라인·복구·권한 전환</b> — 나중에 「언제부터 이랬나」를 묻게 되는 것들이다.
///
/// 🔴 센서 한 대가 죽어도 큰 숫자는 999/1000 로 거의 안 변한다.
///    <b>눈으로는 못 잡는 변화가 여기 글자로 남는다.</b>
/// </summary>
public sealed class EventLogPanel : HudPanel
{
    public EventLogPanel()
    {
        Title = "이벤트 목록";
    }

    protected override void RenderContent(DrawingContext dc, Rect area)
    {
        var log = Snapshot.Log;

        if (log.Count == 0)
        {
            HudDraw.Text(dc, "기록된 상태 변화가 없습니다", area.X, area.Y + 4, 11,
                HudPalette.TextDim, Ppd);
            return;
        }

        const double RowHeight = 17;

        double timeWidth = 52;
        double kindWidth = 40;
        double messageX = area.X + timeWidth + kindWidth;
        double messageWidth = Math.Max(20, area.Right - messageX);

        double y = area.Y;
        int shown = 0;

        foreach (var entry in log)
        {
            if (y + RowHeight > area.Bottom) break;

            HudDraw.Text(dc, entry.At.ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                area.X, y, 10.5, HudPalette.TextDim, Ppd);

            var color = ColorOf(entry.Kind);

            dc.DrawEllipse(color, null, new Point(area.X + timeWidth + 4, y + 7), 3, 3);

            HudDraw.Text(dc, entry.Kind, area.X + timeWidth + 12, y, 10, HudPalette.TextMuted, Ppd,
                maxWidth: kindWidth - 14);

            HudDraw.Text(dc, entry.Message, messageX, y, 11, color, Ppd, maxWidth: messageWidth);

            y += RowHeight;
            shown++;
        }

        // 잘랐으면 잘랐다고 쓴다 — 보이는 것이 전부인 줄 알게 두지 않는다.
        if (log.Count > shown && shown > 0)
        {
            HudDraw.Text(dc, $"전체 {log.Count}건 중 최근 {shown}건", area.X, area.Bottom - 11, 9.5,
                HudPalette.TextDim, Ppd);
        }
    }

    private static Brush ColorOf(string kind) => kind switch
    {
        "센서" => HudPalette.Warn,
        "범위" => HudPalette.Accent,
        _ => HudPalette.Foreground
    };
}
