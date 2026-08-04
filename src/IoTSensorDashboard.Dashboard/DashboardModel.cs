using IoTSensorDashboard.Core.Authorization;
using IoTSensorDashboard.Core.Codecs;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Health;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Provisioning;
using IoTSensorDashboard.Mqtt;

namespace IoTSensorDashboard.Dashboard;

/// <summary>
/// 데이터가 어디서 오고 있는가.
///
/// 🔴 세 상태를 <b>구분해</b> 표시한다.
///    브로커에 못 붙어 시뮬레이터로 돌고 있는데 화면이 그냥 숫자를 보여주면,
///    그건 <b>가짜 데이터를 실제인 것처럼</b> 그리는 것이다.
/// </summary>
public enum FeedMode
{
    /// <summary>아직 붙는 중.</summary>
    Connecting,

    /// <summary>실제 브로커를 구독 중.</summary>
    Live,

    /// <summary>⚠ 내부 시뮬레이터로 도는 중 — <b>반드시 화면에 표시한다.</b></summary>
    Demo
}

/// <summary>매장 한 곳의 표시값.</summary>
/// <param name="Online">
/// 온라인 센서 수. 분모(<paramref name="Total"/>)는 <b>있어야 할 명부</b> 기준이다.
/// </param>
public sealed record StoreStat(string SiteId, string Name, long In, long Out, int Online, int Total);

/// <summary>
/// 대시보드의 읽기 모델.
///
/// 🔑 대시보드는 <b>소비자</b>이지 원본 보관자가 아니다.
///    그래서 상한이 있는 인메모리 저장소를 쓴다 — 관제실의 SQLite 와 다르다.
/// </summary>
public sealed class DashboardModel : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CodecRegistry _codecs = new(new FlirCodec(), new MilesightCodec());
    private readonly SiteProvisioning _provisioning = new();
    private readonly SensorHealthTracker _health = new();
    private readonly SiteTree _tree;
    private readonly ScopePolicy _scope;

    private readonly Dictionary<string, long> _inBySite = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _outBySite = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _siteOfSensor = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _cts = new();
    private MqttIngestionSource? _ingest;

    private long _events;
    private bool _disposed;

    public DashboardModel()
    {
        _tree = new SiteTree(_provisioning.Sites);
        _scope = new ScopePolicy(_tree);

        // 🔑 분모의 출처. 등록하지 않으면 처음부터 죽어 있던 센서가 분모에서 빠진다.
        _health.Expect(_provisioning.SensorIds);
    }

    public FeedMode Mode { get; private set; } = FeedMode.Connecting;

    public long TotalEvents => Interlocked.Read(ref _events);

    public DateTimeOffset? LastMessageAt { get; private set; }

    public SiteProvisioning Provisioning => _provisioning;

    /// <summary>지금 보고 있는 권한 범위.</summary>
    public Role CurrentRole { get; private set; } = Role.TotalAdmin;

    public string? CurrentSiteId { get; private set; }

    public void SetScope(Role role, string? siteId)
    {
        CurrentRole = role;
        CurrentSiteId = siteId;
    }

    public async Task StartAsync()
    {
        _ingest = new MqttIngestionSource(clientId: "dashboard-view");
        _ = _ingest.RunAsync(OnRawAsync, _cts.Token);

        // 잠깐 기다렸다 붙지 못했으면 Demo 로 떨어뜨린다 — 다만 그 사실을 숨기지 않는다.
        _ = FallbackToDemoIfNeededAsync();

        await Task.CompletedTask;
    }

    private Task OnRawAsync(RawPayload raw)
    {
        var siteId = SensorTopic.SiteOf(raw.Source?.Replace("mqtt:", "", StringComparison.Ordinal));

        foreach (var e in _codecs.Decode(raw))
        {
            // 센서의 사이트 결정 순서:
            //   ① 스트림(토픽)에서 받은 siteId   ← 우선
            //   ② 프로비저닝 명부 폴백
            //   ③ 그래도 없으면 스코프 밖
            //
            // 🔴 ②가 없으면 사이트를 못 받은 센서를 건너뛰게 되고,
            //    센서가 전부 죽은 매장이 목록에서 **통째로 사라진다.**
            //    0 으로 표시되는 것보다 나쁘다 —
            //    0 은 「손님이 없었구나」지만 사라지면 「그런 매장이 없구나」가 된다.
            var resolved = siteId ?? _provisioning.SiteOf(e.SensorId);
            if (resolved is null) continue;

            lock (_gate)
            {
                _siteOfSensor[e.SensorId] = resolved;

                var target = string.Equals(e.Direction, "out", StringComparison.Ordinal)
                    ? _outBySite : _inBySite;

                target[resolved] = target.GetValueOrDefault(resolved) + e.Count;
            }

            // 🔑 헬스는 도착 시각 기준이다.
            _health.Observe(e.SensorId, raw.ReceivedAt);
            Interlocked.Increment(ref _events);
        }

        LastMessageAt = DateTimeOffset.UtcNow;
        Mode = FeedMode.Live;

        return Task.CompletedTask;
    }

    private async Task FallbackToDemoIfNeededAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8), _cts.Token).ConfigureAwait(false);

        // 아직 한 건도 못 받았고 연결도 안 됐으면 Demo 다.
        if (Mode == FeedMode.Connecting && _ingest?.IsConnected != true)
            Mode = FeedMode.Demo;
    }

    /// <summary>
    /// 매장별 표시값.
    ///
    /// 🔒 목록의 출처는 <b>프로비저닝 명부</b>다. 「데이터가 들어온 매장」이 아니다.
    ///    그래야 센서가 전부 죽은 매장도 목록에 남아 「측정 불가」로 보인다.
    /// </summary>
    public IReadOnlyList<StoreStat> Stores(DateTimeOffset now)
    {
        var visible = _scope.VisibleSites(CurrentRole, CurrentSiteId);
        var result = new List<StoreStat>();

        lock (_gate)
        {
            foreach (var siteId in _provisioning.StoreIds)
            {
                if (!visible.Contains(siteId)) continue;

                int online = 0;
                int total = 0;

                foreach (var sensor in _provisioning.Sensors)
                {
                    if (!string.Equals(sensor.SiteId, siteId, StringComparison.Ordinal)) continue;

                    total++;   // 🔑 「있어야 할 수」가 분모다
                    if (_health.Status(sensor.Id, now, HealthPolicy.Offline) == SensorStatus.Online) online++;
                }

                result.Add(new StoreStat(
                    siteId,
                    _provisioning.SiteName(siteId),
                    _inBySite.GetValueOrDefault(siteId),
                    _outBySite.GetValueOrDefault(siteId),
                    online,
                    total));
            }
        }

        return result;
    }

    /// <summary>권한 범위 안의 온라인 / 전체.</summary>
    public (int Online, int Total) OnlineSummary(DateTimeOffset now)
    {
        var stores = Stores(now);
        return (stores.Sum(s => s.Online), stores.Sum(s => s.Total));
    }

    public (long In, long Out) FlowSummary(DateTimeOffset now)
    {
        var stores = Stores(now);
        return (stores.Sum(s => s.In), stores.Sum(s => s.Out));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_ingest is not null) await _ingest.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
