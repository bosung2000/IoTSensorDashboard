using System.Globalization;
using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.Ui.Common.Rendering;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Dashboard.Views;

/// <summary>
/// 최근 추이 — 분 단위 막대(유입 · 유출 쌍).
///
/// 🔑 <b>버킷은 발생 시각으로 자른다</b>(도착 시각이 아니라, I3).
///    네트워크가 밀려 늦게 온 데이터를 「지금」 칸에 넣으면, 실제로는 3분 전에 있었던 일이
///    지금 일어난 것처럼 그려진다 — 그 그래프를 보고 사람이 <b>지금 조치를 결정한다</b>.
///
/// 📌 축 라벨을 <b>현지 시각</b>으로 쓴다. 저장은 UTC 지만 화면은 사람이 읽는 시계여야 한다.
/// </summary>
public sealed class HourlyChartPanel : HudPanel
{
    public HourlyChartPanel()
    {
        Title = "최근 추이 (실시간 · 분 단위)";
    }

    protected override void RenderContent(DrawingContext dc, Rect area)
    {
        DrawLegend(dc, area);

        var points = Snapshot.Minutes;

        if (points.Count == 0)
        {
            HudDraw.Text(dc, "아직 집계된 구간이 없습니다", area.X, area.Y + 12, 11,
                HudPalette.TextDim, Ppd);
            return;
        }

        var plot = new Rect(area.X, area.Y + 8, area.Width, Math.Max(0, area.Height - 26));
        if (plot.Height < 12) return;

        long peak = 1;
        foreach (var p in points) peak = Math.Max(peak, Math.Max(p.In, p.Out));

        // 바닥선 — 막대가 없는 구간도 「축은 있다」가 보인다.
        dc.DrawLine(HudPalette.Soft, new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Bottom));

        // 막대 폭은 자리 수에 맞춰 줄이되, 너무 얇아지면 최신 것부터 남긴다.
        double slot = plot.Width / points.Count;
        int start = 0;

        if (slot < 6)
        {
            int keep = Math.Max(1, (int)(plot.Width / 6));
            start = Math.Max(0, points.Count - keep);
            slot = plot.Width / (points.Count - start);
        }

        double barWidth = Math.Max(2, Math.Min(9, slot / 2 - 1));

        for (int i = start; i < points.Count; i++)
        {
            var p = points[i];
            double cx = plot.X + slot * (i - start + 0.5);

            double inHeight = plot.Height * ((double)p.In / peak);
            double outHeight = plot.Height * ((double)p.Out / peak);

            dc.DrawRectangle(HudPalette.In, null,
                new Rect(cx - barWidth - 1, plot.Bottom - inHeight, barWidth, inHeight));

            dc.DrawRectangle(HudPalette.Out, null,
                new Rect(cx + 1, plot.Bottom - outHeight, barWidth, outHeight));
        }

        DrawAxis(dc, plot, points, start, slot);
    }

    private void DrawAxis(
        DrawingContext dc, Rect plot,
        IReadOnlyList<Model.MinutePoint> points, int start, double slot)
    {
        // 라벨이 겹치지 않을 간격만 찍는다 — 겹친 글자는 없는 것만 못하다.
        int visible = points.Count - start;
        int step = Math.Max(1, (int)Math.Ceiling(38.0 / Math.Max(1, slot)));

        for (int i = start; i < points.Count; i += step)
        {
            double cx = plot.X + slot * (i - start + 0.5);

            HudDraw.Text(dc, points[i].Bucket.ToString("HH:mm", CultureInfo.CurrentCulture),
                cx, plot.Bottom + 4, 9, HudPalette.TextDim, Ppd, align: HudDraw.Align.Center);
        }

        _ = visible;
    }

    private void DrawLegend(DrawingContext dc, Rect area)
    {
        double x = area.Right;

        x -= HudDraw.Text(dc, "유출", x, area.Y - 12, 9.5, HudPalette.TextMuted, Ppd,
            align: HudDraw.Align.Right).WidthIncludingTrailingWhitespace + 5;

        dc.DrawRectangle(HudPalette.Out, null, new Rect(x - 8, area.Y - 9, 8, 8));
        x -= 15;

        x -= HudDraw.Text(dc, "유입", x, area.Y - 12, 9.5, HudPalette.TextMuted, Ppd,
            align: HudDraw.Align.Right).WidthIncludingTrailingWhitespace + 5;

        dc.DrawRectangle(HudPalette.In, null, new Rect(x - 8, area.Y - 9, 8, 8));
    }
}
