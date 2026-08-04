using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.Core.Formatting;
using IoTSensorDashboard.Dashboard.Model;
using IoTSensorDashboard.Ui.Common.Rendering;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Dashboard.Views;

/// <summary>
/// 매장별 상태 표.
///
/// 🔒 <b>목록의 출처는 프로비저닝 명부다</b> — 「데이터가 들어온 매장」이 아니다.
///    그래야 센서가 전부 죽은 매장도 목록에 남아 <b>「측정 불가」</b>로 보인다.
///    데이터 기준으로 만들면 그 매장은 <b>목록에서 통째로 사라지고</b>,
///    사라진 것은 아무도 못 찾는다.
/// </summary>
public sealed class SiteStatusPanel : HudPanel
{
    public SiteStatusPanel()
    {
        Title = "매장별 상태 현황";
    }

    protected override void RenderContent(DrawingContext dc, Rect area)
    {
        var stores = Snapshot.Stores;

        var trendById = Snapshot.Trends.ToDictionary(t => t.Id, StringComparer.Ordinal);

        // 컬럼 폭 — 비율로 잡아야 창을 줄여도 무너지지 않는다.
        double nameWidth = Math.Max(48, area.Width * 0.30);
        double colWidth = Math.Max(30, (area.Width - nameWidth - 16) / 3);

        double xOnline = area.X + nameWidth + colWidth;
        double xIn = xOnline + colWidth;
        double xOut = xIn + colWidth;

        HudDraw.Text(dc, "매장", area.X, area.Y, 10, HudPalette.TextDim, Ppd, maxWidth: nameWidth);
        HudDraw.Text(dc, "온라인", xOnline, area.Y, 10, HudPalette.TextDim, Ppd, align: HudDraw.Align.Right);
        HudDraw.Text(dc, "유입/s", xIn, area.Y, 10, HudPalette.TextDim, Ppd, align: HudDraw.Align.Right);
        HudDraw.Text(dc, "유출/s", xOut, area.Y, 10, HudPalette.TextDim, Ppd, align: HudDraw.Align.Right);

        double y = area.Y + 16;
        dc.DrawLine(HudPalette.Soft, new Point(area.X, y - 3), new Point(area.Right, y - 3));

        if (stores.Count == 0)
        {
            HudDraw.Text(dc, "표시할 매장이 없습니다", area.X, y + 4, 11, HudPalette.TextDim, Ppd);
            return;
        }

        const double RowHeight = 17;
        int fits = Math.Max(0, (int)((area.Bottom - y) / RowHeight));

        for (int i = 0; i < stores.Count && i < fits; i++)
        {
            var store = stores[i];
            var trend = trendById.GetValueOrDefault(store.Id);

            // 얼룩 배경 — 가로로 눈이 미끄러지지 않게. 표의 오독은 대부분 줄을 잘못 따라가서 난다.
            if (i % 2 == 1)
                dc.DrawRectangle(HudPalette.FrozenBrush(0x0C, 0xFF, 0xFF, 0xFF), null,
                    new Rect(area.X - 4, y - 2, area.Width + 8, RowHeight));

            HudDraw.Text(dc, store.Name, area.X, y, 11, HudPalette.Foreground, Ppd, maxWidth: nameWidth);

            var health = HealthColors.Of(store.Health);

            HudDraw.Text(dc, $"{store.Online:N0}/{store.Total:N0}", xOnline, y, 11, health, Ppd,
                HudDraw.Weight.Semi, HudDraw.Align.Right);

            double inRate = trend?.RecentIn ?? 0;
            double outRate = trend?.RecentOut ?? 0;

            // 🔴 정수 반올림 금지 — 매장당 1.4/s 를 0 으로 그리면 정상이 장애로 읽힌다.
            HudDraw.Text(dc, RateText.Format(inRate), xIn, y, 11,
                HudPalette.In, Ppd, align: HudDraw.Align.Right);

            HudDraw.Text(dc, RateText.Format(outRate), xOut, y, 11,
                HudPalette.Out, Ppd, align: HudDraw.Align.Right);

            dc.DrawEllipse(health, null, new Point(area.Right - 4, y + 7), 3, 3);

            y += RowHeight;
        }

        // 🔴 잘라서 보여줄 때는 **잘랐다는 사실**을 남긴다.
        //    「최근 N건」이 아니라 「전체 M건 중 N건」이어야 한다 —
        //    안 그러면 보이는 것이 전부인 줄 알게 된다.
        if (stores.Count > fits && fits > 0)
        {
            HudDraw.Text(dc, $"전체 {stores.Count}곳 중 {fits}곳 표시", area.X, area.Bottom - 11, 9.5,
                HudPalette.TextDim, Ppd);
        }
    }
}
