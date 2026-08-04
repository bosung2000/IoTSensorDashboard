using System.Diagnostics;
using System.Threading.Channels;
using IoTSensorDashboard.Core.Codecs;
using IoTSensorDashboard.Core.Diagnostics;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Health;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Provisioning;
using IoTSensorDashboard.Core.Storage;
using IoTSensorDashboard.Mqtt;
using IoTSensorDashboard.Sqlite;

namespace IoTSensorDashboard.ControlRoom;

/// <summary>
/// 관제실의 서버 부분 — 브로커 · 수집 · 저장 · 유지보수 · 생존 감시.
///
/// 화면은 이 객체의 카운터를 <b>읽기만</b> 한다.
/// 판정은 전부 Core 가 하고, 여기는 배선이다.
/// </summary>
public sealed class ServerHost : IAsyncDisposable
{
    /// <summary>
    /// 큐 용량.
    ///
    /// 📌 근거: 폭주(5,000/s) − 처리(3,000/s) = 2,000/s 가 60초 밀려도 버티는 크기
    ///    ≈ 120,000 + 여유. 약 70MB.
    /// </summary>
    public const int QueueCapacity = 150_000;

    /// <summary>
    /// 한 번에 묶을 최대 건수.
    ///
    /// 📌 폭주 구간에서 커밋 횟수를 두 자릿수로 줄이면서,
    ///    화면 갱신 주기(초당 수십 회)는 유지하는 지점.
    /// </summary>
    public const int BatchMax = 512;

    /// <summary>이 이상 밀리면 워커를 늘린다.</summary>
    public const int HighWater = 300;

    /// <summary>이 이하로 내려가면 줄여도 된다.</summary>
    public const int LowWater = 80;

    /// <summary>워커당 처리 상한. 실운영의 이벤트당 비용을 반영한다.</summary>
    public const int PerWorkerEventsPerSec = 2_500;

    /// <summary>생존 확인 주기.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// 워커 상한.
    ///
    /// 📌 코어를 다 덮으면 UI 가 굶는다. 2개는 화면·수집 루프 몫으로 남긴다.
    /// </summary>
    public static int MaxWorkers => Math.Max(1, Math.Min(8, Environment.ProcessorCount - 2));

    private readonly SqliteEventStore _store;
    private readonly IngestionPipeline _pipeline;
    private readonly PipelineMetrics _metrics = new();
    private readonly CodecRegistry _codecs = new(new FlirCodec(), new MilesightCodec());
    private readonly SensorHealthTracker _health = new();

    /// <summary>
    /// <b>데이터만</b> 보고 판정하는 추적기 — 핑 응답은 넣지 않는다.
    ///
    /// 🔑 <b>왜 두 벌인가</b>: 「응답한다」와 「데이터를 보낸다」는 다른 사실이고,
    ///    화면은 그 둘을 <b>나란히</b> 보여줘야 한다.
    ///    한 벌만 두고 핑까지 섞으면 발신이 멈춰도 100% 가 나와,
    ///    <b>데이터가 0 건인데 화면이 완벽해 보이는</b> 상태가 된다(실측으로 재현했다).
    ///
    /// 📌 덤: 이 값은 종합상황판이 제 눈으로 세는 값과 <b>같아야 한다</b>
    ///    (둘 다 같은 브로커의 데이터만 보므로). 두 화면이 어긋나면 그 자체가 신호다.
    /// </summary>
    private readonly SensorHealthTracker _dataHealth = new();

    private readonly SiteProvisioning _provisioning = new();

    private readonly CancellationTokenSource _cts = new();

    /// <summary>워커 목록과 각 워커를 개별로 세울 손잡이. 축소할 때 하나만 끈다.</summary>
    private readonly object _workersGate = new();
    private readonly List<(Thread Thread, CancellationTokenSource Stop)> _workers = [];

    /// <summary>축소 판정의 히스테리시스 — 연속으로 한가해야 줄인다.</summary>
    private int _quietRounds;

    private EmbeddedMqttBroker? _broker;
    private MqttIngestionSource? _ingest;
    private MqttHealthProbe? _probe;
    private Channel<RawPayload>? _channel;

    private long _messagesReceived;
    private long _droppedUnderLoad;
    private bool _disposed;

    public ServerHost(string? dbPath = null)
    {
        _store = new SqliteEventStore(dbPath);
        _pipeline = new IngestionPipeline(_store, _metrics);

        // 🔑 「있어야 할 명부」를 등록한다 — 이게 가동률의 분모다.
        //    빼먹으면 처음부터 죽어 있던 센서가 분모에서 통째로 빠져
        //    950/950 = 100% 가 된다.
        _health.Expect(_provisioning.SensorIds);

        // 🔑 분모는 두 추적기가 **같아야** 한다. 명부가 곧 분모이므로 같은 것을 등록한다.
        _dataHealth.Expect(_provisioning.SensorIds);
    }

    // ── 화면이 읽는 값들 ─────────────────────────────────────────────────

    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);

    /// <summary>
    /// 🔴 과부하로 큐에서 폐기된 수.
    ///
    /// <b>화면에 반드시 표시한다.</b> 세기만 하고 안 이으면
    ///    세지 않은 것과 사용자에게는 똑같다.
    /// </summary>
    public long DroppedUnderLoad => Interlocked.Read(ref _droppedUnderLoad);

    public long StoreFailures => _pipeline.StoreFailures;

    public long ObserverFailures => _pipeline.ObserverFailures;

    public int Backlog => _channel?.Reader.Count ?? 0;

    public int WorkerCount
    {
        get { lock (_workersGate) return _workers.Count; }
    }

    public MetricsSnapshot Metrics => _metrics.Snapshot();

    public long TotalStored => _store.Count;

    /// <summary>브로커가 실제로 리슨 중인가. 🔒 <b>매 틱 다시 물어야 한다.</b></summary>
    public bool BrokerRunning => _broker?.IsStarted ?? false;

    /// <summary>수집 채널이 실제로 붙어 있는가.</summary>
    public bool IngestConnected => _ingest?.IsConnected ?? false;

    /// <summary>응답 기준(데이터 + 핑 ACK) — 「이 센서가 살아 있는가」.</summary>
    public SensorHealthTracker Health => _health;

    /// <summary>데이터 기준 — 「이 센서가 실제로 보내고 있는가」.</summary>
    public SensorHealthTracker DataHealth => _dataHealth;

    public SiteProvisioning Provisioning => _provisioning;

    /// <summary>마지막 유지보수 시각. 활력 표시등의 근거.</summary>
    public DateTimeOffset? LastMaintenanceAt { get; private set; }

    /// <summary>마지막 유지보수 오류. 🔒 <b>삼키지 않고 남긴다.</b></summary>
    public string? LastMaintenanceError { get; private set; }

    public StorageStats Storage => _store.Stats();

    // ── 기동 ─────────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        var ct = _cts.Token;

        // ① 브로커를 먼저 띄운다 — 나머지가 붙을 곳이다.
        _broker = new EmbeddedMqttBroker(tlsCertificate: DevTls.CreateSelfSigned());
        await _broker.StartAsync().ConfigureAwait(false);

        // ② 큐
        //
        // 🔴 DropOldest 는 TryWrite 가 항상 true 를 돌려준다.
        //    콜백을 안 달면 폐기가 완전히 무성의하게 사라진다 — 코드 어디에도 흔적이 없다.
        //
        //    DropOldest 자체는 우아한 감쇠를 위한 설계 선택이지 문제가 아니다.
        //    문제는 **감쇠했다는 사실을 숨기는 것**이다.
        _channel = Channel.CreateBounded<RawPayload>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true,
            },
            itemDropped: _ => Interlocked.Increment(ref _droppedUnderLoad));

        // ③ 수집 채널
        _ingest = new MqttIngestionSource();
        _ = _ingest.RunAsync(OnRawAsync, ct);

        // ④ 워커 — 하나로 시작해서 백로그에 따라 늘린다.
        StartWorker();
        _ = RunScalingLoopAsync(ct);

        // ⑤ 생존 확인
        _probe = new MqttHealthProbe();
        _probe.AckReceived += id => _health.Observe(id, DateTimeOffset.UtcNow);
        await _probe.ConnectAsync(ct).ConfigureAwait(false);
        _ = RunHealthLoopAsync(ct);

        // ⑥ 유지보수
        _ = RunMaintenanceLoopAsync(ct);

        Diag.Info("server", $"기동 완료 · 브로커 :{MqttEndpoint.Port} · 워커 상한 {MaxWorkers} · 명부 {_provisioning.SensorIds.Count:N0}대");
    }

    /// <summary>
    /// 백로그를 보고 워커를 늘리거나 줄인다.
    ///
    /// 🔑 <b>백로그가 차기 전에</b> 늘린다. 다 찬 뒤에 늘리면 이미 폐기가 시작된 뒤다.
    ///
    /// 축소에 히스테리시스를 두는 이유: 경계에서 왔다 갔다 하면
    /// 스레드를 만들고 죽이는 비용이 처리량을 갉아먹는다.
    /// </summary>
    private async Task RunScalingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

                int backlog = Backlog;

                if (backlog >= HighWater)
                {
                    _quietRounds = 0;

                    lock (_workersGate)
                    {
                        if (_workers.Count < MaxWorkers)
                        {
                            StartWorkerLocked();
                            Diag.Info("server.scale",
                                $"워커 증설 → {_workers.Count} (백로그 {backlog:N0})");
                        }
                    }
                }
                else if (backlog <= LowWater)
                {
                    // 연속 5초 한가해야 줄인다.
                    if (++_quietRounds >= 5)
                    {
                        _quietRounds = 0;
                        StopOneWorker(backlog);
                    }
                }
                else
                {
                    _quietRounds = 0;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Diag.Error("server.scale", "워커 조절 루프 오류", ex);
            }
        }
    }

    private void StopOneWorker(int backlog)
    {
        lock (_workersGate)
        {
            // 🔒 마지막 한 개는 남긴다 — 0 이 되면 수집이 통째로 멈춘다.
            if (_workers.Count <= 1) return;

            var (_, stop) = _workers[^1];
            _workers.RemoveAt(_workers.Count - 1);
            stop.Cancel();

            Diag.Info("server.scale", $"워커 축소 → {_workers.Count} (백로그 {backlog:N0})");
        }
    }

    /// <summary>
    /// 수신 — <b>절대 블록하지 않는다.</b>
    ///
    /// 📌 구독 콜백이 지체되면 브로커에 확인 응답이 늦어지고,
    ///    브로커가 재전송을 시작해 그 재전송이 다시 큐를 채운다 → <b>잼 고착.</b>
    /// </summary>
    private Task OnRawAsync(RawPayload raw)
    {
        Interlocked.Increment(ref _messagesReceived);

        // 🔑 도착 시각은 **지금** 찍는다.
        //    큐에서 꺼낼 때 찍으면 대기한 시간만큼 헬스가 거짓으로 젊어진다.
        _channel!.Writer.TryWrite(raw with { ReceivedAt = DateTimeOffset.UtcNow });

        return Task.CompletedTask;
    }

    /// <summary>
    /// 워커 하나를 띄운다.
    ///
    /// 🔒 스레드풀 태스크가 아니라 <b>전용 스레드</b>다.
    ///
    /// 📌 근거: 서버가 아무리 바빠도 <b>UI 는 멈추면 안 된다.</b>
    ///    스레드풀 태스크는 <b>우선순위를 낮출 수 없어</b> UI 스레드와 같은 순위로 코어를 다툰다.
    ///    8 논리 프로세서에 워커 8개를 태우면 UI 가 구조적으로 굶는다.
    ///
    ///    전용 스레드면 BelowNormal 을 줄 수 있고, OS 스케줄러가 <b>항상 UI 를 먼저 깨운다.</b>
    ///    처리량은 유휴 CPU 로 그대로 흡수되고, 잃는 것은 <b>「UI 를 밀어낼 권리」뿐</b>이다.
    /// </summary>
    private void StartWorker()
    {
        lock (_workersGate) StartWorkerLocked();
    }

    private void StartWorkerLocked()
    {
        // 전체 종료와 개별 축소를 둘 다 받는 손잡이.
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        var thread = new Thread(() => WorkerLoop(stop.Token))
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = $"ingest-worker-{_workers.Count + 1}",
        };

        _workers.Add((thread, stop));
        thread.Start();
    }

    private void WorkerLoop(CancellationToken ct)
    {
        var reader = _channel!.Reader;
        var batch = new List<CountEvent?>(BatchMax);
        var pace = Stopwatch.StartNew();
        int done = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 🔒 스핀 금지 — 재워야 그 코어를 UI 가 쓴다.
                if (!reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult()) break;
                if (!reader.TryRead(out var raw)) continue;

                batch.Clear();
                Decode(raw, batch);

                // 지금 당장 꺼낼 수 있는 것까지만 묶는다 — 모으려고 기다리지 않는다.
                // 저부하에선 배치가 1이 되어 지연이 안 늘어난다.
                while (batch.Count < BatchMax && reader.TryRead(out var more))
                    Decode(more, batch);

                if (batch.Count > 0)
                {
                    _pipeline.IngestBatch(batch);
                    done += batch.Count;
                }

                Pace(pace, ref done);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 한 배치가 터져도 루프는 계속 돈다.
                // 🔒 다만 조용히 넘기지 않는다 — 로그에 남긴다.
                //    (건수는 파이프라인의 StoreFailures 가 센다.)
                Diag.Error("server.worker", "배치 처리 오류", ex);
                Thread.Sleep(50);   // 오류가 반복될 때 로그가 폭주하지 않게
            }
        }
    }

    private void Decode(RawPayload raw, List<CountEvent?> batch)
    {
        var decoded = _codecs.Decode(raw);

        foreach (var e in decoded)
        {
            batch.Add(e);

            // 🔑 헬스는 **도착 시각** 기준이다. 처리 시각이 아니다.
            _health.Observe(e.SensorId, raw.ReceivedAt);

            // 🔒 데이터 추적기는 **여기서만** 갱신된다 — 핑 ACK 경로에서는 건드리지 않는다.
            //    그 구분이 이 추적기의 존재 이유다.
            _dataHealth.Observe(e.SensorId, raw.ReceivedAt);
        }
    }

    /// <summary>
    /// 슬라이딩 윈도우 페이싱 — <b>부채(debt)를 쌓지 않는다.</b>
    ///
    /// 📌 누적으로 비교하면, 부하 중 증설된 워커가 밀린 백로그를 한 번에 처리해
    ///    done 이 allowed 보다 크게 앞선 채(= debt) 태어난다.
    ///    그 워커는 <b>오래 저속으로 고착</b>되어, 부하가 풀려도 백로그가 안 빠지는 잼이 생긴다.
    ///
    ///    1초마다 리셋하면 debt 가 소멸해 <b>부하가 풀린 즉시 회복</b>된다.
    /// </summary>
    private static void Pace(Stopwatch pace, ref int done)
    {
        double win = pace.Elapsed.TotalSeconds;

        if (win >= 1.0)
        {
            pace.Restart();
            done = 0;
            return;
        }

        double allowed = win * PerWorkerEventsPerSec + PerWorkerEventsPerSec * 0.05;
        if (done > allowed) Thread.Sleep(1);
    }

    /// <summary>
    /// 생존 확인 — <b>조용한 센서에게만</b> 묻는다.
    ///
    /// 📌 1,000대에게 2.5초마다 물으면 그 트래픽이 실제 데이터를 압도한다.
    ///    데이터가 오는 센서는 그 데이터가 생존 증거다.
    /// </summary>
    private async Task RunHealthLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PingInterval, ct).ConfigureAwait(false);

                var targets = _health.ProbeTargets(DateTimeOffset.UtcNow, HealthPolicy.Probe);
                if (targets.Count > 0 && _probe is not null)
                    await _probe.PingAsync(targets, ct).ConfigureAwait(false);

                HealthBeat?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 다음 주기에 다시 시도한다. 🔑 활력 표시등이 이 루프의 생사를 드러낸다.
                Diag.Warn("server.health", "생존 확인 루프 오류", ex);
            }
        }
    }

    /// <summary>
    /// 유지보수 — 롤업 · 프룬 · 체크포인트 · 공간 회수.
    ///
    /// 📌 근거: 보존·롤업 없이 7시간 돌렸더니 DB 가 778MB 로 불어나
    ///    시스템이 스스로 느려졌다.
    ///
    /// 🔴 그리고 이 루프가 예외를 통째로 삼켜 계속 실패해도 아무도 모른 적이 있다.
    ///    그래서 마지막 시각·마지막 오류를 남기고, 활력 표시등이 그걸 드러낸다.
    /// </summary>
    private async Task RunMaintenanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RetentionPolicy.MaintenanceInterval, ct).ConfigureAwait(false);

                // 바쁘면 이번 주기는 양보한다 — 정리가 수집을 죽이면 안 된다.
                bool busy = Backlog >= RetentionPolicy.MaintenanceBusyBacklog;

                var cutoff = RetentionPolicy.CutoffFor(DateTimeOffset.UtcNow);
                var budget = Stopwatch.StartNew();

                while (budget.Elapsed < RetentionPolicy.MaintenanceBudget)
                {
                    int pruned = _store.RollupAndPrune(cutoff, RetentionPolicy.PruneChunkRows);
                    if (pruned < RetentionPolicy.PruneChunkRows) break;
                }

                _store.Checkpoint(truncate: !busy);
                _store.ReclaimIncremental();

                // ⚠️ 전체 재작성은 두 조건을 모두 만족할 때만.
                if (!busy && RetentionPolicy.ShouldReclaimFull(_store.Stats()))
                    _store.ReclaimFull();

                LastMaintenanceAt = DateTimeOffset.UtcNow;
                LastMaintenanceError = null;

                MaintenanceBeat?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 🔒 삼키되 남긴다. 두 주기 연속 실패하면 활력 표시등 색이 바뀐다.
                LastMaintenanceError = ex.Message;
                Diag.Error("server.maintenance", "유지보수 루프 오류", ex);
            }
        }
    }

    /// <summary>수집 루프가 한 바퀴 돌 때 화면이 활력 점을 찍게 한다.</summary>
    public event Action? HealthBeat;

    /// <summary>유지보수 루프가 한 바퀴 돌 때.</summary>
    public event Action? MaintenanceBeat;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync().ConfigureAwait(false);
        _channel?.Writer.TryComplete();

        if (_ingest is not null) await _ingest.DisposeAsync().ConfigureAwait(false);
        if (_probe is not null) await _probe.DisposeAsync().ConfigureAwait(false);
        if (_broker is not null) await _broker.DisposeAsync().ConfigureAwait(false);

        lock (_workersGate)
        {
            foreach (var (thread, stop) in _workers)
            {
                thread.Join(TimeSpan.FromSeconds(2));
                stop.Dispose();
            }

            _workers.Clear();
        }

        _store.Dispose();
        _cts.Dispose();

        Diag.Info("server", "종료 완료");
    }
}
