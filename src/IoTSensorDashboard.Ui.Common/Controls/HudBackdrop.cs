using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Ui.Common.Controls;

/// <summary>
/// 화면 바닥에 깔리는 배경. 3층으로 그린다.
///
/// 🔑 이 배경은 패널이 <b>「같은 평면에 붙은 스티커」가 아니라 「위에 떠 있는 판」으로</b>
///    읽히게 하는 유일한 장치다. 없으면 화면이 한 겹으로 납작해진다.
///
/// 🔴 <b>셋 다 알파가 매우 낮다. 눈에 띄면 실패다</b> —
///    배경은 <b>인지되지 않으면서 깊이만</b> 만들어야 한다.
/// </summary>
public sealed class HudBackdrop : FrameworkElement
{
    private static readonly Pen GridPen = HudPalette.FrozenPen(0x16, 0x7E, 0x93, 0xB0, 1);
    private static readonly Pen GridMajorPen = HudPalette.FrozenPen(0x2A, 0x8E, 0xA6, 0xC4, 1);
    private static readonly Pen CrossPen = HudPalette.FrozenPen(0x38, 0x9E, 0xB6, 0xD4, 1);

    /// <summary>5칸마다 굵은 선을 긋는다.</summary>
    private const int MajorEvery = 5;

    /// <summary>십자 표식의 팔 길이(px).</summary>
    private const double CrossArm = 3;

    public HudBackdrop()
    {
        // 🔒 배경은 클릭을 가로채면 안 된다.
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>격자 간격(px).</summary>
    public double Cell { get; set; } = 44;

    /// <summary>바닥 색.</summary>
    public Brush Base { get; set; } = HudPalette.Void;

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var bounds = new Rect(0, 0, w, h);

        // ① 바닥
        dc.DrawRectangle(Base, null, bounds);

        if (Cell <= 0) return;

        double major = Cell * MajorEvery;

        // ② 미세 격자 — 5칸마다 굵게
        for (double x = 0; x <= w; x += Cell)
        {
            bool isMajor = Math.Abs(x % major) < 0.5;
            dc.DrawLine(isMajor ? GridMajorPen : GridPen, new Point(x, 0), new Point(x, h));
        }

        for (double y = 0; y <= h; y += Cell)
        {
            bool isMajor = Math.Abs(y % major) < 0.5;
            dc.DrawLine(isMajor ? GridMajorPen : GridPen, new Point(0, y), new Point(w, y));
        }

        // ③ 십자 표식 — 굵은 선이 만나는 지점
        //
        // 🔑 제도판·레이더의 기준점이다. 격자가 「무늬」가 아니라 <b>「눈금」으로 읽히게</b> 하는
        //    최소 장치이고, <b>요소를 늘리지 않고 정밀함만 올리는</b> 몇 안 되는 수단이다.
        for (double x = major; x < w; x += major)
        {
            for (double y = major; y < h; y += major)
            {
                dc.DrawLine(CrossPen, new Point(x - CrossArm, y), new Point(x + CrossArm, y));
                dc.DrawLine(CrossPen, new Point(x, y - CrossArm), new Point(x, y + CrossArm));
            }
        }

        // ④ 비네트 — 가장자리를 살짝 어둡게 해 시선을 가운데로 모은다
        dc.DrawRectangle(BuildVignette(w, h), null, bounds);
    }

    /// <summary>방사형 그라디언트. 가운데는 투명, 가장자리만 어둡다.</summary>
    private static Brush BuildVignette(double w, double h)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.75,
            RadiusY = 0.75,
            GradientStops =
            [
                new GradientStop(Colors.Transparent, 0.0),
                new GradientStop(Colors.Transparent, 0.55),
                new GradientStop(Color.FromArgb(0x66, 0, 0, 0), 1.0),
            ]
        };

        brush.Freeze();
        return brush;
    }
}
