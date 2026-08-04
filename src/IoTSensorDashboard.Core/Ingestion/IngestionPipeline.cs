using System.Diagnostics;
using System.Threading;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Storage;

namespace IoTSensorDashboard.Core.Ingestion;

/// <summary>
/// 판정의 중심 — 코덱이 만든 이벤트를 검증하고 저장한다.
///
/// 여기의 판정 순서·수치·"버린 것을 세는 방식"이 이 시스템의 신뢰성 전부다.
/// 어떤 채널·어떤 코덱이 꽂혀도 경로는 하나다: raw → decode → ingest.
/// 이 경로를 우회하는 저장이 있으면 I1·I2 가 강제되지 않는다.
/// </summary>
public sealed class IngestionPipeline
{
    /// <summary>
    /// 오버플로 가드. 이 상한이 없으면 악성·오류 값(예: int.MaxValue) 한 건이 집계 합을 오버플로시키고,
    /// 저장이 append-only 라 지울 수도 없어서 영구히 전 집계가 마비된다.
    /// </summary>
    public const int MaxCount = 1_000_000;

    /// <summary>
    /// 한 센서가 한 번의 리딩에서 물리적으로 셀 수 있는 최대 인원(I7).
    /// 한 출입구를 통과하는 인원에는 물리적 한계가 있다(가장 붐벼도 초당 수 명).
    /// 오버플로 가드보다 훨씬 낮은 "말 되는 값"의 경계다.
    /// </summary>
    public const int DefaultMaxPlausibleCountPerReading = 100;

    private readonly IEventStore _store;
    private readonly IPipelineObserver _observer;
    private readonly int _maxPlausible;

    private long _observerFailures;
    private long _storeFailures;

    /// <param name="maxPlausibleCountPerReading">
    /// 실운영에서는 매장·측정 간격별로 보정할 값이지만 이번 범위에서는 100 으로 고정한다.
    /// 주입 가능해야 하는 이유는 검증이 경계값을 바꿔 보기 때문이다.
    /// </param>
    public IngestionPipeline(
        IEventStore store,
        IPipelineObserver? observer = null,
        int maxPlausibleCountPerReading = DefaultMaxPlausibleCountPerReading)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (maxPlausibleCountPerReading <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPlausibleCountPerReading),
                "정합 한계가 0 이하면 모든 이벤트가 격리되어 수집이 통째로 멈춘다.");

        _store = store;
        _observer = observer ?? NullPipelineObserver.Instance;
        _maxPlausible = maxPlausibleCountPerReading;
    }

    /// <summary>관측 콜백이 던진 예외 수. 정상이면 0 이고, 화면의 "관측 실패 0"은 이 값이 근거다.</summary>
    public long ObserverFailures => Interlocked.Read(ref _observerFailures);

    /// <summary>배치 실패 후 건별 재시도까지 실패한 수. 세기만 하지 말고 화면에 드러낼 것.</summary>
    public long StoreFailures => Interlocked.Read(ref _storeFailures);

    /// <summary>이 파이프라인이 쓰는 물리 정합 한계.</summary>
    public int MaxPlausibleCountPerReading => _maxPlausible;

    /// <summary>
    /// 이벤트 한 건 판정.
    ///
    /// 판정 순서는 계약이다. 바꾸지 말 것:
    ///   1. e is null                              → Rejected
    ///   2. SensorId 가 null·공백                   → Rejected
    ///   3. Count &lt; 0 또는 Count &gt; MaxCount   → Rejected
    ///   4. Count &gt; MaxPlausible                 → Implausible  (경계값 자신은 통과)
    ///   5. 그 외                                   → 정규화 후 저장 → Appended / Duplicate
    ///
    /// 3번과 4번의 순서가 중요하다. 오버플로 가드를 먼저 통과시켜야
    /// "망가진 데이터"와 "멀쩡하지만 불가능한 데이터"가 섞이지 않는다.
    /// </summary>
    public IngestResult Ingest(CountEvent? e)
    {
        var start = Stopwatch.GetTimestamp();

        IngestResult result;
        string sensorId;

        if (e is null)
        {
            result = IngestResult.Rejected; sensorId = "";
        }
        else if (string.IsNullOrWhiteSpace(e.SensorId))
        {
            // 센서를 모르는 이벤트는 저장할 곳이 없다.
            result = IngestResult.Rejected; sensorId = e.SensorId ?? "";
        }
        else if (e.Count < 0 || e.Count > MaxCount)
        {
            result = IngestResult.Rejected; sensorId = e.SensorId;
        }
        else if (e.Count > _maxPlausible)
        {
            // 🔒 버리는 게 아니라 격리하고 센다. 조용히 버리면 아무도 모른다.
            result = IngestResult.Implausible; sensorId = e.SensorId;
        }
        else
        {
            var normalized = e with { Direction = NormalizeDirection(e.Direction) };
            result = _store.TryAppend(normalized) ? IngestResult.Appended : IngestResult.Duplicate;
            sensorId = e.SensorId;
        }

        Observe(sensorId, result, ElapsedMicros(start));
        return result;
    }

    /// <summary>
    /// 묶음 판정.
    ///
    /// 🔒 결과는 건별 판정과 한 글자도 달라선 안 되고, 배열 순서는 입력과 같다.
    ///    걸러낸 항목의 자리를 당기지 말 것.
    ///
    /// 저장만 한 번에 위임하고, 경계 검증과 I7 판정은 이벤트마다 그대로 한다.
    /// </summary>
    public IReadOnlyList<IngestResult> IngestBatch(IReadOnlyList<CountEvent?> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return [];

        var start = Stopwatch.GetTimestamp();
        var results = new IngestResult[events.Count];

        // ① 판정만 (저장 제외) — 조건과 순서는 Ingest 와 동일
        var toStore = new List<CountEvent>(events.Count);
        var storeSlots = new List<int>(events.Count);
        var sensorIds = new string[events.Count];

        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e is null)
            {
                results[i] = IngestResult.Rejected; sensorIds[i] = "";
            }
            else if (string.IsNullOrWhiteSpace(e.SensorId))
            {
                results[i] = IngestResult.Rejected; sensorIds[i] = e.SensorId ?? "";
            }
            else if (e.Count < 0 || e.Count > MaxCount)
            {
                results[i] = IngestResult.Rejected; sensorIds[i] = e.SensorId;
            }
            else if (e.Count > _maxPlausible)
            {
                results[i] = IngestResult.Implausible; sensorIds[i] = e.SensorId;
            }
            else
            {
                sensorIds[i] = e.SensorId;
                toStore.Add(e with { Direction = NormalizeDirection(e.Direction) });
                storeSlots.Add(i);
            }
        }

        // ② 통과한 것만 한 번에 저장
        if (toStore.Count > 0)
        {
            var stored = AppendWithRescue(toStore);
            for (int j = 0; j < storeSlots.Count; j++)
                results[storeSlots[j]] = stored[j];
        }

        // ③ 관측 — 배치 소요를 건수로 나눠 이벤트당 지연으로 보고한다.
        //    배치의 각 이벤트는 같은 커밋을 공유하므로 개별 지연이 아니라 전체 소요를 나눠 갖는다.
        //    그렇게 보고할 것 — 지어내지 말 것.
        long total = ElapsedMicros(start);
        long per = events.Count > 0 ? total / events.Count : total;
        for (int i = 0; i < events.Count; i++)
            Observe(sensorIds[i], results[i], per);

        return results;
    }

    /// <summary>
    /// 방향 정규화.
    ///
    /// 🔑 public static 인 이유: 소비 측(대시보드 읽기 모델의 in/out 분기)도 같은 함수를 써야 한다.
    ///    대소문자·공백 차이가 멱등 키와 버킷을 쪼개면 "IN" 과 "in" 이 다른 이벤트가 된다.
    /// </summary>
    public static string? NormalizeDirection(string? d) =>
        string.IsNullOrWhiteSpace(d) ? null : d.Trim().ToLowerInvariant();

    /// <summary>
    /// 배치 저장. 실패하면 건별로 다시 시도해 구제하고, 끝내 못 저장한 것만 센다.
    ///
    /// 왜 구제가 필요한가: 배치는 원자적이라 한 행이 실패하면 배치 전체(최대 512건)가 롤백된다.
    /// 건별 저장이었다면 앞의 511건은 이미 커밋돼 살아남았을 것들이다 —
    /// 즉 배치화가 오류 시 손실 반경을 512배로 키운다.
    ///
    /// 🔴 반환이 bool 이 아니라 IngestResult 인 이유:
    ///    여기서 나올 수 있는 상태는 셋이다 — 저장됨 / 이미 있음 / 저장 실패.
    ///    bool 하나로 표현하면 "저장 실패"가 false 로 접히고, 호출부는 그걸 Duplicate 로 읽는다.
    ///    그러면 화면에 "중복 N건"으로 뜨는데 실제로는 유실이다 —
    ///    이 시스템이 막으려는 바로 그 부류의 조용한 거짓말이 된다.
    ///    (사양서가 이 경우의 결과값을 명시하지 않은 자리다. docs/spec-gaps.md 참조.)
    /// </summary>
    private IngestResult[] AppendWithRescue(List<CountEvent> toStore)
    {
        var results = new IngestResult[toStore.Count];

        try
        {
            var batch = _store.TryAppendBatch(toStore);
            for (int j = 0; j < results.Length; j++)
                results[j] = batch[j] ? IngestResult.Appended : IngestResult.Duplicate;
            return results;
        }
        catch (Exception)
        {
            for (int j = 0; j < toStore.Count; j++)
            {
                try
                {
                    results[j] = _store.TryAppend(toStore[j]) ? IngestResult.Appended : IngestResult.Duplicate;
                }
                catch (Exception)
                {
                    // 저장 자체가 실패한 건. 관측되는 사실은 "저장되지 않음"이므로 Rejected 로 보고하되,
                    // 원인이 판정 실패와 다르므로 StoreFailures 로 따로 센다.
                    // 🔒 이 값이 0 이 아니면 화면에 드러나야 한다 — 세기만 하고 안 이으면 세지 않은 것과 같다.
                    Interlocked.Increment(ref _storeFailures);
                    results[j] = IngestResult.Rejected;
                }
            }
            return results;
        }
    }

    /// <summary>
    /// 관측은 판정에 절대 영향을 주지 않는다 — 결과가 확정된 뒤에만 부르고, 던져도 삼키되 센다.
    /// </summary>
    private void Observe(string sensorId, IngestResult result, long micros)
    {
        try
        {
            _observer.OnIngested(new PipelineEvent(sensorId, result, micros));
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _observerFailures);
        }
    }

    /// <summary>
    /// 경과 마이크로초.
    ///
    /// Math.Max(0, ...) 인 이유: 일부 하드웨어에서 Stopwatch 가 비단조(non-monotonic)일 수 있다.
    /// 음수 delta 가 계측을 왜곡하고 테스트를 flaky 하게 만든다.
    /// </summary>
    private static long ElapsedMicros(long startTimestamp) =>
        Math.Max(0, (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000L / Stopwatch.Frequency);
}
