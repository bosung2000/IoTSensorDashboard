using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.Mqtt;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.SensorFarm;

/// <summary>
/// 센서 1,000대를 타일로 그린다.
///
/// ⚠️ 타일 1,000개를 매 프레임 그리므로 <b>프레임 정책을 반드시 지켜야</b> 한다.
///    부하가 15 msg/s 뿐인데 CPU 314% 를 쓴 원인이 이 그리기였다.
///
/// 🔒 차트 라이브러리를 쓰지 않는다 — <b>그리는 빈도를 우리가 통제해야</b> 하기 때문이다.
/// </summary>
public sealed class SensorFarmView : FrameworkElement
{
    private const double Tile = 18;
    private const double Gap = 3;
    private const double Radius = 3;

    private static readonly Brush FlirOnline = HudPalette.FrozenBrush(0xFF, 0x2E, 0x6E, 0x62);
    private static readonly Brush MilesightOnline = HudPalette.FrozenBrush(0xFF, 0x4A, 0x44, 0x74);
    private static readonly Brush FlirPulse = HudPalette.FrozenBrush(0xFF, 0x5E, 0xEA, 0xD4);
    private static readonly Brush MilesightPulse = HudPalette.FrozenBrush(0xFF, 0xC9, 0xB3, 0xFF);
    private static readonly Brush Offline = HudPalette.FrozenBrush(0xFF, 0x7A, 0x2A, 0x2A);
    private static readonly Brush Backfilling = HudPalette.FrozenBrush(0xFF, 0x35, 0xC7, 0xFF);

    private SensorFarmEngine? _engine;
    private int[] _pulseUntilTick = [];
    private int _tick;

    public SensorFarmView()
    {
        ClipToBounds = true;
    }

    /// <summary>타일을 클릭하면 그 센서를 죽이거나 살린다.</summary>
    public event Action<int>? TileClicked;

    public void Attach(SensorFarmEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        _engine = engine;
        _pulseUntilTick = new int[engine.SensorCount];
        InvalidateVisual();
    }

    /// <summary>이번 틱에 발행한 센서들을 잠깐 밝게 표시한다.</summary>
    public void Pulse(IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);

        foreach (var i in indices)
            if (i >= 0 && i < _pulseUntilTick.Length) _pulseUntilTick[i] = _tick + 4;
    }

    public void Advance() => _tick++;

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);

        if (_engine is null || ActualWidth <= 0) return;

        int perRow = Math.Max(1, (int)((ActualWidth + Gap) / (Tile + Gap)));

        for (int i = 0; i < _engine.SensorCount; i++)
        {
            int row = i / perRow;
            int col = i % perRow;

            double x = col * (Tile + Gap);
            double y = row * (Tile + Gap);
            if (y > ActualHeight) break;

            var rect = new Rect(x, y, Tile, Tile);
            dc.DrawRoundedRectangle(BrushFor(i), null, rect, Radius, Radius);
        }
    }

    private Brush BrushFor(int index)
    {
        var id = Core.Provisioning.SiteProvisioning.SensorIdFor(index);

        if (!_engine!.IsOnline(id)) return Offline;

        bool pulsing = _pulseUntilTick[index] > _tick;
        bool isFlir = index % 2 == 0;

        if (pulsing) return isFlir ? FlirPulse : MilesightPulse;
        return isFlir ? FlirOnline : MilesightOnline;
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_engine is not null)
        {
            var p = e.GetPosition(this);
            int perRow = Math.Max(1, (int)((ActualWidth + Gap) / (Tile + Gap)));

            int col = (int)(p.X / (Tile + Gap));
            int row = (int)(p.Y / (Tile + Gap));
            int index = row * perRow + col;

            if (index >= 0 && index < _engine.SensorCount) TileClicked?.Invoke(index);
        }

        base.OnMouseLeftButtonDown(e);
    }

    /// <summary>백필 중인 센서 색 — 화면이 「지금 복구 중」임을 말할 수 있게.</summary>
    public static Brush BackfillBrush => Backfilling;
}
