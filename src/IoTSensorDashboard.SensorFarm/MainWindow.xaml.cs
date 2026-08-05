using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IoTSensorDashboard.Core.Diagnostics;
using IoTSensorDashboard.Core.Provisioning;
using IoTSensorDashboard.Core.Rendering;
using IoTSensorDashboard.Core.Simulation;
using IoTSensorDashboard.Mqtt;

namespace IoTSensorDashboard.SensorFarm;

public partial class MainWindow : Window
{
    /// <summary>
    /// 한 번에 답을 기다리지 않고 띄워 둘 발행 수.
    ///
    /// 📌 QoS1 은 발행마다 확인 응답(PUBACK)을 기다린다 — 왕복이 약 303µs 다.
    ///    <b>하나씩 순차로 기다리면</b> 초당 약 3,300건이 물리적 상한이 된다.
    ///    여러 건을 동시에 띄워 두면 그 왕복이 겹쳐(파이프라이닝) 처리량이 오른다.
    ///
    ///    무한정 띄우면 브로커 쪽 큐가 터지므로 상한을 둔다.
    /// </summary>
    private const int MaxInFlight = 64;

    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x08, 0x99, 0x81));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xF2, 0x36, 0x45));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x72));

    private readonly SiteProvisioning _provisioning = new();
    private readonly SensorFarmEngine _engine;
    private readonly MqttSensorPublisher _publisher = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>센서 ID → 타일 인덱스. 매번 선형 탐색하면 그게 병목이 된다.</summary>
    private readonly Dictionary<string, int> _indexOf;

    /// <summary>발행 스레드가 담고 UI 스레드가 가져간다.</summary>
    private readonly object _pulseGate = new();
    private readonly HashSet<int> _pulsed = [];

    private readonly DispatcherTimer _uiTimer;
    private Thread? _publishThread;

    /// <summary>초당 발행 목표. 0 = 정지, 음수 = 「현실 모드」(분당 1건).</summary>
    private double _ratePerSecond;

    /// <summary>실측 발행량(초당). 목표와 실제가 다를 수 있으므로 따로 잰다.</summary>
    private double _measuredRate;

    private long _publishBeatTicks;
    private long _publishLoopErrors;
    private WindowState _restoreState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();

        _engine = new SensorFarmEngine(_provisioning);

        _indexOf = new Dictionary<string, int>(_provisioning.SensorIds.Count, StringComparer.Ordinal);
        for (int i = 0; i < _provisioning.SensorIds.Count; i++)
            _indexOf[_provisioning.SensorIds[i]] = i;

        // ⚠️ 화면 갱신만 UI 타이머로 한다. 발행은 여기서 하지 않는다.
        _uiTimer = new DispatcherTimer(DispatcherPriority.DataBind)
        {
            Interval = FramePolicy.Idle
        };
        _uiTimer.Tick += OnUiTick;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Farm.Attach(_engine);
        Farm.TileClicked += OnTileClicked;

        Vitals.AddVital("publish", "발행", "발행 루프가 멈추면 관제실이 모든 센서를 오프라인으로 본다.", 3_000);
        Vitals.AddVital("conn", "연결", "브로커 연결이 끊기면 발행이 전부 버려진다.", 8_000);

        // 핑에 응답한다. 🔒 살아 있는 센서만 — 죽은 센서가 응답하면
        //    관제실이 그 센서를 영원히 못 찾는다.
        _publisher.PingReceived += async body =>
        {
            foreach (var id in _engine.AckTargets(body))
                await _publisher.PublishAckAsync(id, _cts.Token).ConfigureAwait(false);
        };

        try
        {
            await _publisher.ConnectAsync(_cts.Token);
            Diag.Info("farm", $"브로커 연결 시도 · {MqttEndpoint.Host}:{MqttEndpoint.Port}");
        }
        catch (Exception ex)
        {
            ConnText.Text = $"연결 실패: {ex.Message}";
            ConnDot.Fill = Bad;
            Diag.Error("farm", "브로커 연결 실패", ex);
        }

        StartPublishThread();
        _uiTimer.Start();
    }

    // ────────────────────────────────────────────────────────────────────
    //  발행 — 🔴 UI 스레드에서 절대 하지 않는다
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 발행 전용 스레드.
    ///
    /// 🔴 <b>이 루프가 UI 타이머에 있었던 것이 결함이었다.</b>
    ///
    /// 📌 무슨 일이 있었나(실측):
    ///    극한(20,000/s) 프리셋에서 50ms 틱마다 1,000건을 <b>순차로 await</b> 했다.
    ///    그 1,000번이 전부 UI 스레드를 거치므로
    ///      · 센서 팜 화면이 통째로 멈췄고
    ///      · 실제 발행량이 목표의 2.4%(483/s)에 그쳤고
    ///      · 일부 센서가 임계 안에 발행을 못 해 <b>관제실 온라인 수가 널뛰었다.</b>
    ///
    ///    관제실 워커는 전용 스레드로 뺐으면서 발행 측은 안 뺀 것이 원인이다.
    ///
    /// 🔒 BelowNormal 인 이유도 같다 — 아무리 바빠도 <b>화면이 먼저</b>다.
    /// </summary>
    private void StartPublishThread()
    {
        _publishThread = new Thread(() => PublishLoop(_cts.Token))
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "sensor-farm-publish",
        };

        _publishThread.Start();
        Diag.Info("farm", "발행 스레드 시작");
    }

    private void PublishLoop(CancellationToken ct)
    {
        var sinceTick = Stopwatch.StartNew();
        var sinceSample = Stopwatch.StartNew();
        var lastBatchAt = DateTimeOffset.MinValue;

        double carry = 0;
        long publishedAtSample = _publisher.Published;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                double elapsed = sinceTick.Elapsed.TotalSeconds;

                if (elapsed < SensorFarmEngine.TickInterval.TotalSeconds)
                {
                    Thread.Sleep(5);
                    continue;
                }

                sinceTick.Restart();

                var now = DateTimeOffset.UtcNow;
                int count = ReadingsForThisTick(now, elapsed, ref carry, ref lastBatchAt);

                if (count > 0)
                {
                    // 🔑 이 틱이 대표하는 **실제 구간**을 넘긴다.
                    //    엔진이 관측 시각을 그 구간에 흩어, 1,000건이 같은 밀리초에
                    //    일어난 것으로 기록되지 않게 한다.
                    var readings = _engine.Tick(now, count, TimeSpan.FromSeconds(elapsed));
                    PublishBatch(readings, ct);
                    RecordPulse(readings);
                }

                // 복구된 센서의 밀린 것을 원본 시각 그대로 내보낸다.
                var backfill = _engine.DrainAllBackfill();
                if (backfill.Count > 0)
                {
                    PublishBatch(backfill, ct);
                    Diag.Info("farm.backfill", $"백필 {backfill.Count:N0}건 발행");
                }

                // 실측 발행량 — 🔑 목표가 아니라 **실제로 나간 양**을 잰다.
                //    타이머 간격으로 나누면 부하가 클 때 과대 표시된다.
                double sampleSec = sinceSample.Elapsed.TotalSeconds;
                if (sampleSec >= 1.0)
                {
                    long nowPublished = _publisher.Published;
                    Volatile.Write(ref _measuredRate, (nowPublished - publishedAtSample) / sampleSec);
                    publishedAtSample = nowPublished;
                    sinceSample.Restart();
                }

                Interlocked.Exchange(ref _publishBeatTicks, DateTimeOffset.UtcNow.Ticks);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 한 틱이 실패해도 루프는 계속 돈다.
                // 🔒 다만 조용히 넘기지 않는다 — 세고 남긴다.
                Interlocked.Increment(ref _publishLoopErrors);
                Diag.Error("farm.publish", "발행 루프 오류", ex);

                Thread.Sleep(100);   // 오류가 반복될 때 로그가 폭주하지 않게
            }
        }

        Diag.Info("farm", "발행 스레드 종료");
    }

    /// <summary>
    /// 여러 건을 묶어 발행한다.
    ///
    /// 🔑 순차 <c>await</c> 가 아니라 <b>동시에 여러 개를 띄워 둔다.</b>
    ///    QoS1 확인 응답 왕복이 겹쳐야 처리량이 오른다.
    ///
    /// 이 스레드는 전용 스레드이므로 여기서 블로킹해도 화면에 영향이 없다.
    /// </summary>
    private void PublishBatch(IReadOnlyList<SensorReading> readings, CancellationToken ct)
    {
        var pending = new List<Task>(MaxInFlight);

        foreach (var r in readings)
        {
            if (ct.IsCancellationRequested) return;

            var body = VendorPayloadFactory.Build(r.Vendor, r.SensorId, r.At, r.In, r.Out);
            pending.Add(_publisher.PublishAsync(r.Vendor, r.SiteId, r.SensorId, body, ct));

            if (pending.Count < MaxInFlight) continue;

            WaitAll(pending);
            pending.Clear();
        }

        if (pending.Count > 0) WaitAll(pending);
    }

    /// <summary>
    /// 발행 완료를 기다린다.
    ///
    /// 🔒 개별 실패가 배치 전체를 멈추게 하지 않는다 —
    ///    연결이 끊긴 순간의 발행은 실패하지만, 재연결 후 계속 가야 한다.
    /// </summary>
    private void WaitAll(List<Task> pending)
    {
        try
        {
            Task.WhenAll(pending).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _publishLoopErrors);
            Diag.Warn("farm.publish", $"발행 {pending.Count}건 중 일부 실패", ex);
        }
    }

    /// <summary>이번 틱에 몇 건을 관측할 것인가.</summary>
    private int ReadingsForThisTick(
        DateTimeOffset now, double elapsedSeconds, ref double carry, ref DateTimeOffset lastBatchAt)
    {
        double rate = Volatile.Read(ref _ratePerSecond);

        // 「현실 모드」 — 실제 인원 카운터는 사람마다 쏘지 않고 분당 1건으로 묶어 보낸다.
        //
        // 🔑 이 모드가 적응 임계를 실증하는 시나리오다.
        //    고정 12초 임계였다면 여기서 정상 센서가 전부 오프라인으로 잡힌다.
        if (rate < 0)
        {
            if (now - lastBatchAt < SensorFarmEngine.BatchInterval) return 0;

            lastBatchAt = now;
            return _engine.SensorCount;
        }

        if (rate <= 0) return 0;

        // 🔑 고정 간격이 아니라 **실제 경과 시간**으로 계산한다.
        //    스레드가 밀리면 그만큼 더 내보내야 목표 속도가 유지된다.
        carry += rate * elapsedSeconds;

        int count = (int)carry;
        carry -= count;
        return count;
    }

    private void RecordPulse(IReadOnlyList<SensorReading> readings)
    {
        lock (_pulseGate)
        {
            foreach (var r in readings)
                if (_indexOf.TryGetValue(r.SensorId, out var index)) _pulsed.Add(index);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  화면 — 카운터를 읽기만 한다
    // ────────────────────────────────────────────────────────────────────

    private void OnUiTick(object? sender, EventArgs e)
    {
        bool connected = _publisher.IsConnected;

        // 🔒 연결 칩은 매 틱 재검증한다.
        ConnDot.Fill = connected ? Ok : Bad;
        ConnText.Text = connected
            ? $"연결됨 · {MqttEndpoint.Host}:{MqttEndpoint.Port}"
            : "브로커 연결 끊김";

        int online = _engine.OnlineCount;
        int total = _engine.SensorCount;

        UptimeLabel.Text = total > 0
            ? (100.0 * online / total).ToString("0.##", CultureInfo.CurrentCulture) + "%"
            : "측정 불가";
        UptimeSub.Text = $"{online:N0} / {total:N0}";

        // 🔑 목표와 실측을 **함께** 보여준다.
        //    극한(20,000)은 원래 도달하지 못하는 값이다 —
        //    목표만 보여주면 "왜 안 나오지?"가 되고, 실측만 보여주면 무엇을 눌렀는지 모른다.
        RateLabel.Text = DescribeRate();

        SentLabel.Text = $"누적 발신 {_publisher.Published:N0} (이번 세션)";

        UpdateLossLabel();
        UpdateBackfillLabel();

        // 발행 스레드가 살아 있는가.
        var lastBeat = new DateTimeOffset(Interlocked.Read(ref _publishBeatTicks), TimeSpan.Zero);
        if (DateTimeOffset.UtcNow - lastBeat < TimeSpan.FromSeconds(2)) Vitals.Beat("publish");
        if (connected) Vitals.Beat("conn");

        Farm.Pulse(TakePulses());
        Farm.Advance();
        Farm.InvalidateVisual();

        _uiTimer.Interval = FramePolicy.IntervalFor(
            IsActive, Volatile.Read(ref _ratePerSecond) != 0, animationsOn: true);
    }

    private string DescribeRate()
    {
        double target = Volatile.Read(ref _ratePerSecond);
        double measured = Volatile.Read(ref _measuredRate);

        if (target < 0) return $"현실 · 실측 {measured:N0}/s";
        if (target <= 0) return "정지";

        return $"{target:N0} 목표 · 실측 {measured:N0}/s";
    }

    private void UpdateLossLabel()
    {
        // 🔴 유실은 0 이면 조용히, 0 이 아니면 경고색 + 건수.
        //    「유실 0」을 쓸 수 있는 이유는 그걸 반증할 카운터가 있기 때문이다.
        long dropped = _engine.DroppedByBufferCap;
        long errors = Interlocked.Read(ref _publishLoopErrors);

        if (dropped > 0)
        {
            LossLabel.Text = $"유실 {dropped:N0}건 (버퍼 상한 초과)";
            LossLabel.Foreground = Bad;
        }
        else if (errors > 0)
        {
            LossLabel.Text = $"유실 0 · 발행 오류 {errors:N0}건";
            LossLabel.Foreground = Warn;
        }
        else
        {
            LossLabel.Text = "유실 0";
            LossLabel.Foreground = Dim;
        }
    }

    private void UpdateBackfillLabel()
    {
        int buffered = _engine.BufferedCount;

        BackfillLabel.Text = buffered > 0
            ? $"백필 대기 {buffered:N0}건 (상한 {SensorFarmEngine.BufferCapacity:N0}/센서)"
            : "";
    }

    private int[] TakePulses()
    {
        lock (_pulseGate)
        {
            if (_pulsed.Count == 0) return [];

            var snapshot = _pulsed.ToArray();
            _pulsed.Clear();
            return snapshot;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  조작
    // ────────────────────────────────────────────────────────────────────

    private void OnTileClicked(int index)
    {
        // 클릭은 드물게 일어나므로 여기서는 인덱스로 바로 묻는다(탐색 없음).
        bool wasOnline = _engine.IsOnlineAt(index);

        _engine.SetOfflineAt(index, wasOnline);
        Diag.Info("farm.toggle",
            $"{SiteProvisioning.SensorIdFor(index)} → {(wasOnline ? "오프라인" : "온라인")}");
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        if (tag == "batch")
        {
            Volatile.Write(ref _ratePerSecond, -1);
            Diag.Info("farm.rate", "현실 모드(분당 1건)");
            return;
        }

        double rate = double.TryParse(tag, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        Volatile.Write(ref _ratePerSecond, rate);
        Diag.Info("farm.rate", $"목표 {rate:N0}/s");
    }

    private void AnomalyBtn_Click(object sender, RoutedEventArgs e)
    {
        // 🔑 화면만 바꾸는 게 아니라 진짜 MQTT 로 발행한다.
        //    그래야 관제실이 실제로 격리하는지 증명된다.
        var anomaly = _engine.CreateAnomaly(DateTimeOffset.UtcNow);
        var body = VendorPayloadFactory.Build(
            anomaly.Vendor, anomaly.SensorId, anomaly.At, anomaly.In, anomaly.Out);

        _ = _publisher.PublishAsync(anomaly.Vendor, anomaly.SiteId, anomaly.SensorId, body, _cts.Token);

        AnomalyLabel.Text = $"{anomaly.SensorId} 에 {anomaly.In:N0} 주입 — 관제실 정합 칩 확인";
        AnomalyLabel.Foreground = Warn;

        Diag.Info("farm.anomaly", $"{anomaly.SensorId} 에 {anomaly.In} 주입");
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _uiTimer.Stop();

        await _cts.CancelAsync();
        _publishThread?.Join(TimeSpan.FromSeconds(2));

        await _publisher.DisposeAsync();
        _cts.Dispose();
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
