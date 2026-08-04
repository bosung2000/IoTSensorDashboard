using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.Mqtt;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.SensorFarm;

/// <summary>
/// 센서 1,000대를 타일로 그린다.
///
/// 🔴 <b>이 컨트롤이 화면을 멈추게 한 적이 있다.</b>
///
/// 📌 무슨 일이 있었나: 타일마다 인덱스를 <b>센서 ID 문자열로 바꿔</b> 엔진에 물어봤다.
///    엔진은 그 문자열로 <b>선형 탐색해 다시 인덱스를 찾고</b> 락을 잡았다.
///
///    프레임당 · 문자열 1,000개 생성 · 최대 100만 회 비교 · <b>락 1,000회</b>
///
///    그 락이 발행 스레드와 같은 것이라, UI 는 화면이 멈추고 발행은 처리량이 떨어졌다.
///
/// 🔒 그래서 지금은 <b>프레임 시작에 상태를 한 번 복사</b>하고, 그 배열만 읽는다.
///    <see cref="OnRender"/> 안에서 엔진을 건드리지 않는다.
/// </summary>
public sealed class SensorFarmView : FrameworkElement
{
    private const double Tile = 18;
    private const double Gap = 3;
    private const double Step = Tile + Gap;
    private const double Radius = 3;

    /// <summary>발행 후 밝게 보일 프레임 수.</summary>
    private const int PulseFrames = 4;

    private static readonly Brush FlirOnline = HudPalette.FrozenBrush(0xFF, 0x2E, 0x6E, 0x62);
    private static readonly Brush MilesightOnline = HudPalette.FrozenBrush(0xFF, 0x4A, 0x44, 0x74);
    private static readonly Brush FlirPulse = HudPalette.FrozenBrush(0xFF, 0x5E, 0xEA, 0xD4);
    private static readonly Brush MilesightPulse = HudPalette.FrozenBrush(0xFF, 0xC9, 0xB3, 0xFF);
    private static readonly Brush Offline = HudPalette.FrozenBrush(0xFF, 0x7A, 0x2A, 0x2A);

    private SensorFarmEngine? _engine;

    /// <summary>프레임 시작에 엔진에서 한 번 복사한다. 그리는 동안에는 이 배열만 본다.</summary>
    private bool[] _online = [];

    private int[] _pulseUntilFrame = [];
    private int _frame;

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
        _online = new bool[engine.SensorCount];
        _pulseUntilFrame = new int[engine.SensorCount];

        InvalidateVisual();
    }

    /// <summary>이번에 발행한 센서들을 잠깐 밝게 표시한다.</summary>
    public void Pulse(IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);

        for (int k = 0; k < indices.Count; k++)
        {
            int i = indices[k];
            if (i >= 0 && i < _pulseUntilFrame.Length) _pulseUntilFrame[i] = _frame + PulseFrames;
        }
    }

    public void Advance() => _frame++;

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);

        if (_engine is null || ActualWidth <= 0 || ActualHeight <= 0) return;

        // 🔑 락 한 번. 이 뒤로는 엔진을 건드리지 않는다.
        _engine.CopyOnlineStates(_online);

        int perRow = Math.Max(1, (int)((ActualWidth + Gap) / Step));
        int visibleRows = (int)(ActualHeight / Step) + 1;
        int maxTiles = Math.Min(_online.Length, perRow * visibleRows);

        for (int i = 0; i < maxTiles; i++)
        {
            double x = (i % perRow) * Step;
            double y = (i / perRow) * Step;
            if (y > ActualHeight) break;

            dc.DrawRoundedRectangle(BrushFor(i), null, new Rect(x, y, Tile, Tile), Radius, Radius);
        }
    }

    /// <summary>
    /// 타일 색 — <b>배열 읽기 두 번</b>이 전부다.
    ///
    /// 벤더는 짝/홀로 알 수 있다(짝수 = FLIR). 명부 규약이 그렇게 정해져 있으므로
    /// 여기서 센서 ID 를 만들 이유가 없다.
    /// </summary>
    private Brush BrushFor(int index)
    {
        if (!_online[index]) return Offline;

        bool pulsing = _pulseUntilFrame[index] > _frame;
        bool isFlir = index % 2 == 0;

        if (pulsing) return isFlir ? FlirPulse : MilesightPulse;
        return isFlir ? FlirOnline : MilesightOnline;
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_engine is not null)
        {
            var point = e.GetPosition(this);
            int perRow = Math.Max(1, (int)((ActualWidth + Gap) / Step));

            int column = (int)(point.X / Step);
            int row = (int)(point.Y / Step);

            // 타일 사이 간격을 클릭한 경우는 무시한다 — 엉뚱한 센서가 죽으면 혼란스럽다.
            bool insideTile = point.X - column * Step <= Tile && point.Y - row * Step <= Tile;

            if (insideTile && column < perRow)
            {
                int index = row * perRow + column;
                if (index >= 0 && index < _engine.SensorCount) TileClicked?.Invoke(index);
            }
        }

        base.OnMouseLeftButtonDown(e);
    }
}
