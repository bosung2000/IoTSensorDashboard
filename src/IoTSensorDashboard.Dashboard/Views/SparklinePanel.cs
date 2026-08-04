using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.Core.Formatting;
using IoTSensorDashboard.Dashboard.Model;
using IoTSensorDashboard.Ui.Common.Rendering;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Dashboard.Views;

/// <summary>
/// 주요 매장의 <b>트래픽 추이</b> — 매장마다 미니 그래프 한 줄.
///
/// 🔑 <b>왜 큰 숫자 옆에 이게 필요한가</b>: 「지금 29/s」는 그 값이 <b>높은지 낮은지</b>를
///    말해 주지 않는다. 방금까지 60/s 였다면 사고고, 계속 30 근처였다면 평소다.
///    <b>같은 숫자가 정반대 뜻</b>이 되므로 추이 없이는 판단할 수 없다.
///
/// 📌 세로 축은 <b>줄마다 따로</b> 잡는다(공통 축 아님). 매장 규모가 10배 차이나면
///    공통 축에서는 작은 매장이 전부 바닥에 붙어 <b>변화가 안 보인다</b> —
///    이 패널의 질문은 「어디가 큰가」가 아니라 「각자 평소와 다른가」다.
/// </summary>
public sealed class SparklinePanel : HudPanel
{
    /// <summary>한 화면에 보여줄 매장 수.</summary>
    private const int MaxRows = 5;

    public SparklinePanel()
    {
        Title = "주요 매장 트래픽 추이";
    }

    protected override void RenderContent(DrawingContext dc, Rect area)
    {
        DrawLegend(dc, area);

        // 유입이 많은 순 — 「주요」의 정의. 전부 0 이면 명부 순서가 그대로 남는다.
        var rows = Snapshot.Trends
            .OrderByDescending(t => t.RecentIn)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Take(MaxRows)
            .ToList();

        if (rows.Count == 0)
        {
            HudDraw.Text(dc, "아직 데이터가 없습니다", area.X, area.Y + 18, 11.5,
                HudPalette.TextDim, Ppd);
            return;
        }

        double top = area.Y + 16;
        double available = area.Bottom - top;
        double rowHeight = available / rows.Count;

        // 줄 높이가 이보다 얇으면 글자와 그래프가 겹친다 — 그럴 바엔 줄 수를 줄인다.
        if (rowHeight < 34)
        {
            int fits = Math.Max(1, (int)(available / 34));
            rows = rows.Take(fits).ToList();
            rowHeight = available / rows.Count;
        }

        foreach (var row in rows)
        {
            DrawRow(dc, new Rect(area.X, top, area.Width, rowHeight - 6), row);
            top += rowHeight;
        }
    }

    private void DrawLegend(DrawingContext dc, Rect area)
    {
        double x = area.Right;

        // 오른쪽 → 왼쪽으로 쌓는다. 폭이 줄어도 잘리는 쪽이 왼쪽(덜 중요한 순)이 된다.
        x -= HudDraw.Text(dc, "유출", x, area.Y, 10, HudPalette.TextMuted, Ppd, align: HudDraw.Align.Right)
            .WidthIncludingTrailingWhitespace + 6;

        dc.DrawRectangle(HudPalette.Out, null, new Rect(x - 8, area.Y + 3, 8, 8));
        x -= 16;

        x -= HudDraw.Text(dc, "유입", x, area.Y, 10, HudPalette.TextMuted, Ppd, align: HudDraw.Align.Right)
            .WidthIncludingTrailingWhitespace + 6;

        dc.DrawRectangle(HudPalette.In, null, new Rect(x - 8, area.Y + 3, 8, 8));
    }

    private void DrawRow(DrawingContext dc, Rect row, StoreTrend trend)
    {
        HudDraw.Text(dc, trend.Name, row.X, row.Y, 11.5, HudPalette.Foreground, Ppd,
            HudDraw.Weight.Semi, maxWidth: row.Width * 0.6);

        HudDraw.Text(dc, $"{RateText.Format(trend.RecentIn)}/s", row.Right, row.Y, 11.5, HudPalette.In, Ppd,
            HudDraw.Weight.Semi, HudDraw.Align.Right);

        var plot = new Rect(row.X, row.Y + 17, row.Width, Math.Max(0, row.Height - 17));
        if (plot.Height < 6) return;

        // 🔑 축 상한은 **두 계열을 합쳐** 잡는다. 따로 잡으면 유입 30·유출 3 인 매장에서
        //    둘 다 같은 높이로 그려져 「유입과 유출이 비슷하다」는 거짓 인상을 준다.
        double peak = Math.Max(Max(trend.In), Max(trend.Out));

        // 바닥 격자 — 값이 0 일 때도 「자리는 있다」를 보여준다(빈 칸과 구분).
        dc.DrawLine(HudPalette.Faint, new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Bottom));

        if (peak <= 0) return;

        DrawSeries(dc, plot, trend.Out, peak, HudPalette.Out, 0x33);
        DrawSeries(dc, plot, trend.In, peak, HudPalette.In, 0x40);
    }

    private static double Max(IReadOnlyList<double> values)
    {
        double max = 0;
        for (int i = 0; i < values.Count; i++)
            if (values[i] > max) max = values[i];

        return max;
    }

    /// <summary>
    /// 한 계열을 <b>면(area)</b> 으로 그린다 — 선만 그리면 겹쳤을 때 어느 쪽이 큰지 안 보인다.
    /// </summary>
    private static void DrawSeries(
        DrawingContext dc, Rect plot, IReadOnlyList<double> values, double peak, Brush stroke, byte fillAlpha)
    {
        if (values.Count < 2) return;

        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            double step = plot.Width / (values.Count - 1);

            Point At(int i) => new(
                plot.X + step * i,
                plot.Bottom - plot.Height * Math.Clamp(values[i] / peak, 0, 1));

            // 채움을 닫으려면 바닥에서 시작해 바닥으로 돌아와야 한다.
            ctx.BeginFigure(new Point(plot.X, plot.Bottom), isFilled: true, isClosed: true);

            for (int i = 0; i < values.Count; i++)
                ctx.LineTo(At(i), isStroked: false, isSmoothJoin: false);

            ctx.LineTo(new Point(plot.Right, plot.Bottom), isStroked: false, isSmoothJoin: false);
        }

        // 🔒 얼려서 넘긴다 — 매 프레임 만드는 지오메트리라 렌더 스레드가 복사하지 않게.
        geometry.Freeze();

        var solid = (SolidColorBrush)stroke;
        var fill = HudPalette.FrozenBrush(fillAlpha, solid.Color.R, solid.Color.G, solid.Color.B);

        dc.DrawGeometry(fill, null, geometry);

        // 윤곽선은 따로 — 면만 있으면 최근 값의 위치가 흐릿하다.
        var pen = new Pen(stroke, 1.4);
        pen.Freeze();

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            double step = plot.Width / (values.Count - 1);

            ctx.BeginFigure(
                new Point(plot.X, plot.Bottom - plot.Height * Math.Clamp(values[0] / peak, 0, 1)),
                isFilled: false, isClosed: false);

            for (int i = 1; i < values.Count; i++)
            {
                ctx.LineTo(
                    new Point(plot.X + step * i, plot.Bottom - plot.Height * Math.Clamp(values[i] / peak, 0, 1)),
                    isStroked: true, isSmoothJoin: true);
            }
        }

        line.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
