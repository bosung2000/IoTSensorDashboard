using IoTSensorDashboard.Core.Provisioning;

namespace IoTSensorDashboard.Mqtt;

/// <summary>
/// 가상 센서 1,000대의 상태와 발행 계획.
///
/// 🔑 이 앱은 「테스트 도구」가 아니라 납품물이다.
///    실제 센서 없이 이 시스템의 신뢰성을 증명하는 <b>유일한 수단</b>이고,
///    인수 검증도 이 앱으로 악조건을 의도적으로 재현해서 한다.
///
/// 이 클래스는 <b>MQTT 를 모른다</b> — 무엇을 발행할지만 정하고, 발행은 호출 측이 한다.
/// 그래서 브로커 없이 헤드리스로 검증할 수 있다.
/// </summary>
public sealed class SensorFarmEngine
{
    /// <summary>
    /// 센서당 오프라인 버퍼 크기.
    ///
    /// 초당 1건 기준 약 10분치다. 그보다 긴 단절에서는 오래된 것부터 폐기된다.
    ///
    /// 🔑 무한 버퍼는 불가능하므로 이건 <b>설계상 한계이지 결함이 아니다.</b>
    ///    결함이었던 것은 한계가 아니라 <b>한계를 감춘 것</b>이었다 —
    ///    화면이 「유실 0」을 하드코딩된 문자열로 띄우고 있어서
    ///    실제로 폐기가 일어나는데도 무손실이라고 말했다.
    /// </summary>
    public const int BufferCap = 600;

    /// <summary>「현실 모드」의 묶음 주기. 실제 인원 카운터는 사람마다 쏘지 않고 분당 1건으로 묶어 보낸다.</summary>
    public static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(60);

    /// <summary>한 센서가 1초에 셀 수 있는 인원 상한. I7 정합 한계(100)를 넘지 않게.</summary>
    public const int MaxPeoplePerSecondPerSensor = 1;

    /// <summary>발행 루프 틱. UI 부드러움과 발행 배치 크기의 균형점.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>물리적으로 불가능한 값 — I7 격리를 시연하기 위한 것.</summary>
    public const int AnomalyCount = 5_000;

    private readonly object _gate = new();
    private readonly SiteProvisioning _provisioning;
    private readonly Random _random;

    private readonly string[] _sensorIds;
    private readonly string[] _vendors;
    private readonly string[] _siteIds;
    private readonly bool[] _offline;
    private readonly Queue<SensorReading>[] _buffers;

    private long _droppedByCap;
    private int _cursor;

    public SensorFarmEngine(SiteProvisioning provisioning, int randomSeed = 20260709)
    {
        ArgumentNullException.ThrowIfNull(provisioning);

        _provisioning = provisioning;
        _random = new Random(randomSeed);   // 시드 고정 — 같은 조건이면 같은 결과가 나와야 검증이 된다

        int n = provisioning.Sensors.Count;
        _sensorIds = new string[n];
        _vendors = new string[n];
        _siteIds = new string[n];
        _offline = new bool[n];
        _buffers = new Queue<SensorReading>[n];

        for (int i = 0; i < n; i++)
        {
            var sensor = provisioning.Sensors[i];
            _sensorIds[i] = sensor.Id;
            _vendors[i] = sensor.Vendor ?? SiteProvisioning.VendorFor(i);
            _siteIds[i] = sensor.SiteId;
            _buffers[i] = new Queue<SensorReading>();
        }
    }

    public int SensorCount => _sensorIds.Length;

    /// <summary>버퍼 상한을 밖에서 읽을 수 있게. 화면이 "약 10분치"를 계산할 때 쓴다.</summary>
    public static int BufferCapacity => BufferCap;

    /// <summary>
    /// 버퍼 상한 초과로 폐기된 이벤트 누적 = <b>실제 유실</b>.
    ///
    /// 🔴 감추지 말 것. 0 이 아니면 경고색으로 「유실 N건 (버퍼 상한 초과)」를 표시한다.
    ///
    /// 📏 원칙: 신뢰 문구는 측정값에만 붙인다.
    ///    「유실 0」을 쓰려면 그걸 <b>반증할 수 있는 카운터</b>가 반드시 함께 있어야 한다.
    ///    카운터 없는 안심 문구는 거짓말이 될 준비가 된 문구다.
    /// </summary>
    public long DroppedByBufferCap => Interlocked.Read(ref _droppedByCap);

    /// <summary>지금 살아 있는 센서 수.</summary>
    public int OnlineCount
    {
        get { lock (_gate) return _offline.Count(o => !o); }
    }

    /// <summary>지금 버퍼에 쌓여 있는 총 건수. 복구 시 이만큼 백필된다.</summary>
    public int BufferedCount
    {
        get { lock (_gate) return _buffers.Sum(b => b.Count); }
    }

    public bool IsOnline(string sensorId)
    {
        int i = IndexOf(sensorId);
        if (i < 0) return false;
        lock (_gate) return !_offline[i];
    }

    /// <summary>
    /// 센서를 죽이거나 살린다.
    ///
    /// 오프라인이 되면 MQTT 발행을 멈추고 내부 링버퍼에 쌓는다.
    /// 복구되면 <see cref="DrainBackfill"/> 로 쌓인 것을 원본 시각 그대로 내보낸다.
    /// </summary>
    public void SetOffline(string sensorId, bool offline)
    {
        int i = IndexOf(sensorId);
        if (i < 0) return;
        lock (_gate) _offline[i] = offline;
    }

    /// <summary>인덱스로 토글 — 화면의 타일 클릭에 대응.</summary>
    public void SetOfflineAt(int index, bool offline)
    {
        if (index < 0 || index >= _sensorIds.Length) return;
        lock (_gate) _offline[index] = offline;
    }

    /// <summary>
    /// 이번 틱에 관측된 것을 만든다.
    ///
    /// 온라인 센서 것은 반환되어 곧바로 발행되고,
    /// 오프라인 센서 것은 <b>버퍼에 쌓인다</b>(반환되지 않는다).
    /// </summary>
    /// <param name="readingCount">이번 틱에 관측할 건수. 발행 속도에서 계산해 넘긴다.</param>
    public IReadOnlyList<SensorReading> Tick(DateTimeOffset now, int readingCount)
    {
        if (readingCount <= 0 || _sensorIds.Length == 0) return [];

        var toPublish = new List<SensorReading>(readingCount);

        lock (_gate)
        {
            for (int k = 0; k < readingCount; k++)
            {
                int i = _cursor;
                _cursor = (_cursor + 1) % _sensorIds.Length;

                var reading = new SensorReading(
                    _sensorIds[i], _vendors[i], _siteIds[i], now,
                    In: _random.Next(0, MaxPeoplePerSecondPerSensor + 1),
                    Out: _random.Next(0, MaxPeoplePerSecondPerSensor + 1));

                if (_offline[i])
                    BufferLocked(i, reading);
                else
                    toPublish.Add(reading);
            }
        }

        return toPublish;
    }

    /// <summary>
    /// 복구된 센서의 버퍼를 비워 백필 목록으로 돌려준다.
    ///
    /// 🔑 <b>원본 타임스탬프 그대로</b> 발행한다. 지금 시각으로 바꾸면
    ///    "10분 전에 온 손님"이 "방금 온 손님"이 되어 시간대별 통계가 통째로 어긋난다.
    ///
    ///    중복은 수신 측의 멱등 판정(I1)이 접으므로, 겹쳐 보내도 두 번 세어지지 않는다.
    /// </summary>
    public IReadOnlyList<SensorReading> DrainBackfill(string sensorId)
    {
        int i = IndexOf(sensorId);
        if (i < 0) return [];

        lock (_gate)
        {
            if (_buffers[i].Count == 0) return [];

            var drained = _buffers[i].ToArray();
            _buffers[i].Clear();
            return drained;
        }
    }

    /// <summary>온라인 상태인 모든 센서의 버퍼를 비운다. 일괄 복구용.</summary>
    public IReadOnlyList<SensorReading> DrainAllBackfill()
    {
        lock (_gate)
        {
            var all = new List<SensorReading>();
            for (int i = 0; i < _buffers.Length; i++)
            {
                if (_offline[i] || _buffers[i].Count == 0) continue;

                all.AddRange(_buffers[i]);
                _buffers[i].Clear();
            }
            return all;
        }
    }

    /// <summary>
    /// 물리적으로 불가능한 값 한 건을 만든다 — I7 격리를 실제로 시연한다.
    ///
    /// 🔑 화면만 바꾸는 게 아니라 <b>진짜로 발행</b>해야 한다.
    ///    그래야 관제실이 실제로 격리하는지 증명된다.
    /// </summary>
    public SensorReading CreateAnomaly(DateTimeOffset now)
    {
        lock (_gate)
        {
            int i = _random.Next(_sensorIds.Length);
            return new SensorReading(_sensorIds[i], _vendors[i], _siteIds[i], now, AnomalyCount, 0);
        }
    }

    /// <summary>
    /// 핑에 응답할 센서 목록.
    ///
    /// 🔒 <b>온라인 센서만</b> 돌려준다. 죽은 센서가 ACK 를 보내면
    ///    관제실이 그 센서를 영원히 못 찾는다.
    /// </summary>
    /// <param name="pingBody">"*" 또는 빈 값이면 전체, 아니면 줄바꿈으로 구분된 센서 ID 목록.</param>
    public IReadOnlyList<string> AckTargets(string? pingBody)
    {
        lock (_gate)
        {
            bool all = string.IsNullOrWhiteSpace(pingBody) || pingBody.Trim() == "*";

            if (all)
            {
                var result = new List<string>();
                for (int i = 0; i < _sensorIds.Length; i++)
                    if (!_offline[i]) result.Add(_sensorIds[i]);
                return result;
            }

            var asked = pingBody!
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);

            var picked = new List<string>();
            for (int i = 0; i < _sensorIds.Length; i++)
                if (!_offline[i] && asked.Contains(_sensorIds[i])) picked.Add(_sensorIds[i]);
            return picked;
        }
    }

    /// <summary>이 센서가 속한 지점 ID.</summary>
    public string? SiteOf(string sensorId) => _provisioning.SiteOf(sensorId);

    /// <summary>
    /// 버퍼에 넣는다. 가득 차면 <b>가장 오래된 것을 버리고 센다.</b>
    ///
    /// 🔴 조용히 버리지 않는다. 이 카운터가 곧 "무손실이 아니었다"는 증거다.
    /// </summary>
    private void BufferLocked(int i, SensorReading reading)
    {
        var buffer = _buffers[i];

        if (buffer.Count >= BufferCap)
        {
            buffer.Dequeue();
            Interlocked.Increment(ref _droppedByCap);
        }

        buffer.Enqueue(reading);
    }

    private int IndexOf(string? sensorId)
    {
        if (string.IsNullOrEmpty(sensorId)) return -1;
        return Array.IndexOf(_sensorIds, sensorId);
    }
}
