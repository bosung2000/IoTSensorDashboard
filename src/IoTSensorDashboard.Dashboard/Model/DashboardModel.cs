using System.Diagnostics;
using IoTSensorDashboard.Core.Authorization;
using IoTSensorDashboard.Core.Codecs;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Health;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Provisioning;
using IoTSensorDashboard.Mqtt;

namespace IoTSensorDashboard.Dashboard.Model;

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

/// <summary>
/// 대시보드의 읽기 모델.
///
/// 🔑 대시보드는 <b>소비자</b>이지 원본 보관자가 아니다.
///    그래서 상한이 있는 인메모리 집계만 든다 — 관제실의 SQLite 와 다르다.
///    (긴 읽기로 공유 DB 의 WAL 체크포인트를 막아 수집을 굶기는 사고가 이 경계에서 난다.)
///
/// 🔒 <b>스레드</b>: 수집 콜백은 백그라운드, <see cref="Snapshot"/> 은 UI 스레드가 부른다.
///    모든 공유 상태는 <c>_gate</c> 로 지킨다.
/// </summary>
public sealed class DashboardModel : IAsyncDisposable
{
    /// <summary>스파크라인이 보관하는 표본 수. 초당 1점이면 약 2분.</summary>
    private const int TrendPoints = 120;

    /// <summary>
    /// 레이트 표본을 뜨는 최소 간격(초).
    ///
    /// 🔴 <b>왜 화면 갱신 주기와 분리하는가 — 실측 결함</b>:
    ///    화면은 수백 ms 마다 갱신된다. 그 짧은 창에서 매장당 초당 1.4건을 세면
    ///    델타가 <b>0 아니면 1</b> 로만 나온다(정수 이벤트라 그 사이 값이 없다).
    ///    그 결과 화면은 대부분 <c>0/s</c> 를 보여주고 가끔 <c>5/s</c> 로 튀었다 —
    ///    <b>누적은 계속 오르는데 속도는 0</b> 인, 설명할 수 없는 화면이 된다.
    ///
    /// 🔑 세는 창이 짧으면 「초당 몇 건」은 <b>측정할 수 없는 양</b>이 된다.
    ///    창을 1초로 넓히면 델타가 실제 레이트에 가까워진다.
    /// </summary>
    private const double TrendIntervalSeconds = 1.0;

    /// <summary>분 버킷 보관 개수 — 최근 1시간.</summary>
    private const int MinuteBuckets = 60;

    /// <summary>상태 로그 보관 줄 수.</summary>
    private const int LogLines = 60;

    /// <summary>
    /// 온라인 수 변화를 기록하는 최소 간격.
    ///
    /// 🔑 이보다 촘촘한 출렁임은 <b>한 줄로 합쳐서</b> 적는다.
    ///    (센서 1,000대의 숨소리를 전부 적으면 로그가 그것만으로 가득 찬다.)
    /// </summary>
    private static readonly TimeSpan OnlineLogInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly CodecRegistry _codecs = new(new FlirCodec(), new MilesightCodec());
    private readonly SiteProvisioning _provisioning = new();
    private readonly SensorHealthTracker _health = new();
    private readonly SiteTree _tree;
    private readonly ScopePolicy _scope;

    private readonly Dictionary<string, long> _inBySite = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _outBySite = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _inBySensor = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _outBySensor = new(StringComparer.Ordinal);

    /// <summary>분 버킷 → (유입, 유출). 표시 시각의 「분」으로 자른다(I3).</summary>
    private readonly Dictionary<DateTimeOffset, long[]> _minutes = [];

    /// <summary>매장별 레이트 추이. 값은 <b>초당</b>이다.</summary>
    private readonly Dictionary<string, Queue<double>> _trendIn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<double>> _trendOut = new(StringComparer.Ordinal);

    /// <summary>직전 표본의 매장별 누적 — 델타로 레이트를 낸다.</summary>
    private readonly Dictionary<string, long> _prevIn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _prevOut = new(StringComparer.Ordinal);

    private readonly Queue<LogEntry> _log = new();

    /// <summary>
    /// 표본 간 <b>실제</b> 경과 시간.
    ///
    /// ⚠️ 「초당」이라 쓸 거면 타이머 간격이 아니라 이걸로 나눈다.
    ///    타이머는 best-effort 라 부하가 커지면 밀리는데, 밀린 만큼 델타가 커지고
    ///    라벨은 그대로여서 <b>바쁠수록 과대 표시</b>된다(실제로 8배 부풀린 적이 있다).
    /// </summary>
    private readonly Stopwatch _sinceSample = Stopwatch.StartNew();

    private readonly CancellationTokenSource _cts = new();
    private MqttIngestionSource? _ingest;

    /// <summary>
    /// 센서 상태를 한 번에 받아 두는 버퍼 — <b>프레임마다 재사용</b>한다.
    ///
    /// 🔑 매 프레임 새로 만들면 1,000칸 배열이 초당 30개씩 쌓인다.
    ///    화면 갱신 경로에서의 할당은 그 자체가 부하다.
    /// </summary>
    private SensorStatus[] _statusBuffer = [];

    private long _events;
    private FeedMode _mode = FeedMode.Connecting;
    private int _prevOnline = -1;
    private DateTimeOffset _lastOnlineLogAt;
    private bool _disposed;

    public DashboardModel()
    {
        _tree = new SiteTree(_provisioning.Sites);
        _scope = new ScopePolicy(_tree);

        // 🔑 분모의 출처. 등록하지 않으면 처음부터 죽어 있던 센서가 분모에서 빠진다.
        _health.Expect(_provisioning.SensorIds);

        Log("피드", "대시보드 시작 · 브로커 탐지 중");
    }

    public FeedMode Mode
    {
        get { lock (_gate) return _mode; }
    }

    public long TotalEvents => Interlocked.Read(ref _events);

    public DateTimeOffset? LastMessageAt
    {
        get { lock (_gate) return _lastMessageAt; }
    }

    private DateTimeOffset? _lastMessageAt;

    public SiteProvisioning Provisioning => _provisioning;

    /// <summary>지금 보고 있는 권한 범위.</summary>
    public Role CurrentRole { get; private set; } = Role.TotalAdmin;

    public string? CurrentSiteId { get; private set; }

    /// <summary>현재 범위의 표시 이름.</summary>
    public string ScopeLabel => CurrentSiteId is null
        ? "전체"
        : _tree.NameOf(CurrentSiteId) ?? CurrentSiteId;

    public void SetScope(Role role, string? siteId)
    {
        CurrentRole = role;
        CurrentSiteId = siteId;

        Log("범위", $"권한 범위 전환 · {(siteId is null ? "전체" : _tree.NameOf(siteId) ?? siteId)}");
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

            bool isOut = string.Equals(e.Direction, "out", StringComparison.Ordinal);

            lock (_gate)
            {
                var bySite = isOut ? _outBySite : _inBySite;
                bySite[resolved] = bySite.GetValueOrDefault(resolved) + e.Count;

                var bySensor = isOut ? _outBySensor : _inBySensor;
                bySensor[e.SensorId] = bySensor.GetValueOrDefault(e.SensorId) + e.Count;

                // 🔑 버킷은 **발생 시각**으로 자른다(도착 시각이 아니라).
                //    네트워크가 밀려 늦게 온 데이터가 「지금」 칸에 쌓이면
                //    시간대별 추이가 실제와 어긋난다.
                AddMinute(e.OccurredAt, isOut, e.Count);
            }

            // 🔑 헬스는 **도착** 시각 기준이다 — 「이 센서가 살아 있는가」의 질문이므로.
            _health.Observe(e.SensorId, raw.ReceivedAt);
            Interlocked.Increment(ref _events);
        }

        lock (_gate)
        {
            _lastMessageAt = DateTimeOffset.UtcNow;

            if (_mode != FeedMode.Live)
            {
                _mode = FeedMode.Live;
                LogLocked("피드", "라이브 · 관제실 브로커 구독 중");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>분 버킷에 더한다. 호출부가 <c>_gate</c> 를 잡고 있어야 한다.</summary>
    private void AddMinute(DateTimeOffset at, bool isOut, long count)
    {
        var local = at.ToLocalTime();
        var bucket = new DateTimeOffset(
            local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, local.Offset);

        if (!_minutes.TryGetValue(bucket, out var pair))
        {
            pair = new long[2];
            _minutes[bucket] = pair;

            // 🔒 상한을 여기서 지킨다 — 안 그러면 이 사전이 무한히 커진다.
            //    (대시보드가 원본 보관자가 되어 버리는 경로가 정확히 이것이다.)
            if (_minutes.Count > MinuteBuckets)
            {
                var oldest = _minutes.Keys.Min();
                _minutes.Remove(oldest);
            }
        }

        pair[isOut ? 1 : 0] += count;
    }

    private async Task FallbackToDemoIfNeededAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8), _cts.Token).ConfigureAwait(false);

        lock (_gate)
        {
            // 아직 한 건도 못 받았고 연결도 안 됐으면 Demo 다.
            if (_mode == FeedMode.Connecting && _ingest?.IsConnected != true)
            {
                _mode = FeedMode.Demo;
                LogLocked("피드", "⚠ 브로커에 붙지 못함 — 관제실이 떠 있는지 확인");
            }
        }
    }

    /// <summary>
    /// 이 순간의 화면 전체를 한 번에 뜬다.
    ///
    /// 🔑 <b>레이트 표본도 여기서 갱신된다</b> — 즉 이 메서드는 순수 조회가 아니다.
    ///    한 곳에서만 표본을 뜨게 해야 「초당」의 기준이 화면 전체에서 하나로 유지된다.
    ///    (패널마다 각자 재면 같은 화면에 서로 다른 /s 가 뜬다.)
    ///
    /// 🔒 UI 스레드에서 부른다. 무거운 IO 는 여기 없다 — 전부 인메모리 집계다.
    /// </summary>
    public DashboardSnapshot Snapshot(DateTimeOffset now)
    {
        var visible = _scope.VisibleSites(CurrentRole, CurrentSiteId);

        // ⚠️ 실제 경과 시간으로 나눈다(타이머 간격이 아니라).
        //    타이머는 best-effort 라 부하가 커지면 밀리는데, 밀린 만큼 델타가 커지고
        //    라벨은 그대로여서 바쁠수록 과대 표시된다.
        double elapsed = _sinceSample.Elapsed.TotalSeconds;

        // 🔑 창이 아직 안 찼으면 표본을 뜨지 않는다 — 짧은 창의 델타는 실제 레이트가 아니다.
        //    이때 시계열은 **직전 것을 그대로** 돌려준다(없는 값을 지어내지 않는다).
        bool sample = elapsed >= TrendIntervalSeconds;
        if (sample) _sinceSample.Restart();

        // 🔴 센서 상태를 **락 한 번**으로 통째로 받아 온다.
        //    하나씩 물으면 프레임당 락 1,000회가 되고, 그 락은 수집이 이벤트마다
        //    잡는 것과 같아 화면을 그리느라 수집이 굶는다(실측으로 확인한 부류).
        var sensors = _provisioning.Sensors;
        if (_statusBuffer.Length < sensors.Count) _statusBuffer = new SensorStatus[sensors.Count];

        _health.StatusesInto(_provisioning.SensorIds, now, HealthPolicy.Offline, _statusBuffer);

        lock (_gate)
        {
            var stores = new List<StoreStat>();
            var trends = new List<StoreTrend>();

            foreach (var siteId in _provisioning.StoreIds)
            {
                // 🔒 목록의 출처는 **프로비저닝 명부**다. 「데이터가 들어온 매장」이 아니다.
                //    그래야 센서가 전부 죽은 매장도 목록에 남아 「측정 불가」로 보인다.
                if (!visible.Contains(siteId)) continue;

                int online = 0;
                int total = 0;
                int unknown = 0;

                // 🔒 위에서 받아 둔 버퍼를 읽는다 — 여기서 추적기를 다시 부르지 않는다.
                //    SensorIds 와 Sensors 는 같은 순서로 만들어지므로 인덱스가 대응한다.
                for (int i = 0; i < sensors.Count; i++)
                {
                    if (!string.Equals(sensors[i].SiteId, siteId, StringComparison.Ordinal)) continue;

                    total++;   // 🔑 「있어야 할 수」가 분모다

                    // 🔴 Online 이 아닌 것을 전부 「오프라인」으로 뭉치지 않는다.
                    //    한 번도 못 본 것(Unknown)은 장애가 아니라 **미관측**이고,
                    //    원인도 처리도 다르다(설치·배선·명부 ↔ 장애 조치).
                    switch (_statusBuffer[i])
                    {
                        case SensorStatus.Online: online++; break;
                        case SensorStatus.Unknown: unknown++; break;
                    }
                }

                long inSum = _inBySite.GetValueOrDefault(siteId);
                long outSum = _outBySite.GetValueOrDefault(siteId);

                stores.Add(new StoreStat(
                    siteId, _provisioning.SiteName(siteId), GroupOf(siteId),
                    inSum, outSum, online, total, unknown));

                trends.Add(new StoreTrend(
                    siteId,
                    _provisioning.SiteName(siteId),
                    PushTrend(_trendIn, _prevIn, siteId, inSum, elapsed, sample),
                    PushTrend(_trendOut, _prevOut, siteId, outSum, elapsed, sample)));
            }

            int onlineTotal = stores.Sum(s => s.Online);
            int sensorTotal = stores.Sum(s => s.Total);

            NoteOnlineChange(onlineTotal, sensorTotal, now);

            return new DashboardSnapshot
            {
                TakenAt = now.ToLocalTime(),
                Role = CurrentRole,
                ScopeLabel = ScopeLabel,
                TotalIn = stores.Sum(s => s.In),
                TotalOut = stores.Sum(s => s.Out),
                UniqueEvents = Interlocked.Read(ref _events),
                OnlineSensors = onlineTotal,
                TotalSensors = sensorTotal,
                UnknownSensors = stores.Sum(s => s.Unknown),
                LastEventAt = _lastMessageAt?.ToLocalTime(),
                Groups = BuildGroups(stores),
                Stores = stores,
                TopThroughput = TopSensors(visible, static s => s.Throughput),
                TopIn = TopSensors(visible, static s => s.In),
                TopOut = TopSensors(visible, static s => s.Out),
                Trends = trends,
                Minutes = BuildMinutes(),
                Log = [.. _log.Reverse()],
            };
        }
    }

    /// <summary>매장이 속한 본부 ID.</summary>
    private string GroupOf(string siteId)
    {
        int dash = siteId.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 ? siteId[..dash] : siteId;
    }

    /// <summary>
    /// 레이트 표본을 하나 밀어 넣고 시계열을 돌려준다.
    ///
    /// 🔑 첫 표본은 <b>버린다</b>(0 으로 넣는다). 직전 누적을 모르는 상태에서
    ///    현재 누적을 그대로 델타로 쓰면 <b>시작 순간에만 거대한 봉우리</b>가 생겨
    ///    그래프의 세로 축이 통째로 찌그러진다.
    /// </summary>
    /// <param name="sample">
    /// <c>false</c> 면 <b>아무것도 바꾸지 않고</b> 지금 시계열만 돌려준다.
    /// 화면 갱신은 표본 주기보다 잦으므로, 그때마다 밀어 넣으면 창이 너무 짧아진다.
    /// </param>
    private static List<double> PushTrend(
        Dictionary<string, Queue<double>> series, Dictionary<string, long> previous,
        string siteId, long cumulative, double elapsed, bool sample)
    {
        if (!series.TryGetValue(siteId, out var queue))
        {
            queue = new Queue<double>(TrendPoints);
            series[siteId] = queue;
        }

        if (!sample) return [.. queue];

        double rate = previous.TryGetValue(siteId, out long before)
            ? Math.Max(0, cumulative - before) / Math.Max(0.05, elapsed)
            : 0;

        previous[siteId] = cumulative;

        queue.Enqueue(rate);
        while (queue.Count > TrendPoints) queue.Dequeue();

        return [.. queue];
    }

    private List<GroupStat> BuildGroups(List<StoreStat> stores)
    {
        return [.. stores
            .GroupBy(s => s.GroupId, StringComparer.Ordinal)
            .Select(g => new GroupStat(
                g.Key,
                _tree.NameOf(g.Key) ?? g.Key,
                g.Sum(s => s.In),
                g.Sum(s => s.Out),
                g.Sum(s => s.Online),
                g.Sum(s => s.Total),
                g.Sum(s => s.Unknown)))
            .OrderBy(g => g.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 상위 5개 센서. <paramref name="visible"/> 범위 밖 센서는 제외한다(I4).
    ///
    /// 🔒 순위에서 권한을 빠뜨리기 쉽다 — 합계는 걸러 놓고 순위는 전체에서 뽑으면
    ///    <b>볼 권한이 없는 매장의 센서 ID 가 화면에 뜬다.</b>
    /// </summary>
    private List<SensorStat> TopSensors(IReadOnlySet<string> visible, Func<SensorStat, long> key)
    {
        var stats = new List<SensorStat>();

        foreach (var sensor in _provisioning.Sensors)
        {
            if (!visible.Contains(sensor.SiteId)) continue;

            long inSum = _inBySensor.GetValueOrDefault(sensor.Id);
            long outSum = _outBySensor.GetValueOrDefault(sensor.Id);

            if (inSum == 0 && outSum == 0) continue;

            stats.Add(new SensorStat(sensor.Id, _provisioning.SiteName(sensor.SiteId), inSum, outSum));
        }

        return [.. stats.OrderByDescending(key).ThenBy(s => s.SensorId, StringComparer.Ordinal).Take(5)];
    }

    private List<MinutePoint> BuildMinutes() =>
        [.. _minutes
            .OrderBy(kv => kv.Key)
            .Select(kv => new MinutePoint(kv.Key, kv.Value[0], kv.Value[1]))];

    /// <summary>
    /// 온라인 수가 변하면 기록한다.
    ///
    /// 🔑 <b>조용한 죽음을 남기는 자리다.</b> 센서가 하나 죽어도 화면의 큰 숫자는
    ///    거의 안 변하므로(999/1000) 눈으로는 못 잡는다. 로그에 남아야 나중에 셀 수 있다.
    ///
    /// 🔴 <b>다만 매 틱 기록하면 로그가 못 쓰게 된다 — 실측 결함.</b>
    ///    센서 1,000대가 각자 다른 주기로 숨 쉬면 온라인 수는 <b>초당 여러 번</b> 출렁인다.
    ///    그대로 남겼더니 「감지 1대 / 복구 1대」가 몇 초 만에 버퍼를 가득 채워
    ///    <b>연결·권한 전환 같은 진짜 사건이 밀려 나갔다.</b>
    ///    남는 게 많아서 아무것도 못 찾는 로그는 없는 로그와 같다.
    ///
    /// 📌 그래서 <b>잔물결은 접고 추세만 남긴다</b> — 최소 간격을 두고, 그 사이의 변화는
    ///    합쳐서 한 줄로 적는다. 순간값을 감추는 게 아니다:
    ///    <b>지금 온라인 수는 KPI 에 언제나 정확히 떠 있다.</b> 여기 남는 건 「변화의 이력」이다.
    /// </summary>
    private void NoteOnlineChange(int online, int total, DateTimeOffset now)
    {
        if (_prevOnline < 0)
        {
            _prevOnline = online;
            _lastOnlineLogAt = now;
            return;
        }

        if (online == _prevOnline) return;
        if (now - _lastOnlineLogAt < OnlineLogInterval) return;

        int delta = online - _prevOnline;
        _prevOnline = online;
        _lastOnlineLogAt = now;

        LogLocked("센서", delta < 0
            ? $"오프라인 {-delta:N0}대 증가 · 현재 {online:N0}/{total:N0}"
            : $"복구 {delta:N0}대 · 현재 {online:N0}/{total:N0}");
    }

    private void Log(string kind, string message)
    {
        lock (_gate) LogLocked(kind, message);
    }

    /// <summary>호출부가 <c>_gate</c> 를 잡고 있어야 한다.</summary>
    private void LogLocked(string kind, string message)
    {
        _log.Enqueue(new LogEntry(DateTimeOffset.Now, kind, message));
        while (_log.Count > LogLines) _log.Dequeue();
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
