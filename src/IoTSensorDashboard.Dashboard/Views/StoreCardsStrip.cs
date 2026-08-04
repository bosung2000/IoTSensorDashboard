using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.Core.Formatting;
using IoTSensorDashboard.Dashboard.Model;
using IoTSensorDashboard.Ui.Common.Rendering;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Dashboard.Views;

/// <summary>
/// 매장 카드 띠 — 매장마다 <b>지금 속도</b>(유입/s · 유출/s)를 한 칸에.
///
/// 🔑 <b>누적이 아니라 속도를 보여 준다.</b> 누적은 하루가 갈수록 커지기만 해서
///    「지금 무슨 일이 벌어지고 있는가」에 답하지 못한다. 관제 화면이 답해야 하는 건
///    <b>「지금」</b>이고, 그건 속도다.
/// </summary>
public sealed class StoreCardsStrip : HudPanel
{
    public StoreCardsStrip()
    {
        Chromeless = true;
    }

    protected override void RenderContent(DrawingContext dc, Rect area)
    {
        var stores = Snapshot.Stores;
        if (stores.Count == 0) return;

        // 🔑 추이는 **Id 로** 찾는다. 인덱스가 맞을 것이라 가정하면
        //    한쪽만 정렬이 바뀌는 날 다른 매장의 숫자가 다른 매장 카드에 붙는다.
        var trendById = Snapshot.Trends.ToDictionary(t => t.Id, StringComparer.Ordinal);

        // 카드가 이보다 좁아지면 매장 이름조차 안 들어간다 → 들어갈 만큼만 그리고
        // 나머지는 「+N」으로 정직하게 남긴다(말없이 자르지 않는다).
        const double MinCard = 92;

        int fits = Math.Max(1, (int)((area.Width + 6) / MinCard));
        bool overflow = stores.Count > fits;
        int shown = overflow ? Math.Max(1, fits - 1) : stores.Count;

        double cardWidth = (area.Width + 6) / (overflow ? fits : shown) - 6;

        for (int i = 0; i < shown; i++)
        {
            var rect = new Rect(area.X + i * (cardWidth + 6), area.Y, cardWidth, area.Height);
            DrawCard(dc, rect, stores[i], trendById.GetValueOrDefault(stores[i].Id));
        }

        if (!overflow) return;

        var more = new Rect(area.X + shown * (cardWidth + 6), area.Y, cardWidth, area.Height);
        if (more.Width < 20) return;

        dc.DrawRoundedRectangle(HudPalette.Panel, HudPalette.Soft, more, 6, 6);

        HudDraw.TextFit(dc, $"+{stores.Count - shown}", more.X + more.Width / 2, more.Y + more.Height / 2 - 11,
            16, 10, more.Width - 8, HudPalette.TextMuted, Ppd, HudDraw.Weight.Heavy, HudDraw.Align.Center);

        HudDraw.TextFit(dc, "매장 더 있음", more.X + more.Width / 2, more.Y + more.Height / 2 + 8,
            9, 7, more.Width - 6, HudPalette.TextDim, Ppd, align: HudDraw.Align.Center);
    }

    private void DrawCard(DrawingContext dc, Rect rect, StoreStat store, StoreTrend? trend)
    {
        dc.DrawRoundedRectangle(HudPalette.Panel, HudPalette.Base, rect, 6, 6);

        // 왼쪽 세로 띠 = 이 매장의 건강 상태. 색 하나로 카드 전체의 성격이 정해진다.
        var status = HealthColors.Of(store.Health);

        dc.DrawRoundedRectangle(status, null, new Rect(rect.X, rect.Y + 6, 3, rect.Height - 12), 1.5, 1.5);

        double x = rect.X + 10;
        double innerWidth = rect.Width - 20;

        if (innerWidth < 10) return;

        HudDraw.TextFit(dc, store.Name, x, rect.Y + 7, 12, 8.5, innerWidth,
            HudPalette.Foreground, Ppd, HudDraw.Weight.Semi);

        // 오른쪽 위 상태 점 — 이름이 길어 띠를 못 봤을 때의 두 번째 신호.
        dc.DrawEllipse(status, null, new Point(rect.Right - 9, rect.Y + 11), 3.5, 3.5);

        // 🔑 마지막 표본이 아니라 **최근 평균**이다 — 정수 이벤트라 표본 하나는 널뛴다.
        double inRate = trend?.RecentIn ?? 0;
        double outRate = trend?.RecentOut ?? 0;

        DrawRate(dc, x, rect.Y + 26, innerWidth, "유입", inRate, HudPalette.In);
        DrawRate(dc, x, rect.Y + 42, innerWidth, "유출", outRate, HudPalette.Out);
    }

    private void DrawRate(DrawingContext dc, double x, double y, double width, string label, double rate, Brush color)
    {
        var lbl = HudDraw.Text(dc, label, x, y + 2, 9.5, HudPalette.TextDim, Ppd);

        HudDraw.TextFit(dc, $"{RateText.Format(rate)}/s", x + width, y, 13, 9,
            Math.Max(10, width - lbl.WidthIncludingTrailingWhitespace - 4),
            color, Ppd, HudDraw.Weight.Semi, HudDraw.Align.Right);
    }
}
