using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IoTSensorDashboard.Core.Provisioning;
using IoTSensorDashboard.Core.Rendering;
using IoTSensorDashboard.Core.Simulation;
using IoTSensorDashboard.Mqtt;

namespace IoTSensorDashboard.SensorFarm;

public partial class MainWindow : Window
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x08, 0x99, 0x81));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xF2, 0x36, 0x45));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));

    private readonly SiteProvisioning _provisioning = new();
    private readonly SensorFarmEngine _engine;
    private readonly MqttSensorPublisher _publisher = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _uiTimer;

    /// <summary>초당 발행 목표. 0 이면 정지, 음수(-1)면 「현실 모드」(분당 1건).</summary>
    private double _ratePerSecond;

    /// <summary>정수로 안 떨어지는 발행량을 다음 틱으로 넘긴다.</summary>
    private double _carry;

    private DateTimeOffset _lastBatchAt = DateTimeOffset.MinValue;
    private WindowState _restoreState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();

        _engine = new SensorFarmEngine(_provisioning);

        // 발행 루프 — 20Hz. UI 부드러움과 발행 배치 크기의 균형점.
        _tickTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = SensorFarmEngine.TickInterval
        };
        _tickTimer.Tick += OnPublishTick;

        // ⚠️ 화면 갱신 타이머는 우선순위를 명시한다. 기본값에 맡기지 않는다.
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

        // 핑에 응답한다. 🔒 **살아 있는 센서만** — 죽은 센서가 응답하면
        //    관제실이 그 센서를 영원히 못 찾는다.
        _publisher.PingReceived += async body =>
        {
            foreach (var id in _engine.AckTargets(body))
                await _publisher.PublishAckAsync(id, _cts.Token).ConfigureAwait(false);
        };

        try
        {
            await _publisher.ConnectAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            ConnText.Text = $"연결 실패: {ex.Message}";
            ConnDot.Fill = Bad;
        }

        _tickTimer.Start();
        _uiTimer.Start();
    }

    /// <summary>발행 루프.</summary>
    private async void OnPublishTick(object? sender, EventArgs e)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            int count = ReadingsForThisTick(now);

            if (count > 0)
            {
                var readings = _engine.Tick(now, count);
                await PublishAsync(readings);

                // 발행한 센서를 잠깐 밝게 — 「지금 살아 있다」를 눈으로 보이게.
                Farm.Pulse(readings.Select(r => IndexOf(r.SensorId)).ToList());
            }

            // 온라인이 된 센서의 밀린 것을 원본 시각 그대로 내보낸다.
            var backfill = _engine.DrainAllBackfill();
            if (backfill.Count > 0) await PublishAsync(backfill);

            Farm.Advance();
            Vitals.Beat("publish");

            if (_publisher.IsConnected) Vitals.Beat("conn");
        }
        catch (Exception)
        {
            // 한 틱이 실패해도 루프는 계속 돈다. 상태는 활력 표시등이 드러낸다.
        }
    }

    /// <summary>이번 틱에 몇 건을 관측할 것인가.</summary>
    private int ReadingsForThisTick(DateTimeOffset now)
    {
        // 「현실 모드」 — 실제 인원 카운터는 사람마다 쏘지 않고 분당 1건으로 묶어 보낸다.
        //
        // 🔑 이 모드가 적응 임계를 실증하는 시나리오다.
        //    고정 12초 임계였다면 이 모드에서 정상 센서가 전부 오프라인으로 잡힌다.
        if (_ratePerSecond < 0)
        {
            if (now - _lastBatchAt < SensorFarmEngine.BatchInterval) return 0;

            _lastBatchAt = now;
            return _engine.SensorCount;
        }

        if (_ratePerSecond <= 0) return 0;

        _carry += _ratePerSecond * SensorFarmEngine.TickInterval.TotalSeconds;
        int count = (int)_carry;
        _carry -= count;
        return count;
    }

    private async Task PublishAsync(IReadOnlyList<SensorReading> readings)
    {
        foreach (var r in readings)
        {
            var body = VendorPayloadFactory.Build(r.Vendor, r.SensorId, r.At, r.In, r.Out);
            await _publisher.PublishAsync(r.Vendor, r.SiteId, r.SensorId, body, _cts.Token)
                            .ConfigureAwait(true);
        }
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        bool connected = _publisher.IsConnected;

        // 🔒 연결 칩은 매 틱 재검증한다. 기동 시 한 번 켜고 두면
        //    끊겨도 초록으로 남는다.
        ConnDot.Fill = connected ? Ok : Bad;
        ConnText.Text = connected ? $"연결됨 · {MqttEndpoint.Host}:{MqttEndpoint.Port}" : "브로커 연결 끊김";

        int online = _engine.OnlineCount;
        int total = _engine.SensorCount;

        UptimeLabel.Text = total > 0
            ? (100.0 * online / total).ToString("0.##", CultureInfo.CurrentCulture) + "%"
            : "측정 불가";
        UptimeSub.Text = $"{online:N0} / {total:N0}";

        SentLabel.Text = $"누적 발신 {_publisher.Published:N0} (이번 세션)";

        // 🔴 유실 — 0 이면 조용히, 0 이 아니면 경고색 + 건수.
        //
        // 📌 화면이 「유실 0」을 하드코딩된 문자열로 띄우고 있어서
        //    실제로는 링버퍼가 조용히 폐기 중인데도 무손실이라고 말한 적이 있다.
        long dropped = _engine.DroppedByBufferCap;
        if (dropped > 0)
        {
            LossLabel.Text = $"유실 {dropped:N0}건 (버퍼 상한 초과)";
            LossLabel.Foreground = Bad;
        }
        else
        {
            LossLabel.Text = "유실 0";
            LossLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x72));
        }

        int buffered = _engine.BufferedCount;
        BackfillLabel.Text = buffered > 0
            ? $"백필 대기 {buffered:N0}건 (상한 {SensorFarmEngine.BufferCapacity:N0}/센서)"
            : "";

        Farm.InvalidateVisual();

        _uiTimer.Interval = FramePolicy.IntervalFor(IsActive, _ratePerSecond != 0, animationsOn: true);
    }

    private void OnTileClicked(int index)
    {
        var id = SiteProvisioning.SensorIdFor(index);
        _engine.SetOfflineAt(index, _engine.IsOnline(id));
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        if (tag == "batch")
        {
            _ratePerSecond = -1;
            _lastBatchAt = DateTimeOffset.MinValue;
            RateLabel.Text = "현실 (분당 1건)";
            return;
        }

        _ratePerSecond = double.TryParse(tag, CultureInfo.InvariantCulture, out var rate) ? rate : 0;
        _carry = 0;

        RateLabel.Text = _ratePerSecond <= 0
            ? "정지"
            : $"{_ratePerSecond:N0} msg/s";
    }

    private void AnomalyBtn_Click(object sender, RoutedEventArgs e)
    {
        // 🔑 가짜로 화면만 바꾸는 게 아니라 **진짜 MQTT 로 발행**한다.
        //    그래야 관제실이 실제로 격리하는지 증명된다.
        var anomaly = _engine.CreateAnomaly(DateTimeOffset.UtcNow);
        var body = VendorPayloadFactory.Build(
            anomaly.Vendor, anomaly.SensorId, anomaly.At, anomaly.In, anomaly.Out);

        _ = _publisher.PublishAsync(anomaly.Vendor, anomaly.SiteId, anomaly.SensorId, body, _cts.Token);

        AnomalyLabel.Text = $"{anomaly.SensorId} 에 {anomaly.In:N0} 주입 — 관제실 정합 칩 확인";
        AnomalyLabel.Foreground = Warn;
    }

    private int IndexOf(string sensorId)
    {
        for (int i = 0; i < _provisioning.SensorIds.Count; i++)
            if (string.Equals(_provisioning.SensorIds[i], sensorId, StringComparison.Ordinal)) return i;

        return -1;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _tickTimer.Stop();
        _uiTimer.Stop();

        await _cts.CancelAsync();
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
