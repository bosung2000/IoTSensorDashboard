using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IoTSensorDashboard.Core.Health;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Rendering;

namespace IoTSensorDashboard.ControlRoom;

public partial class MainWindow : Window
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x08, 0x99, 0x81));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xF2, 0x36, 0x45));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
    private static readonly Brush Unknown = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAC));

    private readonly ServerHost _host = new();
    private readonly DispatcherTimer _dataTimer;
    private readonly Stopwatch _sinceSample = Stopwatch.StartNew();

    private MetricsSnapshot _previous;
    private WindowState _restoreState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();

        // ⚠️ 우선순위를 명시한다. 기본값(Background)에 맡기면
        //    애니메이션 틱에 밀려 **숫자가 영원히 갱신되지 않는다.**
        //    화면은 살아 있어 보이는데 값만 굳는 — 가장 찾기 어려운 부류다.
        _dataTimer = new DispatcherTimer(DispatcherPriority.DataBind)
        {
            Interval = FramePolicy.Idle
        };
        _dataTimer.Tick += OnDataTick;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 「화면엔 안 보여도 반드시 돌아야 하는 것」을 등록한다.
        Vitals.AddVital("ingest", "수집", "수집 루프가 멈추면 데이터가 안 들어오는데 화면은 마지막 값을 유지한다.", 8_000);
        Vitals.AddVital("maintenance", "정리", "유지보수 루프가 멈추면 DB 가 무한히 커진다(778MB 사고).", 400_000);
        Vitals.AddVital("health", "핑", "생존 확인이 멈추면 죽은 센서를 영원히 못 찾는다.", 10_000);

        _host.HealthBeat += () => Dispatcher.Invoke(() => Vitals.Beat("health"));
        _host.MaintenanceBeat += () => Dispatcher.Invoke(() => Vitals.Beat("maintenance"));

        WorkerLabel.Text = $"워커 (적응 /{ServerHost.MaxWorkers})";

        try
        {
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            // 🔒 기동 실패를 조용히 넘기지 않는다.
            //    브로커가 못 뜨면 나머지가 전부 붙을 곳이 없다.
            BrokerText.Text = $"브로커 기동 실패: {ex.Message}";
            BrokerDot.Fill = Bad;
        }

        _dataTimer.Start();
    }

    private void OnDataTick(object? sender, EventArgs e)
    {
        var now = _host.Metrics;

        // ⚠️ 「초당」이라 쓸 거면 **실제 경과 시간**으로 나눈다.
        //
        // 📌 타이머 델타를 그대로 「/s」라 불러 처리량이 8배 과대 표시된 적이 있다.
        //    타이머는 best-effort 라 부하가 커지면 밀리고, 밀린 만큼 델타가 커지는데
        //    라벨은 그대로여서 **바쁠수록 과대 표시**된다.
        double elapsed = Math.Max(0.05, _sinceSample.Elapsed.TotalSeconds);
        _sinceSample.Restart();

        double rate = (now.Received - _previous.Received) / elapsed;
        _previous = now;

        RxValue.Text = ((long)rate).ToString("N0", CultureInfo.CurrentCulture);
        WorkerValue.Text = _host.WorkerCount.ToString(CultureInfo.CurrentCulture);
        BacklogValue.Text = _host.Backlog.ToString("N0", CultureInfo.CurrentCulture);
        LatValue.Text = $"{now.AvgLatencyMicros:F1} µs";
        RxTotalValue.Text = _host.MessagesReceived.ToString("N0", CultureInfo.CurrentCulture);
        StoreTotalValue.Text = _host.TotalStored.ToString("N0", CultureInfo.CurrentCulture);

        UpdateBrokerChip();
        UpdateHealthChip();
        UpdateIntegrityChip(now);
        UpdateMaintenanceVital();

        // 🔑 이 화면의 숫자가 몇 초 전 것인지 밝힌다.
        //    대시보드와 값이 다를 때 이 시각을 비교하면 시점 차이인지 오류인지 바로 갈린다.
        StampText.Text = $"기준 {DateTime.Now:HH:mm:ss}";

        Vitals.Beat("ingest");

        // 부하가 없으면 갱신 빈도를 낮춘다 —
        // 「이 시스템은 효율적이다」라고 말하려면 부하가 없을 때 CPU 도 내려가야 한다.
        _dataTimer.Interval = FramePolicy.IntervalFor(IsActive, rate > 0, animationsOn: true);
    }

    /// <summary>
    /// 🔒 브로커 칩은 <b>매 틱 재검증</b>한다.
    ///
    /// 📌 기동 시 한 번 켜고 두면 수집이 조용히 끝나도 초록으로 남는다.
    ///    조건 없이 그려지는 안심 표시가 이 프로젝트에서 가장 자주 나온 결함이다.
    /// </summary>
    private void UpdateBrokerChip()
    {
        bool running = _host.BrokerRunning;
        bool connected = _host.IngestConnected;

        if (running && connected)
        {
            BrokerDot.Fill = Ok;
            BrokerText.Text = "브로커 가동 · :5281 구독 중";
        }
        else if (running)
        {
            BrokerDot.Fill = Warn;
            BrokerText.Text = "브로커 가동 · 수집 연결 대기";
        }
        else
        {
            BrokerDot.Fill = Bad;
            BrokerText.Text = "브로커 정지";
        }
    }

    private void UpdateHealthChip()
    {
        var (online, offline, total) = _host.Health.Summary(DateTimeOffset.UtcNow, HealthPolicy.Offline);
        int neverSeen = _host.Health.NeverSeenIds().Count;

        // 🔑 「온라인 N / 전체 M」 — M 은 **있어야 할 명부** 기준이다.
        //    비율만 보여주면 분모가 무엇인지 알 수 없고, 그러면 그 비율을 믿을 수 없다.
        HealthText.Text = neverSeen > 0
            ? $"센서 {online:N0} / {total:N0} · 미확인 {neverSeen:N0}대"
            : $"센서 {online:N0} / {total:N0} 온라인";

        HealthDot.Fill = offline == 0 ? Ok : neverSeen == total ? Unknown : Warn;
    }

    /// <summary>
    /// 정합 칩.
    ///
    /// 🔑 <b>과부하 폐기를 이상치 차단보다 먼저</b> 표시한다 — 더 심각하기 때문이다.
    ///    이상치는 한 건이 격리된 것이고, 과부하 폐기는 <b>데이터가 사라진 것</b>이다.
    /// </summary>
    private void UpdateIntegrityChip(MetricsSnapshot metrics)
    {
        long dropped = _host.DroppedUnderLoad;
        long storeFailures = _host.StoreFailures;

        if (dropped > 0)
        {
            IntegrityDot.Fill = Bad;
            IntegrityText.Text = $"과부하 폐기 {dropped:N0}건";
        }
        else if (storeFailures > 0)
        {
            IntegrityDot.Fill = Bad;
            IntegrityText.Text = $"저장 실패 {storeFailures:N0}건";
        }
        else if (metrics.Implausible > 0 || metrics.Rejected > 0)
        {
            IntegrityDot.Fill = Warn;
            IntegrityText.Text = $"격리 {metrics.Implausible:N0} · 거부 {metrics.Rejected:N0}";
        }
        else
        {
            // 📏 「유실 0」을 쓸 수 있는 이유는 그걸 **반증할 카운터가 있기** 때문이다.
            //    카운터 없는 안심 문구는 거짓말이 될 준비가 된 문구다.
            IntegrityDot.Fill = Ok;
            IntegrityText.Text = "정합 OK · 유실 0";
        }
    }

    private void UpdateMaintenanceVital()
    {
        // 🔴 유지보수가 계속 실패해도 아무도 모른 적이 있다.
        //    실패를 드러내되, 성공했으면 조용히 둔다.
        if (_host.LastMaintenanceError is { } error)
            IncidentSummary.Text = $"⚠ 정리 실패: {error}";
        else if (_host.LastMaintenanceAt is { } at)
            IncidentSummary.Text = $"활성 0건 · 정상 (정리 {at.ToLocalTime():HH:mm})";
        else
            IncidentSummary.Text = "활성 0건 · 정상";
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _dataTimer.Stop();
        await _host.DisposeAsync();
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
}
