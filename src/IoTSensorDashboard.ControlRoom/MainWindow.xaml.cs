using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IoTSensorDashboard.ControlRoom.Model;
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

    /// <summary>활동 스트립이 보관하는 막대 수. 초당 1개면 약 1분.</summary>
    private const int ActivityPoints = 60;

    /// <summary>피드 보관 줄 수.</summary>
    private const int FeedLines = 60;

    /// <summary>
    /// 「아무 일 없음」을 적는 간격.
    ///
    /// 🔑 사건이 없을 때도 <b>주기적으로 한 줄</b>을 남긴다. 피드가 조용한 것이
    ///    「평화롭다」인지 「굳었다」인지 구별할 방법이 그것뿐이다.
    /// </summary>
    private static readonly TimeSpan HeartbeatLine = TimeSpan.FromSeconds(10);

    private readonly ServerHost _host = new();
    private readonly DispatcherTimer _dataTimer;
    private readonly Stopwatch _sinceSample = Stopwatch.StartNew();
    private readonly Stopwatch _sinceActivity = Stopwatch.StartNew();

    private readonly Queue<double> _activity = new();
    private readonly Queue<FeedLine> _feed = new();

    private MetricsSnapshot _previous;
    private long _previousStored;
    private double _storeRate;
    private DateTimeOffset _lastFeedAt;
    private bool? _lastBrokerOk;
    private long _lastDropped;
    private long _lastStoreFailures;
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

        // ⚠️ 블로킹 Dispatcher.Invoke 금지 — 이 이벤트는 **백그라운드 루프**가 발화한다.
        //    Invoke 는 UI 스레드가 받아줄 때까지 호출한 쪽을 세운다. 고부하로 UI 가 밀리면
        //    활력 점을 찍으러 온 유지보수·헬스 루프가 **자기 일을 못 하고 같이 멈춘다**.
        //    (활력 표시가 본체를 멈추는 셈 — 진단하려다 고장을 만든다.)
        //    InvokeAsync 는 큐에 넣고 즉시 돌아가므로 루프가 자기 주기를 지킨다.
        _host.HealthBeat += () => Dispatcher.InvokeAsync(() => Vitals.Beat("health"), DispatcherPriority.Background);
        _host.MaintenanceBeat += () => Dispatcher.InvokeAsync(() => Vitals.Beat("maintenance"), DispatcherPriority.Background);

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

        UpdatePipeline(now, rate);

        // 🔑 이 화면의 숫자가 몇 초 전 것인지 밝힌다.
        //    대시보드와 값이 다를 때 이 시각을 비교하면 시점 차이인지 오류인지 바로 갈린다.
        StampText.Text = $"기준 {DateTime.Now:HH:mm:ss}";

        Vitals.Beat("ingest");

        // 부하가 없으면 갱신 빈도를 낮춘다 —
        // 「이 시스템은 효율적이다」라고 말하려면 부하가 없을 때 CPU 도 내려가야 한다.
        _dataTimer.Interval = FramePolicy.IntervalFor(IsActive, rate > 0, animationsOn: true);
    }

    /// <summary>
    /// 파이프라인 그림과 피드를 갱신한다.
    ///
    /// 🔑 <b>스냅샷을 한 번만 만들어</b> 두 패널에 같은 것을 넘긴다.
    ///    각자 읽으면 같은 프레임에서 서로 다른 시점의 값을 그리게 된다.
    /// </summary>
    private void UpdatePipeline(MetricsSnapshot metrics, double receiveRate)
    {
        var now = DateTimeOffset.Now;

        // 저장 레이트는 **1초 이상 모아서** 낸다.
        // 짧은 창의 델타는 정수라 0/1 로만 튀어 「저장이 멈췄다」로 오독된다.
        double activityElapsed = _sinceActivity.Elapsed.TotalSeconds;

        if (activityElapsed >= 1.0)
        {
            _sinceActivity.Restart();

            long stored = _host.TotalStored;
            _storeRate = Math.Max(0, stored - _previousStored) / activityElapsed;
            _previousStored = stored;

            _activity.Enqueue(_storeRate);
            while (_activity.Count > ActivityPoints) _activity.Dequeue();
        }

        NoteFeed(now, metrics, receiveRate);

        var (online, _, total) = _host.Health.Summary(DateTimeOffset.UtcNow, HealthPolicy.Offline);

        var snapshot = new PipelineSnapshot
        {
            TakenAt = now,
            BrokerRunning = _host.BrokerRunning,
            IngestConnected = _host.IngestConnected,
            SensorsOnline = online,
            SensorsTotal = total,
            Backlog = _host.Backlog,
            Workers = _host.WorkerCount,
            MaxWorkers = ServerHost.MaxWorkers,
            ReceiveRate = receiveRate,
            StoreRate = _storeRate,
            AvgLatencyMicros = metrics.AvgLatencyMicros,
            TotalReceived = _host.MessagesReceived,
            TotalStored = _host.TotalStored,
            SessionStored = metrics.Appended,
            Duplicate = metrics.Duplicate,
            Rejected = metrics.Rejected,
            Implausible = metrics.Implausible,
            Dropped = _host.DroppedUnderLoad,
            Activity = [.. _activity],
            Feed = [.. _feed.Reverse()],
        };

        Pipeline.Update(snapshot);
        Feed.Update(snapshot);
    }

    /// <summary>
    /// 피드에 남길 것을 판단한다.
    ///
    /// 🔴 <b>상태가 바뀐 순간</b>은 반드시 남긴다 — 다음 틱이면 숫자에서 사라지기 때문이다.
    ///    브로커가 끊겼다 1초 만에 붙으면 어떤 카드에도 흔적이 없지만, 그건 사건이다.
    /// </summary>
    private void NoteFeed(DateTimeOffset now, MetricsSnapshot metrics, double receiveRate)
    {
        bool brokerOk = _host.BrokerRunning && _host.IngestConnected;

        if (_lastBrokerOk != brokerOk)
        {
            _lastBrokerOk = brokerOk;

            Push(now, brokerOk ? FeedLevel.Normal : FeedLevel.Error,
                brokerOk ? "브로커 연결 · 구독 시작" : "브로커 연결 끊김");
        }

        long dropped = _host.DroppedUnderLoad;
        if (dropped > _lastDropped)
        {
            // 🔴 버린 것은 반드시 남긴다. 조용한 폐기가 「유실 0」이라는 거짓말의 씨앗이다.
            Push(now, FeedLevel.Error, $"과부하 폐기 {dropped - _lastDropped:N0}건 (누적 {dropped:N0})");
            _lastDropped = dropped;
        }

        long failures = _host.StoreFailures;
        if (failures > _lastStoreFailures)
        {
            Push(now, FeedLevel.Error, $"저장 실패 {failures - _lastStoreFailures:N0}건");
            _lastStoreFailures = failures;
        }

        if (now - _lastFeedAt < HeartbeatLine) return;

        Push(now, FeedLevel.Normal,
            $"정상 처리 · 수신 {receiveRate:N0}/s · 저장 {_storeRate:N0}/s");
    }

    private void Push(DateTimeOffset at, FeedLevel level, string message)
    {
        _feed.Enqueue(new FeedLine(at, level, message));
        while (_feed.Count > FeedLines) _feed.Dequeue();

        _lastFeedAt = at;
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
        //
        // 🔴 「핑 포함」을 붙이는 이유 — 실측 사고:
        //    같은 시각에 여기는 1,000/1,000, 대시보드는 693/1,000 이었다.
        //    관제실은 조용한 센서에게 **직접 물어보고**(MqttHealthProbe) 응답을 세지만
        //    대시보드는 도착한 데이터만 본다. 근거가 다르면 값이 달라지는 게 정상이고,
        //    화면은 그 근거를 스스로 말해야 한다. 안 그러면 둘 중 하나가 고장 난 것처럼 보인다.
        HealthText.Text = neverSeen > 0
            ? $"센서 {online:N0} / {total:N0} · 미확인 {neverSeen:N0}대 (핑 포함)"
            : $"센서 {online:N0} / {total:N0} 온라인 (핑 포함)";

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
