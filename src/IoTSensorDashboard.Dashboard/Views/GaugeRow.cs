using System.Globalization;
using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.Dashboard.Model;
using IoTSensorDashboard.Ui.Common.Rendering;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Dashboard.Views;

/// <summary>
/// 그룹 가동률 — 도넛 게이지.
///
/// 🔴 <b>분모가 0 이면 「측정 불가」로 그린다. 100% 가 아니다.</b>
///    이 패널이 이 프로젝트에서 가장 비싼 거짓말을 할 수 있는 자리다 —
///    장애 기록이 없다는 이유로 만점을 그리면, <b>처음부터 죽어 있던 곳</b>과
///    <b>지켜보지 못한 기간</b>이 「완벽했다」로 둔갑한다.
///    화면이 스스로 구매 판단 근거라고 말하는 숫자일수록 틀렸을 때 대가가 크다.
/// </summary>
public sealed class GaugeRow : HudPanel
{
    public GaugeRow()
    {
        Title = "그룹 가동률 (I5)";
    }

    protected override void RenderContent(DrawingContext dc, Rect area)
    {
        // 전체를 맨 앞에 둔다 — 먼저 보는 것이 「우리 전체가 괜찮은가」이므로.
        var cells = new List<(string Name, int Online, int Total)>
        {
            ("전체", Snapshot.OnlineSensors, Snapshot.TotalSensors)
        };

        foreach (var g in Snapshot.Groups)
            cells.Add((g.Name, g.Online, g.Total));

        if (cells.Count == 0) return;

        // 도넛이 이보다 작아지면 가운데 숫자를 못 읽는다 → 들어갈 수 있는 만큼만 그린다.
        const double MinCell = 74;

        int fits = Math.Max(1, (int)(area.Width / MinCell));
        if (cells.Count > fits) cells = cells.Take(fits).ToList();

        double cellWidth = area.Width / cells.Count;
        double radius = Math.Min(cellWidth * 0.36, (area.Height - 34) / 2);

        if (radius < 12) return;

        for (int i = 0; i < cells.Count; i++)
        {
            var (name, online, total) = cells[i];
            var center = new Point(area.X + cellWidth * (i + 0.5), area.Y + radius + 4);

            DrawGauge(dc, center, radius, name, online, total);
        }
    }

    private void DrawGauge(DrawingContext dc, Point center, double radius, string name, int online, int total)
    {
        // 관측 못 한 것은 비율을 만들지 않는다.
        double? ratio = total > 0 ? (double)online / total : null;

        var color = ratio switch
        {
            null => HudPalette.Unknown,
            >= 0.999 => HudPalette.In,
            >= 0.95 => HudPalette.Warn,
            _ => HudPalette.Down
        };

        // 바탕 링 — 남은 양(=아직 안 채운 부분)이 보여야 비율로 읽힌다.
        var trackPen = new Pen(HudPalette.FrozenBrush(0x22, 0xFF, 0xFF, 0xFF), 7);
        trackPen.Freeze();
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        if (ratio is double r && r > 0)
        {
            var pen = new Pen(color, 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, Arc(center, radius, r));
        }

        // 가운데 숫자 — 「측정 불가」는 숫자 자리에 그대로 글자로 쓴다.
        if (ratio is double pct)
        {
            HudDraw.TextFit(dc, (pct * 100).ToString("F2", CultureInfo.CurrentCulture),
                center.X, center.Y - 13, 15, 9, radius * 1.5, color, Ppd,
                HudDraw.Weight.Heavy, HudDraw.Align.Center);

            HudDraw.Text(dc, "%", center.X, center.Y + 3, 9, HudPalette.TextMuted, Ppd,
                align: HudDraw.Align.Center);
        }
        else
        {
            HudDraw.TextFit(dc, "측정 불가", center.X, center.Y - 6, 10.5, 8, radius * 1.7,
                HudPalette.Unknown, Ppd, HudDraw.Weight.Semi, HudDraw.Align.Center);
        }

        HudDraw.TextFit(dc, name, center.X, center.Y + radius + 6, 11, 8.5, radius * 2.4,
            HudPalette.Foreground, Ppd, HudDraw.Weight.Semi, HudDraw.Align.Center);

        // 🔑 N/M 을 같이 적는다 — 비율만 보여 주면 **분모가 무엇인지** 알 수 없고,
        //    분모를 모르는 비율은 믿을 근거가 없다.
        HudDraw.TextFit(dc, $"{online:N0}/{total:N0}", center.X, center.Y + radius + 19, 9.5, 7.5,
            radius * 2.4, HudPalette.TextDim, Ppd, align: HudDraw.Align.Center);
    }

    /// <summary>12시에서 시계방향으로 <paramref name="ratio"/> 만큼의 호.</summary>
    private static Geometry Arc(Point center, double radius, double ratio)
    {
        double sweep = Math.Clamp(ratio, 0, 1) * 2 * Math.PI;

        var start = new Point(center.X, center.Y - radius);
        var end = new Point(
            center.X + radius * Math.Sin(sweep),
            center.Y - radius * Math.Cos(sweep));

        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);

            // ⚠️ 100% 는 시작점과 끝점이 같아진다. 호는 「어느 쪽으로 도는지」를
            //    두 점으로 정하므로, 같은 점이면 WPF 가 **아무것도 그리지 않는다**.
            //    그래서 완전한 원은 반원 두 개로 나눠 그린다.
            if (ratio >= 1)
            {
                var half = new Point(center.X, center.Y + radius);
                ctx.ArcTo(half, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                ctx.ArcTo(start, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            else
            {
                ctx.ArcTo(end, new Size(radius, radius), 0, sweep > Math.PI,
                    SweepDirection.Clockwise, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }
}
