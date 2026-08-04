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
    private const double Gap = 3;
    private const double Radius = 3;

    /// <summary>타일이 이보다 작아지면 클릭도 어렵고 색도 못 읽는다.</summary>
    private const double MinTile = 4;

    /// <summary>이보다 크면 「센서 다발」이 아니라 「버튼 몇 개」로 보인다.</summary>
    private const double MaxTile = 26;

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

    /// <summary>
    /// 마지막으로 그릴 때 쓴 격자 크기.
    ///
    /// 🔑 <b>클릭 판정이 이 값을 그대로 써야 한다.</b> 그리기와 클릭이 서로 다른 계산을 하면
    ///    보이는 타일과 눌리는 타일이 어긋난다 — 「엉뚱한 센서가 죽는」 버그가 그렇게 난다.
    /// </summary>
    private double _step;

    private int _perRow = 1;

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

        Layout(_online.Length);

        double tile = _step - Gap;
        if (tile < 1) return;

        double radius = Math.Min(Radius, tile / 3);

        for (int i = 0; i < _online.Length; i++)
        {
            double x = (i % _perRow) * _step;
            double y = (i / _perRow) * _step;
            if (y > ActualHeight) break;

            dc.DrawRoundedRectangle(BrushFor(i), null, new Rect(x, y, tile, tile), radius, radius);
        }
    }

    /// <summary>
    /// 센서 전부가 <b>영역 안에 들어가는</b> 가장 큰 격자를 고른다.
    ///
    /// 🔴 <b>고정 크기를 쓰면 안 되는 이유</b>: 타일을 18px 로 박아 두었더니
    ///    창이 커져도 격자는 그대로라 <b>아래 4분의 1이 빈 채</b>로 남았다.
    ///    반대로 창을 줄이면 뒤쪽 센서가 <b>말없이 화면 밖으로</b> 밀려났다 —
    ///    「안 보이는 센서」는 죽어도 아무도 모른다. 1,000대가 <b>전부 보이는 것</b>이 이 화면의 요건이다.
    ///
    /// 📌 큰 쪽부터 훑어 첫 번째로 들어맞는 값을 쓴다. 후보가 40단계뿐이라
    ///    이분 탐색을 쓸 이유가 없고, 위에서 내려오므로 <b>항상 가능한 가장 큰 타일</b>이 나온다.
    /// </summary>
    private void Layout(int count)
    {
        if (count <= 0)
        {
            _step = MinTile + Gap;
            _perRow = 1;
            return;
        }

        for (double tile = MaxTile; tile >= MinTile; tile -= 0.5)
        {
            double step = tile + Gap;

            int perRow = Math.Max(1, (int)((ActualWidth + Gap) / step));
            int rows = (int)Math.Ceiling((double)count / perRow);

            if (rows * step - Gap > ActualHeight) continue;

            _step = step;
            _perRow = perRow;
            return;
        }

        // 하한에서도 안 들어가면 하한으로 그린다 — 잘려도 「가능한 만큼」은 보여준다.
        _step = MinTile + Gap;
        _perRow = Math.Max(1, (int)((ActualWidth + Gap) / _step));
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

        // 🔒 그릴 때 쓴 격자(_step·_perRow)를 그대로 쓴다. 여기서 다시 계산하면
        //    보이는 타일과 눌리는 타일이 어긋나 엉뚱한 센서가 죽는다.
        if (_engine is not null && _step > Gap)
        {
            var point = e.GetPosition(this);
            double tile = _step - Gap;

            int column = (int)(point.X / _step);
            int row = (int)(point.Y / _step);

            // 타일 사이 간격을 클릭한 경우는 무시한다 — 엉뚱한 센서가 죽으면 혼란스럽다.
            bool insideTile = point.X - column * _step <= tile && point.Y - row * _step <= tile;

            if (insideTile && column < _perRow)
            {
                int index = row * _perRow + column;
                if (index >= 0 && index < _engine.SensorCount) TileClicked?.Invoke(index);
            }
        }

        base.OnMouseLeftButtonDown(e);
    }
}
