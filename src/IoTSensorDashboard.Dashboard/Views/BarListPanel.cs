using System.Globalization;
using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.Dashboard.Model;
using IoTSensorDashboard.Ui.Common.Rendering;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Dashboard.Views;

/// <summary>
/// 센서 상위 5개 — 가로 막대 순위.
///
/// 🔑 <b>막대가 숫자보다 먼저 읽힌다.</b> 45·43·41·41·41 을 숫자로만 늘어놓으면
///    「비슷하다」를 읽는 데 시간이 걸리지만, 막대는 <b>한눈에</b> 알려 준다.
///    반대로 1위가 2위의 세 배인 상황도 즉시 보인다 — 그게 이상 징후일 때가 많다.
///
/// 📌 한 클래스로 세 자리(처리량·유입·유출)를 모두 채운다.
///    같은 모양을 세 벌 만들면 나중에 한 벌만 고쳐 놓고 나머지를 잊는다.
/// </summary>
public sealed class BarListPanel : HudPanel
{
    /// <summary>무엇으로 줄 세울 것인가.</summary>
    public enum Metric
    {
        Throughput,
        In,
        Out
    }

    /// <summary>이 패널이 보여줄 지표. XAML 에서 지정한다.</summary>
    public Metric Mode { get; set; } = Metric.Throughput;

    protected override Brush? Accent => Mode switch
    {
        Metric.In => HudPalette.In,
        Metric.Out => HudPalette.Out,
        _ => HudPalette.Accent
    };

    protected override void RenderContent(DrawingContext dc, Rect area)
    {
        var (items, value, color) = Select();

        if (items.Count == 0)
        {
            HudDraw.Text(dc, "아직 집계된 센서가 없습니다", area.X, area.Y + 8, 11.5,
                HudPalette.TextDim, Ppd);
            return;
        }

        // 🔑 축 상한은 **1위 값**이다. 전체 합으로 나누면 막대가 전부 짧아져
        //    순위 간 차이가 안 보인다 — 이 패널의 질문은 「전체 중 몇 %」가 아니라
        //    「서로 얼마나 차이 나는가」다.
        long peak = Math.Max(1, value(items[0]));

        double rowHeight = Math.Min(30, area.Height / items.Count);
        double y = area.Y;

        // 값이 들어갈 자리를 먼저 떼어 둔다 — 막대가 숫자를 덮으면 둘 다 못 읽는다.
        double valueWidth = 52;
        double barWidth = Math.Max(20, area.Width - valueWidth - 8);

        foreach (var item in items)
        {
            if (y + rowHeight > area.Bottom + 2) break;

            long v = value(item);

            HudDraw.Text(dc, item.SensorId, area.X, y, 11.5, HudPalette.Foreground, Ppd,
                maxWidth: barWidth * 0.62);

            HudDraw.Text(dc, item.StoreName, area.X, y + 12, 9.5, HudPalette.TextDim, Ppd,
                maxWidth: barWidth * 0.62);

            var track = new Rect(area.X + barWidth * 0.64, y + 6, barWidth * 0.36, 8);

            // 트랙(빈 홈)을 먼저 — 0 인 항목도 「자리는 있다」가 보인다.
            dc.DrawRoundedRectangle(HudPalette.FrozenBrush(0x18, 0xFF, 0xFF, 0xFF), null, track, 3, 3);
            HudDraw.Bar(dc, track, (double)v / peak, color);

            HudDraw.Text(dc, v.ToString("N0", CultureInfo.CurrentCulture), area.Right, y + 2, 12.5,
                HudPalette.Foreground, Ppd, HudDraw.Weight.Semi, HudDraw.Align.Right);

            y += rowHeight;
        }
    }

    private (IReadOnlyList<SensorStat> Items, Func<SensorStat, long> Value, Brush Color) Select() => Mode switch
    {
        Metric.In => (Snapshot.TopIn, static s => s.In, HudPalette.In),
        Metric.Out => (Snapshot.TopOut, static s => s.Out, HudPalette.Out),
        _ => (Snapshot.TopThroughput, static s => s.Throughput, HudPalette.Accent)
    };
}
