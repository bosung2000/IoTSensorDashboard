using System.Globalization;
using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.Core.Formatting;
using IoTSensorDashboard.Dashboard.Model;
using IoTSensorDashboard.Ui.Common.Rendering;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Dashboard.Views;

/// <summary>
/// 조직 전체를 <b>그림 한 장</b>으로 — 본사 · 본부 · 매장.
///
/// 🔑 <b>왜 표가 아니라 그림인가</b>: 표는 「어느 매장이 나쁜가」에 답하지만
///    <b>「어느 갈래가 통째로 나쁜가」</b>에는 답하지 못한다. 한 본부의 매장이 전부
///    노란색이면 그건 매장 6곳의 문제가 아니라 <b>그 본부 회선·브로커</b>의 문제다.
///    그 판단은 배치로만 즉시 보인다.
///
/// 📌 권한 범위(I4)를 그대로 따른다 — 매장 한 곳만 볼 권한이면 노드도 하나만 그린다.
///    「전체 그림」이라고 못 볼 것을 보여 주면 안 된다.
/// </summary>
public sealed class TopologyView : HudPanel
{
    public TopologyView()
    {
        Title = "사이트 종합 상황";
    }

    protected override void RenderContent(DrawingContext dc, Rect area)
    {
        HudDraw.Text(dc, $"권한 스코프(I4): {Snapshot.ScopeLabel}", area.X, area.Y - 14, 10.5,
            HudPalette.Accent, Ppd, maxWidth: area.Width * 0.5);

        var groups = Snapshot.Groups;
        if (groups.Count == 0)
        {
            HudDraw.Text(dc, "표시할 사이트가 없습니다", area.X, area.Y + 20, 11.5, HudPalette.TextDim, Ppd);
            return;
        }

        var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);

        DrawBackdrop(dc, center, area);

        // 본부를 좌우로 벌린다. 하나뿐이면 중앙 바로 옆에 붙인다.
        double spread = Math.Min(area.Width * 0.29, 240);
        double hubRadius = Math.Min(area.Height * 0.19, 46);

        if (hubRadius < 16) return;

        var storesByGroup = Snapshot.Stores
            .GroupBy(s => s.GroupId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        for (int i = 0; i < groups.Count; i++)
        {
            // 좌 · 우 교대 배치 (3개 이상이면 세로로도 벌린다)
            double side = i % 2 == 0 ? -1 : 1;
            double tier = i / 2;
            var hub = new Point(
                center.X + side * spread,
                center.Y + (tier == 0 ? 0 : tier * hubRadius * 2.6 * (i % 4 < 2 ? -1 : 1)));

            dc.DrawLine(HudPalette.Soft, center, hub);

            DrawStores(dc, hub, side, area, storesByGroup.GetValueOrDefault(groups[i].Id) ?? []);
            DrawHub(dc, hub, groups[i]);
        }

        DrawHeadquarters(dc, center, Math.Min(area.Height * 0.24, 62));
        DrawLegend(dc, area);
    }

    /// <summary>배경 육각 링 — 그림이 「빈 공간에 떠 있지」 않게 잡아 준다.</summary>
    private static void DrawBackdrop(DrawingContext dc, Point center, Rect area)
    {
        double r = Math.Min(area.Width, area.Height) * 0.42;
        if (r < 20) return;

        for (int ring = 0; ring < 2; ring++)
        {
            double rr = r * (1 - ring * 0.22);
            var geometry = new StreamGeometry();

            using (var ctx = geometry.Open())
            {
                var pts = new Point[6];
                for (int i = 0; i < 6; i++)
                {
                    double a = Math.PI / 3 * i - Math.PI / 2;
                    pts[i] = new Point(center.X + rr * Math.Cos(a), center.Y + rr * Math.Sin(a) * 0.86);
                }

                ctx.BeginFigure(pts[0], isFilled: false, isClosed: true);
                for (int i = 1; i < 6; i++) ctx.LineTo(pts[i], isStroked: true, isSmoothJoin: false);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, HudPalette.Faint, geometry);
        }
    }

    private void DrawHeadquarters(DrawingContext dc, Point center, double radius)
    {
        if (radius < 18) return;

        // 바깥 후광 — 여기가 중심이라는 것을 형태로 말한다.
        var halo = HudPalette.FrozenBrush(0x1A, 0x2E, 0x74, 0xFF);
        dc.DrawEllipse(halo, null, center, radius * 1.34, radius * 1.34);

        var pen = new Pen(HudPalette.Accent, 1.6);
        pen.Freeze();
        dc.DrawEllipse(HudPalette.FrozenBrush(0xFF, 0x0E, 0x1A, 0x2E), pen, center, radius, radius);

        HudDraw.TextFit(dc, "본사", center.X, center.Y - radius * 0.56, 12, 8, radius * 1.6,
            HudPalette.Accent, Ppd, HudDraw.Weight.Semi, HudDraw.Align.Center);

        HudDraw.TextFit(dc, Snapshot.TotalIn.ToString("N0", CultureInfo.CurrentCulture),
            center.X, center.Y - radius * 0.2, 21, 10, radius * 1.72,
            HudPalette.Foreground, Ppd, HudDraw.Weight.Heavy, HudDraw.Align.Center);

        HudDraw.TextFit(dc, "누적 유입", center.X, center.Y + radius * 0.3, 9.5, 7, radius * 1.6,
            HudPalette.TextDim, Ppd, align: HudDraw.Align.Center);

        HudDraw.TextFit(dc, CompactNumber.Format(Snapshot.TotalOut) + " 유출",
            center.X, center.Y + radius * 0.52, 10.5, 7.5, radius * 1.72,
            HudPalette.Out, Ppd, align: HudDraw.Align.Center);
    }

    private void DrawHub(DrawingContext dc, Point hub, GroupStat group)
    {
        const double W = 108;
        const double H = 46;

        var rect = new Rect(hub.X - W / 2, hub.Y - H / 2, W, H);

        var color = group.Uptime switch
        {
            null => HudPalette.Unknown,
            >= 0.999 => HudPalette.In,
            >= 0.95 => HudPalette.Warn,
            _ => HudPalette.Down
        };

        var pen = new Pen(color, 1.4);
        pen.Freeze();

        dc.DrawRoundedRectangle(HudPalette.FrozenBrush(0xFF, 0x10, 0x14, 0x1B), pen, rect, 10, 10);

        HudDraw.TextFit(dc, group.Name, hub.X, hub.Y - 15, 12, 8.5, W - 12,
            HudPalette.Foreground, Ppd, HudDraw.Weight.Semi, HudDraw.Align.Center);

        HudDraw.TextFit(dc, $"{group.Online:N0}/{group.Total:N0}", hub.X, hub.Y + 1, 14, 9, W - 12,
            color, Ppd, HudDraw.Weight.Heavy, HudDraw.Align.Center);
    }

    /// <summary>본부 바깥쪽으로 매장 노드를 부채꼴로 건다.</summary>
    private void DrawStores(DrawingContext dc, Point hub, double side, Rect area, List<StoreStat> stores)
    {
        if (stores.Count == 0) return;

        double radius = Math.Min(area.Height * 0.36, 150);
        if (radius < 40) return;

        // 부채꼴 각도 — 바깥쪽(중앙의 반대편)으로 편다.
        double baseAngle = side < 0 ? Math.PI : 0;
        double span = Math.PI * 0.82;

        for (int i = 0; i < stores.Count; i++)
        {
            double t = stores.Count == 1 ? 0.5 : (double)i / (stores.Count - 1);
            double angle = baseAngle - span / 2 + span * t;

            var at = new Point(
                hub.X + radius * Math.Cos(angle) * side * -1,
                hub.Y + radius * Math.Sin(angle) * 0.82);

            // 점선 — 실선으로 그리면 연결선이 노드보다 눈에 띈다.
            var dash = new Pen(HudPalette.FrozenBrush(0x40, 0x8A, 0x93, 0xA2), 1)
            {
                DashStyle = new DashStyle([2, 3], 0)
            };
            dash.Freeze();
            dc.DrawLine(dash, hub, at);

            var store = stores[i];
            var color = store.Total == 0
                ? HudPalette.Unknown
                : store.Online == 0
                    ? HudPalette.Down
                    : store.Online < store.Total
                        ? HudPalette.Warn
                        : HudPalette.In;

            var ring = new Pen(color, 1.6);
            ring.Freeze();

            dc.DrawEllipse(HudPalette.FrozenBrush(0x30, 0x5E, 0xEA, 0xD4), null, at, 11, 11);
            dc.DrawEllipse(HudPalette.FrozenBrush(0xFF, 0x0C, 0x10, 0x16), ring, at, 7.5, 7.5);
            dc.DrawEllipse(color, null, at, 3, 3);

            // 이름은 바깥쪽에 — 안쪽에 쓰면 연결선·본부 상자와 겹친다.
            double labelY = at.Y + (Math.Sin(angle) >= 0 ? 14 : -26);

            HudDraw.TextFit(dc, store.Name, at.X, labelY, 10.5, 8, 74,
                HudPalette.Foreground, Ppd, HudDraw.Weight.Semi, HudDraw.Align.Center);
        }
    }

    private void DrawLegend(DrawingContext dc, Rect area)
    {
        double y = area.Bottom - 12;
        double x = area.Right;

        (string Label, Brush Color)[] items =
        [
            ("측정 불가", HudPalette.Unknown),
            ("일부 오프라인", HudPalette.Warn),
            ("정상", HudPalette.In),
        ];

        foreach (var (label, color) in items)
        {
            var ft = HudDraw.Text(dc, label, x, y, 9.5, HudPalette.TextMuted, Ppd, align: HudDraw.Align.Right);
            x -= ft.WidthIncludingTrailingWhitespace + 8;

            dc.DrawEllipse(color, null, new Point(x, y + 6), 3.5, 3.5);
            x -= 12;
        }
    }
}
