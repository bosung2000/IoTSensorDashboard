using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Rendering;

namespace IoTSensorDashboard.Dashboard;

public partial class MainWindow : Window
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x08, 0x99, 0x81));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xF2, 0x36, 0x45));

    private readonly DashboardModel _model = new();
    private readonly DispatcherTimer _timer;

    private WindowState _restoreState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();

        // ⚠️ 우선순위 명시. 기본값에 맡기면 값이 굳는다.
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
        var now = DateTimeOffset.UtcNow;

        ClockText.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        UpdateFeedChip();

        var (inFlow, outFlow) = _model.FlowSummary(now);
        var (online, total) = _model.OnlineSummary(now);

        KpiIn.Text = inFlow.ToString("N0", CultureInfo.CurrentCulture);
        KpiOut.Text = outFlow.ToString("N0", CultureInfo.CurrentCulture);

        // 🔑 N / M — M 은 있어야 할 명부 기준이다.
        KpiOnline.Text = $"{online:N0} / {total:N0}";

        KpiEvents.Text = _model.TotalEvents.ToString("N0", CultureInfo.CurrentCulture);

        StoreList.ItemsSource = _model.Stores(now).Select(ToRow).ToList();

        StampText.Text = _model.LastMessageAt is { } last
            ? $"기준 {last.ToLocalTime():HH:mm:ss}"
            : "아직 수신 없음";

        Vitals.Beat("feed");

        _timer.Interval = FramePolicy.IntervalFor(IsActive, _model.Mode == FeedMode.Live, animationsOn: true);
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

    /// <summary>
    /// 표시용 행.
    ///
    /// 🔒 센서가 0대이거나 전부 죽은 매장도 <b>목록에 남는다.</b>
    ///    사라지면 「그런 매장이 없구나」가 된다 — 0 으로 표시되는 것보다 나쁘다.
    /// </summary>
    private static StoreRow ToRow(StoreStat stat)
    {
        // 관측된 적이 없으면 「측정 불가」다. 0 도 100% 도 아니다.
        string status = stat.Total == 0
            ? "센서 없음"
            : stat.Online == 0
                ? "측정 불가 (전부 무응답)"
                : stat.Online < stat.Total
                    ? $"일부 오프라인 {stat.Total - stat.Online:N0}대"
                    : "정상";

        return new StoreRow(
            stat.Name,
            $"{stat.Online:N0} / {stat.Total:N0}",
            stat.In.ToString("N0", CultureInfo.CurrentCulture),
            stat.Out.ToString("N0", CultureInfo.CurrentCulture),
            status);
    }

    private void Scope_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        // Tag 포맷: {Role}|{assignedSiteId}
        var parts = tag.Split('|');
        if (parts.Length != 2 || !Enum.TryParse<Role>(parts[0], out var role)) return;

        var siteId = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1];
        _model.SetScope(role, siteId);

        // 🔒 스코프 전환은 감사 로그에 남길 대상이다(이번 범위에서는 화면에만 표기).
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

    /// <summary>
    /// 목록 행.
    ///
    /// 🔑 <c>ToString</c> 을 재정의하지 않으면 화면에 타입 이름이 그대로 보인다.
    ///    (여기서는 컬럼 바인딩을 쓰지만, 규칙 자체를 지킨다.)
    /// </summary>
    private sealed record StoreRow(
        string Name, string OnlineText, string InText, string OutText, string StatusText)
    {
        public override string ToString() => $"{Name} · {OnlineText}";
    }
}
