namespace IoTSensorDashboard.Core.Health;

/// <summary>
/// 센서의 생사를 판정한다 (I5).
///
/// 🔴 <b>이 시스템에서 가장 자주 틀린 자리다.</b>
///    사각지대가 세 번 연속 여기서 나왔고, 세 번 다 <b>분모</b> 문제였다.
/// </summary>
public sealed class SensorHealthTracker
{
    /// <summary>5분 넘는 공백은 「주기」가 아니라 사고다. 주기 학습 표본에서 제외한다.</summary>
    private const double MaxSampleSec = 300;

    /// <summary>지수평활 계수. 빠르게 따라가되 한 번 튄 값에 흔들리지 않게.</summary>
    private const double Alpha = 0.3;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _cadenceSec = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expected = new(StringComparer.Ordinal);

    /// <summary>
    /// 「있어야 할 센서 명부」를 등록한다(프로비저닝).
    ///
    /// 🔑 이게 분모의 출처다. 등록하지 않으면
    ///    <b>처음부터 죽어 있던 센서가 분모에서 통째로 빠진다.</b>
    /// </summary>
    public void Expect(IEnumerable<string> sensorIds)
    {
        ArgumentNullException.ThrowIfNull(sensorIds);

        lock (_gate)
            foreach (var id in sensorIds)
                if (!string.IsNullOrWhiteSpace(id)) _expected.Add(id);
    }

    /// <summary>
    /// 신호가 도착했음을 기록한다.
    /// </summary>
    /// <param name="receivedAt">
    /// 🔴 <b>호스트 도착 시각</b>이다. 센서가 payload 에 담아 보낸 기기 시각이 아니다.
    ///
    /// 📌 근거: 기기의 시계 오차로 <b>미래 시각</b>이 오면 그 센서는 영원히 온라인으로 오염된다.
    ///    `now - last` 가 계속 음수라 임계를 절대 넘지 않기 때문이다.
    ///
    /// | 시각            | 어디에 쓰나              |
    /// |-----------------|--------------------------|
    /// | 호스트 도착 시각 | **헬스 판정** (여기)      |
    /// | 기기 시각        | 집계(시간대별 통계, I3)   |
    ///
    /// 두 시각을 섞지 말 것. 용도가 완전히 다르다.
    /// </param>
    public void Observe(string sensorId, DateTimeOffset receivedAt)
    {
        if (string.IsNullOrWhiteSpace(sensorId)) return;

        lock (_gate)
        {
            bool had = _lastSeen.TryGetValue(sensorId, out var prev);

            // 주기는 도착 간격에서만 배운다.
            if (had && receivedAt > prev)
                LearnCadenceLocked(sensorId, (receivedAt - prev).TotalSeconds);

            // 🔒 과거 시각으로 되돌리지 않는다.
            //    백필(과거 데이터 몰아 보내기)이 마지막 수신 시각을 뒤로 밀면
            //    살아 있는 센서가 죽은 것처럼 보인다.
            if (!had || receivedAt > prev) _lastSeen[sensorId] = receivedAt;
        }
    }

    /// <summary>이 센서의 지금 상태.</summary>
    public SensorStatus Status(string sensorId, DateTimeOffset now, HealthPolicy policy)
    {
        lock (_gate) return StatusLocked(sensorId, now, policy);
    }

    /// <summary>
    /// 여러 센서의 상태를 <b>락 한 번</b>으로 읽는다.
    ///
    /// 🔴 <b>왜 따로 있는가 — 실측 결함.</b>
    ///    화면이 센서 1,000대를 돌며 <see cref="Status"/> 를 하나씩 불렀다.
    ///    그건 <b>프레임당 락 1,000회</b>이고, 창이 활성이면 틱이 33ms 라
    ///    <b>초당 3만 번</b>이 된다. 그 락은 수집이 <b>이벤트마다</b> 잡는 것과 같으므로
    ///    (<see cref="Observe"/>) 화면을 그리느라 수집이 굶는다.
    ///
    /// 📌 이 프로젝트에서 같은 부류가 세 번째다(센서 팜 타일 → 화면 정지).
    ///    <b>「한 번은 무해한 호출」이 고빈도에서 흉기가 된다.</b>
    ///
    /// 🔑 호출부가 결과 배열을 <b>재사용</b>하면 프레임당 할당도 0 이 된다.
    /// </summary>
    /// <param name="into">
    /// 결과를 받을 배열. <paramref name="sensorIds"/> 와 <b>길이가 같아야</b> 한다.
    /// </param>
    public void StatusesInto(
        IReadOnlyList<string> sensorIds, DateTimeOffset now, HealthPolicy policy, SensorStatus[] into)
    {
        ArgumentNullException.ThrowIfNull(sensorIds);
        ArgumentNullException.ThrowIfNull(into);

        if (into.Length < sensorIds.Count)
            throw new ArgumentException("결과 배열이 센서 수보다 짧다.", nameof(into));

        lock (_gate)
            for (int i = 0; i < sensorIds.Count; i++)
                into[i] = StatusLocked(sensorIds[i], now, policy);
    }

    /// <summary>호출부가 <c>_gate</c> 를 잡고 있어야 한다.</summary>
    private SensorStatus StatusLocked(string sensorId, DateTimeOffset now, HealthPolicy policy)
    {
        if (!_lastSeen.TryGetValue(sensorId, out var last))
            return SensorStatus.Unknown;   // 한 번도 본 적 없다

        // 🔑 임계와 **정확히 같은** 시각은 Online 이다(<= 비교).
        //    경계에서 깜빡이면 장애 알림이 무의미해진다.
        return (now - last) <= policy.For(CadenceLocked(sensorId))
            ? SensorStatus.Online
            : SensorStatus.Offline;
    }

    /// <summary>
    /// 온라인 / 오프라인 / 전체.
    ///
    /// 🔴 <b>분모 = 프로비저닝된 명부 ∪ 관측된 센서</b>
    ///    「지금까지 신호를 받아본 센서」가 아니다.
    ///
    /// 📌 근거 — 실제로 무슨 일이 있었나:
    ///    분모가 「관측된 센서」라 처음부터 죽어 있던 센서가 통째로 빠졌다.
    ///    1,000대 중 <b>50대가 무응답인데</b> 화면은 <b>950 / 950 = 가동률 100%</b> 를 띄웠다.
    ///
    ///    이 부류가 가장 찾기 어렵다 — <b>처음부터 없는 것은 영원히 안 보이기</b> 때문이다.
    ///    화면에 아무 이상 징후가 없고, 숫자는 오히려 완벽해 보인다.
    ///
    /// 🧭 비율을 그리는 코드를 보면 반드시 분모의 출처를 물을 것.
    ///    「있어야 할 것」인가 「이미 본 것」인가.
    /// </summary>
    public (int Online, int Offline, int Total) Summary(DateTimeOffset now, HealthPolicy policy)
    {
        lock (_gate)
        {
            int online = 0;
            foreach (var kv in _lastSeen)
                if ((now - kv.Value) <= policy.For(CadenceLocked(kv.Key))) online++;

            int total = _lastSeen.Count;
            foreach (var id in _expected)
                if (!_lastSeen.ContainsKey(id)) total++;   // 🔑 무응답분도 분모에 남긴다

            return (online, total - online, total);
        }
    }

    /// <summary>
    /// 지금 물어볼 센서들.
    ///
    /// 두 집합을 <b>합쳐야</b> 한다:
    ///   ① 관측 이력이 있는데 임계 동안 조용한 센서
    ///   ② 한 번도 신호가 없던 센서
    ///
    /// 📌 ②를 빼면 생기는 일(테스트가 출하 전에 잡은 회귀):
    ///    기동 직후처럼 아직 아무 신호도 없으면 목록이 비고,
    ///    「물어볼 대상 0」으로 읽혀 <b>아무에게도 묻지 않게 된다.</b>
    ///    → 센서가 영원히 미확인으로 남는다.
    ///
    /// 데이터가 도는 동안 이 목록은 자연히 비므로,
    /// <b>부하가 클 때 핑 트래픽이 0 에 수렴</b>하는 성질은 그대로다.
    /// </summary>
    public IReadOnlyList<string> ProbeTargets(DateTimeOffset now, HealthPolicy policy)
    {
        lock (_gate)
        {
            var list = new List<string>();

            foreach (var kv in _lastSeen)
                if ((now - kv.Value) > policy.For(CadenceLocked(kv.Key))) list.Add(kv.Key);

            foreach (var id in _expected)
                if (!_lastSeen.ContainsKey(id)) list.Add(id);

            return list;
        }
    }

    /// <summary>
    /// 프로비저닝돼 있는데 한 번도 신호가 없었던 센서.
    ///
    /// 🔑 「끊김」과 분리해서 보여준다. 원인이 설치·배선 문제라 담당자가 다르다.
    /// </summary>
    public IReadOnlyList<string> NeverSeenIds()
    {
        lock (_gate)
            return _expected.Where(id => !_lastSeen.ContainsKey(id)).ToList();
    }

    /// <summary>
    /// 관측된 발신 주기. 아직 모르면 null.
    ///
    /// 진단 화면에서 「이 센서는 원래 이만큼 조용하다」를 보여줄 수 있어야 한다 —
    /// 임계가 센서마다 다르므로, 근거를 못 보여주면 판정을 믿을 수 없다.
    /// </summary>
    public TimeSpan? Cadence(string sensorId)
    {
        lock (_gate) return CadenceLocked(sensorId);
    }

    /// <summary>마지막으로 신호를 받은 시각. 없으면 null.</summary>
    public DateTimeOffset? LastSeen(string sensorId)
    {
        lock (_gate)
            return _lastSeen.TryGetValue(sensorId, out var at) ? at : null;
    }

    /// <summary>등록된 명부 크기.</summary>
    public int ExpectedCount
    {
        get { lock (_gate) return _expected.Count; }
    }

    /// <summary>
    /// 주기 학습 — <b>도착에서만</b> 배운다.
    ///
    /// | 규칙 | 이유 |
    /// |---|---|
    /// | 침묵에서는 절대 배우지 않는다 | 배우면 죽어가는 센서가 <b>자기 죽음을 정상으로</b> 만든다.<br/>도착에서만 배우면 죽은 센서의 주기 추정치는 죽기 직전 값에 멈춘다 |
    /// | 표본 상한 300초 | 한 번의 긴 공백(재연결·백필)이 추정치를 통째로 밀어 올리지 않게 |
    /// | 지수평활 α=0.3 | 빠르게 따라가되 한 번 튄 값에 흔들리지 않게 |
    /// </summary>
    private void LearnCadenceLocked(string sensorId, double gapSec)
    {
        if (gapSec <= 0 || gapSec > MaxSampleSec) return;

        _cadenceSec[sensorId] = _cadenceSec.TryGetValue(sensorId, out var ema) && ema > 0
            ? ema * (1 - Alpha) + gapSec * Alpha
            : gapSec;
    }

    private TimeSpan? CadenceLocked(string sensorId) =>
        _cadenceSec.TryGetValue(sensorId, out var sec) && sec > 0
            ? TimeSpan.FromSeconds(sec)
            : null;
}
