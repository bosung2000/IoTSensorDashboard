using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Ui.Common.Controls;

/// <summary>
/// HUD 패널 — 모서리를 자른 팔각형 틀.
///
/// <code>
///  ╱────────────────────────╲     ← Cut 만큼 모서리를 자른다
///  │  ▔▔▔ Sheen (위 2%)      │
///  │                        │
///  │       (콘텐츠)          │
///  ╲────────────────────────╱
///    └ Emphasis=True 면 네 모서리에 브래킷
/// </code>
///
/// <see cref="Fill"/> 의 알파로 <b>뒤가 비치는 정도</b>를 조절한다 —
/// 오버레이 카드는 뒤가 비쳐야 「떠 있는」 느낌이 난다.
/// </summary>
public sealed class HudFrame : ContentControl
{
    public static readonly DependencyProperty CutProperty =
        DependencyProperty.Register(nameof(Cut), typeof(double), typeof(HudFrame),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(HudFrame),
            new FrameworkPropertyMetadata(HudPalette.Panel, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmphasisProperty =
        DependencyProperty.Register(nameof(Emphasis), typeof(bool), typeof(HudFrame),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(HudFrame),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    static HudFrame()
    {
        // 기본 여백 — 콘텐츠가 테두리에 붙지 않게.
        PaddingProperty.OverrideMetadata(typeof(HudFrame),
            new FrameworkPropertyMetadata(new Thickness(10)));
    }

    /// <summary>모서리를 잘라내는 크기(px).</summary>
    public double Cut
    {
        get => (double)GetValue(CutProperty);
        set => SetValue(CutProperty, value);
    }

    /// <summary>배경. 알파를 낮추면 뒤가 비친다.</summary>
    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>강조 — 네 모서리에 브래킷을 그린다.</summary>
    public bool Emphasis
    {
        get => (bool)GetValue(EmphasisProperty);
        set => SetValue(EmphasisProperty, value);
    }

    /// <summary>상단 액센트 선. null 이면 안 그린다.</summary>
    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double cut = Math.Max(0, Math.Min(Cut, Math.Min(w, h) / 2));
        var outline = BuildOctagon(w, h, cut);

        dc.DrawGeometry(Fill, Emphasis ? HudPalette.Strong : HudPalette.Base, outline);

        // 상단 유리 하이라이트 — 위 2% 만.
        double sheenHeight = Math.Max(1, h * 0.02);
        dc.DrawRectangle(HudPalette.Sheen, null, new Rect(cut, 1, Math.Max(0, w - cut * 2), sheenHeight));

        if (Accent is { } accent)
            dc.DrawRectangle(accent, null, new Rect(cut, 0, Math.Max(0, w - cut * 2), 2));

        if (Emphasis) DrawCornerBrackets(dc, w, h, cut);

        base.OnRender(dc);
    }

    /// <summary>모서리를 잘라낸 팔각형.</summary>
    private static Geometry BuildOctagon(double w, double h, double cut)
    {
        var figure = new PathFigure { StartPoint = new Point(cut, 0), IsClosed = true, IsFilled = true };

        figure.Segments.Add(new LineSegment(new Point(w - cut, 0), true));
        figure.Segments.Add(new LineSegment(new Point(w, cut), true));
        figure.Segments.Add(new LineSegment(new Point(w, h - cut), true));
        figure.Segments.Add(new LineSegment(new Point(w - cut, h), true));
        figure.Segments.Add(new LineSegment(new Point(cut, h), true));
        figure.Segments.Add(new LineSegment(new Point(0, h - cut), true));
        figure.Segments.Add(new LineSegment(new Point(0, cut), true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>네 모서리의 짧은 꺾쇠 — 시선을 붙잡는 유일한 장치라 아껴 쓴다.</summary>
    private static void DrawCornerBrackets(DrawingContext dc, double w, double h, double cut)
    {
        const double Arm = 10;
        var pen = HudPalette.Strong;

        // 좌상
        dc.DrawLine(pen, new Point(cut, 0), new Point(cut + Arm, 0));
        dc.DrawLine(pen, new Point(0, cut), new Point(0, cut + Arm));

        // 우상
        dc.DrawLine(pen, new Point(w - cut - Arm, 0), new Point(w - cut, 0));
        dc.DrawLine(pen, new Point(w, cut), new Point(w, cut + Arm));

        // 좌하
        dc.DrawLine(pen, new Point(cut, h), new Point(cut + Arm, h));
        dc.DrawLine(pen, new Point(0, h - cut - Arm), new Point(0, h - cut));

        // 우하
        dc.DrawLine(pen, new Point(w - cut - Arm, h), new Point(w - cut, h));
        dc.DrawLine(pen, new Point(w, h - cut - Arm), new Point(w, h - cut));
    }
}
