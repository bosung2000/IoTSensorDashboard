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

    /// <summary>
    /// 레이트를 재는 창(초).
    ///
    /// 🔴 <b>화면 갱신 주기와 반드시 분리한다 — 실측 결함.</b>
    ///    창이 앞에 있으면 프레임 정책이 틱을 <b>33ms</b> 로 올린다. 그런데 센서 팜은
    ///    <b>50ms 마다 묶어서</b> 발행하므로, 33ms 창에는 그 묶음이 <b>0개 아니면 1개</b>만 들어간다.
    ///    그러면 같은 500/s 인데도 표시가 0 과 750 사이를 오간다.
    ///
    /// 📌 실측(500 발행): 33ms 틱에서 717~1,219(변동폭 502), 500ms 틱에서 862~1,138(변동폭 276).
    ///    <b>같은 시간 센서 팜의 실측 발행은 500 고정(변동폭 0)이었고</b>,
    ///    누적 증가분으로 검증한 실제 수신도 501/s 였다 —
    ///    즉 데이터는 정확했고 <b>이 계산만 틀렸다</b>. 창을 넓히면 사라진다.
    /// </summary>
    private const double RateWindowSeconds = 1.0;

    /// <summary>표시용 평균에 쓸 표본 수 — 남은 잔떨림을 눌러 준다.</summary>
    private const int RateSamples = 3;

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

    /// <summary>
    /// 레이트 표본 창. <b>수신·저장·활동이 같은 창을 쓴다</b> —
    /// 각자 재면 같은 화면에 서로 다른 기준의 「/s」가 뜬다.
    /// </summary>
    private readonly Stopwatch _sinceRate = Stopwatch.StartNew();

    private readonly Queue<double> _receiveSamples = new();
    private readonly Queue<double> _activity = new();
    private readonly Queue<FeedLine> _feed = new();

    private long _previousMessages;
    private long _previousStored;
    private bool _rateBaselineSet;
    private double _receiveRate;
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

        // 🔑 레이트는 **화면 갱신과 다른 주기**로 잰다(RateWindowSeconds).
        //    여기서 매 틱 재면 창이 발행 묶음보다 짧아져 숫자가 널뛴다.
        SampleRates();

        RxValue.Text = ((long)_receiveRate).ToString("N0", CultureInfo.CurrentCulture);
        WorkerValue.Text = _host.WorkerCount.ToString(CultureInfo.CurrentCulture);
        BacklogValue.Text = _host.Backlog.ToString("N0", CultureInfo.CurrentCulture);
        LatValue.Text = $"{now.AvgLatencyMicros:F1} µs";
        RxTotalValue.Text = _host.MessagesReceived.ToString("N0", CultureInfo.CurrentCulture);
        StoreTotalValue.Text = _host.TotalStored.ToString("N0", CultureInfo.CurrentCulture);

        UpdateBrokerChip();
        UpdateHealthChip();
        UpdateIntegrityChip(now);
        UpdateMaintenanceVital();

        UpdatePipeline(now);

        // 🔑 이 화면의 숫자가 몇 초 전 것인지 밝힌다.
        //    대시보드와 값이 다를 때 이 시각을 비교하면 시점 차이인지 오류인지 바로 갈린다.
        StampText.Text = $"기준 {DateTime.Now:HH:mm:ss}";

        Vitals.Beat("ingest");

        // 부하가 없으면 갱신 빈도를 낮춘다 —
        // 「이 시스템은 효율적이다」라고 말하려면 부하가 없을 때 CPU 도 내려가야 한다.
        _dataTimer.Interval = FramePolicy.IntervalFor(IsActive, _receiveRate > 0, animationsOn: true);
    }

    /// <summary>
    /// 파이프라인 그림과 피드를 갱신한다.
    ///
    /// 🔑 <b>스냅샷을 한 번만 만들어</b> 두 패널에 같은 것을 넘긴다.
    ///    각자 읽으면 같은 프레임에서 서로 다른 시점의 값을 그리게 된다.
    /// </summary>
    /// <summary>
    /// 수신·저장 레이트 표본을 뜬다 — <b>화면 갱신과 다른 주기</b>로.
    ///
    /// 🔑 창이 안 찼으면 <b>아무것도 바꾸지 않는다</b>. 직전 값을 그대로 보여주는 편이,
    ///    잴 수 없는 구간을 억지로 나눠 만든 숫자를 보여주는 것보다 정직하다.
    ///
    /// ⚠️ 나눌 때는 타이머 간격이 아니라 <b>실제 경과 시간</b>을 쓴다.
    ///    타이머는 best-effort 라 부하가 커지면 밀리고, 밀린 만큼 델타가 커지는데
    ///    라벨은 그대로여서 바쁠수록 과대 표시된다(과거 8배 부풀린 적이 있다).
    /// </summary>
    private void SampleRates()
    {
        double elapsed = _sinceRate.Elapsed.TotalSeconds;
        if (elapsed < RateWindowSeconds) return;

        _sinceRate.Restart();

        // 🔴 첫 표본은 **버리고 기준점만 잡는다** — 실측 결함.
        //    저장 카운터는 DB 전체 행 수라 재시작해도 남는다. 직전값을 0 으로 두고
        //    첫 델타를 그대로 쓰면 「1초에 235만 건 저장」이라는 값이 나오고,
        //    그게 활동 스트립의 최댓값이 되어 **이후 정상값이 전부 바닥에 깔린다.**
        //    한 번의 가짜 봉우리가 그 뒤 1분치 그래프를 통째로 무의미하게 만든다.
        if (!_rateBaselineSet)
        {
            _rateBaselineSet = true;
            _previousMessages = _host.MessagesReceived;
            _previousStored = _host.TotalStored;
            return;
        }

        // 🔑 수신은 **메시지** 수로 센다 — 센서 팜이 「발신 500/s」라 말할 때의 그 단위다.
        //
        // 📌 왜 이벤트가 아니라 메시지인가: 이 카드는 밖에서 들어온 양을 말한다.
        //    이벤트로 세면 한 메시지의 in·out 이 2로 잡혀 팜의 500 이 여기서 1,000 이 되고,
        //    보는 사람은 그 2배가 **중복이나 유실**인 줄 안다. 두 화면을 나란히 놓고
        //    비교하는 게 이 숫자의 용도이므로, 비교되는 쪽과 단위를 맞춘다.
        //
        // 🔴 「이벤트 ÷ 2」로 환산하지 않는다. 라인 수가 항상 2라는 보장이 없다 —
        //    코덱은 lines 배열을 순회하고, out 만 깨진 payload 는 in 하나만 낸다.
        //    그때 ÷2 는 0.5 라는 있지도 않은 값을 만든다.
        //    세지 않은 것을 나눗셈으로 지어내지 말고, **실제로 센 카운터**를 쓴다.
        long messages = _host.MessagesReceived;
        double instant = Math.Max(0, messages - _previousMessages) / elapsed;
        _previousMessages = messages;

        _receiveSamples.Enqueue(instant);
        while (_receiveSamples.Count > RateSamples) _receiveSamples.Dequeue();

        _receiveRate = _receiveSamples.Average();

        // 저장 — 활동 스트립에는 **평균이 아니라 순간값**을 넣는다.
        //        스트립의 일은 「변화를 보여주는 것」이라 평활하면 존재 이유가 없어진다.
        long stored = _host.TotalStored;
        _storeRate = Math.Max(0, stored - _previousStored) / elapsed;
        _previousStored = stored;

        _activity.Enqueue(_storeRate);
        while (_activity.Count > ActivityPoints) _activity.Dequeue();
    }

    private void UpdatePipeline(MetricsSnapshot metrics)
    {
        var now = DateTimeOffset.Now;

        NoteFeed(now, metrics, _receiveRate);

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
            ReceiveRate = _receiveRate,
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

        // 단위를 적되 **짧게** — 피드 폭은 좁고, 넘치면 말줄임에 잘려 뒤가 안 보인다.
        // 화살표가 「들어와서 나간다」는 방향까지 같이 말해 준다.
        // (수신은 메시지, 저장은 이벤트다. 안 적으면 500 → 1,000 이 유실·중복으로 읽힌다.)
        Push(now, FeedLevel.Normal,
            $"정상 · {receiveRate:N0} msg/s → {_storeRate:N0} evt/s");
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
