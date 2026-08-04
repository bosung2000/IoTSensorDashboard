using System.Globalization;
using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.ControlRoom.Model;
using IoTSensorDashboard.Ui.Common.Rendering;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.ControlRoom.Views;

/// <summary>
/// 감지 피드 — 파이프라인이 <b>스스로 하는 말</b>.
///
/// 🔑 숫자 카드는 「지금 얼마」에 답하지만 <b>「방금 무슨 일이 있었나」</b>에는 답하지 못한다.
///    브로커가 끊겼다 붙었는지, 과부하로 버렸는지, 정합이 깨졌는지는 <b>순간의 사건</b>이라
///    다음 틱이면 숫자에서 사라진다. 그 순간을 남기는 자리다.
///
/// 🔴 <b>「없음」이 정상으로 읽히는 화면</b>이므로 실패를 삼키지 않는다.
///    피드가 비어 있으면 「조용하다」가 아니라 「기록이 없다」일 수도 있다.
/// </summary>
public sealed class EventFeedView : RenderPanel
{
    private PipelineSnapshot _snapshot = PipelineSnapshot.Empty();

    public EventFeedView()
    {
        Title = "감지 피드";
    }

    public void Update(PipelineSnapshot snapshot)
    {
        _snapshot = snapshot ?? PipelineSnapshot.Empty();
        Redraw();
    }

    protected override void RenderContent(DrawingContext dc, Rect area)
    {
        var feed = _snapshot.Feed;

        // 살아 있음 표시 — 피드가 도는 중인지 굳었는지 구별하게.
        HudDraw.Text(dc, "LIVE", area.Right, area.Y - 24, 9.5, HudPalette.In, Ppd,
            HudDraw.Weight.Semi, HudDraw.Align.Right);

        if (feed.Count == 0)
        {
            HudDraw.Text(dc, "아직 기록된 사건이 없습니다", area.X, area.Y + 4, 11,
                HudPalette.TextDim, Ppd);
            return;
        }

        const double RowHeight = 18;

        double timeWidth = 56;
        double messageX = area.X + timeWidth;
        double messageWidth = Math.Max(20, area.Right - messageX);

        double y = area.Y;
        int shown = 0;

        foreach (var line in feed)
        {
            if (y + RowHeight > area.Bottom) break;

            HudDraw.Text(dc, line.At.ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                area.X, y, 10.5, HudPalette.TextDim, Ppd);

            var color = line.Level switch
            {
                FeedLevel.Error => HudPalette.Down,
                FeedLevel.Warn => HudPalette.Warn,
                _ => HudPalette.Foreground
            };

            dc.DrawEllipse(color, null, new Point(messageX - 8, y + 7), 3, 3);

            HudDraw.Text(dc, line.Message, messageX, y, 11, color, Ppd, maxWidth: messageWidth);

            y += RowHeight;
            shown++;
        }

        // 잘랐으면 잘랐다고 쓴다 — 「최근 N건」이 아니라 「전체 M건 중 N건」이다.
        if (feed.Count > shown && shown > 0)
        {
            HudDraw.Text(dc, $"전체 {feed.Count}건 중 최근 {shown}건", area.X, area.Bottom - 11, 9.5,
                HudPalette.TextDim, Ppd);
        }
    }
}
