using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Rendering;
using IoTSensorDashboard.Dashboard.Model;

namespace IoTSensorDashboard.Dashboard;

public partial class MainWindow : Window
{
    private static readonly Brush Ok = Frozen(0x08, 0x99, 0x81);
    private static readonly Brush Warn = Frozen(0xFF, 0xC1, 0x07);
    private static readonly Brush Bad = Frozen(0xF2, 0x36, 0x45);

    private readonly DashboardModel _model = new();
    private readonly DispatcherTimer _timer;

    private WindowState _restoreState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();

        // ⚠️ 우선순위 명시. 기본값(Background)에 맡기면 애니메이션 틱에 밀려
        //    **숫자가 영원히 갱신되지 않는다.** 화면은 살아 있어 보이는데 값만 굳는,
        //    가장 찾기 어려운 부류다.
        _timer = new DispatcherTimer(DispatcherPriority.DataBind)
        {
            Interval = FramePolicy.Idle
        };
        _timer.Tick += OnTick;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Vitals.AddVital("feed", "구독", "구독이 멈추면 화면이 마지막 값을 계속 보여준다.", 8_000);

        await _model.StartAsync();
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        // 🔑 한 번만 뜬다. 패널마다 모델을 따로 읽으면 같은 프레임 안에서
        //    **서로 다른 시점**의 값을 그리게 되고, 「합계 ≠ 부분의 합」이 되어
        //    설명할 수 없는 화면이 나온다.
        var snapshot = _model.Snapshot(DateTimeOffset.UtcNow);

        UpdateHeader(snapshot);
        UpdateFeedChip();

        Sparklines.Update(snapshot);
        TopInPanel.Update(snapshot);
        Cards.Update(snapshot);
        Topology.Update(snapshot);
        Hourly.Update(snapshot);
        SiteStatus.Update(snapshot);
        EventLog.Update(snapshot);
        Gauges.Update(snapshot);
        TopThroughputPanel.Update(snapshot);
        TopOutPanel.Update(snapshot);

        Vitals.Beat("feed");

        // 유휴일 때 프레임을 늦춘다 — 아무도 안 보는 화면에 CPU 를 쓰지 않는다.
        _timer.Interval = FramePolicy.IntervalFor(IsActive, _model.Mode == FeedMode.Live, animationsOn: true);
    }

    private void UpdateHeader(DashboardSnapshot s)
    {
        KpiIn.Text = s.TotalIn.ToString("N0", CultureInfo.CurrentCulture);
        KpiOut.Text = s.TotalOut.ToString("N0", CultureInfo.CurrentCulture);
        KpiStay.Text = s.Stay.ToString("N0", CultureInfo.CurrentCulture);
        KpiOnline.Text = $"{s.OnlineSensors:N0} / {s.TotalSensors:N0}";
        KpiEvents.Text = s.UniqueEvents.ToString("N0", CultureInfo.CurrentCulture);

        // 🔑 시점 도장 — 갱신이 멈추면 이 값과 현재 시각의 차이가 스스로 자라므로
        //    **멈춘 화면이 정상처럼 보이지 않는다.**
        StampText.Text = s.LastEventAt is { } last
            ? $"기준 {last:HH:mm:ss}"
            : "아직 수신 없음";
    }

    /// <summary>
    /// 🔴 세 상태를 <b>구분해</b> 표시한다.
    ///
    /// 📌 브로커에 못 붙어 시뮬레이터로 돌고 있는데 화면이 그냥 숫자를 보여주면,
    ///    그건 <b>가짜 데이터를 실제인 것처럼</b> 그리는 것이다.
    /// </summary>
    private void UpdateFeedChip()
    {
        switch (_model.Mode)
        {
            case FeedMode.Live:
                FeedDot.Fill = Ok;
                FeedText.Text = "라이브 · 관제실 브로커 구독 중";
                break;

            case FeedMode.Demo:
                FeedDot.Fill = Bad;
                FeedText.Text = "⚠ 데모 모드 · 브로커에 붙지 못함";
                break;

            default:
                FeedDot.Fill = Warn;
                FeedText.Text = "연결 중…";
                break;
        }
    }

    private void Scope_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        // Tag 포맷: {Role}|{assignedSiteId}
        var parts = tag.Split('|');
        if (parts.Length != 2 || !Enum.TryParse<Role>(parts[0], out var role)) return;

        var siteId = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1];
        _model.SetScope(role, siteId);

        // 🔒 스코프 전환은 감사 대상이다(이번 범위에서는 화면 표기 + 모델 로그).
        ScopeNote.Text = $"스코프: {role}{(siteId is null ? "" : $" · {siteId}")}";
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        await _model.DisposeAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key == Key.F11) ToggleFullScreen();
        base.OnKeyDown(e);
    }

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.Maximized && ResizeMode == ResizeMode.NoResize)
        {
            ResizeMode = ResizeMode.CanResize;
            WindowState = _restoreState;
        }
        else
        {
            _restoreState = WindowState;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
    }

    private void FullBtn_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
