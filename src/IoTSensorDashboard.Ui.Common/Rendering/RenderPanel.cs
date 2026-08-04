using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Ui.Common.Rendering;

/// <summary>
/// 직접 그리는 패널의 공통 뼈대 — 앱과 무관한 부분만.
///
/// 🔑 <b><see cref="FrameworkElement"/> 를 상속하는 이유</b>: <see cref="System.Windows.Controls.Control"/> 은
///    템플릿·스타일 기계를 통째로 끌고 온다. 우리는 <see cref="OnRender"/> 하나만 쓰므로
///    그 무게가 전부 낭비다. 패널이 10장이고 33ms 마다 갱신되니 이 차이가 쌓인다.
///
/// 🔒 <b>클리핑을 켠다</b>(<see cref="UIElement.ClipToBounds"/>). 직접 그리기는 자기 경계를
///    자동으로 지켜 주지 않는다 — 계산이 틀리면 <b>옆 패널 위에 그려진다</b>.
///    그러면 원인 패널이 아니라 침범당한 패널이 고장 나 보여 진단이 엉뚱한 데로 간다.
/// </summary>
public abstract class RenderPanel : FrameworkElement
{
    protected RenderPanel()
    {
        ClipToBounds = true;
    }

    /// <summary>패널 제목. 비어 있으면 제목 줄 없이 여백만 준다.</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// <c>true</c> 면 배경·테두리를 그리지 않는다 — 다른 카드 <b>안에</b> 구획으로 넣을 때.
    /// 카드 안에 카드를 그리면 테두리가 두 겹이 되어 지저분해진다.
    /// </summary>
    public bool Chromeless { get; set; }

    /// <summary>화면 배율 — 텍스트 캐시 키에 들어간다.</summary>
    protected double Ppd => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// <summary>제목 왼쪽 액센트 막대의 색. <c>null</c> 이면 막대 없음.</summary>
    protected virtual Brush? Accent => HudPalette.Accent;

    /// <summary>
    /// 다시 그리라고 표시한다.
    ///
    /// 🔑 <see cref="UIElement.InvalidateVisual"/> 는 <b>지금 그리라는 명령이 아니라</b>
    ///    「다음 렌더 차례에 다시 그려라」는 표시다. 한 틱에 열 번 불러도 렌더는 한 번이다.
    /// </summary>
    protected void Redraw() => InvalidateVisual();

    protected sealed override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);

        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;

        // 레이아웃이 아직 안 잡혔거나 사용자가 접어 버린 상태.
        // 여기서 막지 않으면 아래 계산이 음수 폭을 만들어 WPF 가 던진다.
        if (w < 8 || h < 8) return;

        var area = Title.Length > 0
            ? HudDraw.Frame(dc, w, h, Title, Ppd, !Chromeless, Accent)
            : Bare(dc, w, h);

        if (area.Width < 4 || area.Height < 4) return;

        RenderContent(dc, area);
    }

    /// <summary>제목이 없을 때 — 테두리만 그리고 여백을 준다.</summary>
    private Rect Bare(DrawingContext dc, double w, double h)
    {
        if (!Chromeless)
            dc.DrawRoundedRectangle(HudPalette.Panel, HudPalette.Base, new Rect(0, 0, w, h), 8, 8);

        return new Rect(12, 12, Math.Max(0, w - 24), Math.Max(0, h - 24));
    }

    /// <summary><paramref name="area"/> 안에만 그린다.</summary>
    protected abstract void RenderContent(DrawingContext dc, Rect area);
}
