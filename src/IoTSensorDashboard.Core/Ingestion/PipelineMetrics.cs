using System.Threading;

namespace IoTSensorDashboard.Core.Ingestion;

/// <summary>
/// 어느 한 순간의 지표. UI 는 이걸 타이머로 샘플링해 초당 처리량을 스스로 계산한다
/// (이벤트마다 UI 를 건드리지 않는다).
///
/// ⚠️ 레이트("초당 N건")를 계산할 때 타이머 간격을 그대로 나누지 말 것.
///    타이머는 부하가 걸리면 밀린다. 반드시 실제 경과 시각으로 나눈다.
///    이걸 틀려서 처리량이 8배 과대 표시된 적이 있다(팜 3,815/s → 화면 30,645/s).
/// </summary>
public readonly record struct MetricsSnapshot(
    long Received, long Appended, long Duplicate, long Rejected, long Implausible,
    long TotalLatencyMicros, long MaxLatencyMicros)
{
    public double AvgLatencyMicros => Received == 0 ? 0 : (double)TotalLatencyMicros / Received;
}

/// <summary>
/// 판정 결과와 처리 지연을 누적하는 관측자.
///
/// 구현 규약:
///   ① 누적은 Interlocked 로만 — 초당 수만 건 경로라 락을 걸면 관측 자체가 병목이 된다
///   ② 최댓값은 CAS 루프로 갱신한다(락 없이)
///   ③ UI 는 Snapshot() 을 타이머로 샘플링한다
/// </summary>
public sealed class PipelineMetrics : IPipelineObserver
{
    private long _received;
    private long _appended;
    private long _duplicate;
    private long _rejected;
    private long _implausible;
    private long _totalLatencyMicros;
    private long _maxLatencyMicros;

    public void OnIngested(in PipelineEvent ev)
    {
        Interlocked.Increment(ref _received);

        switch (ev.Result)
        {
            case IngestResult.Appended: Interlocked.Increment(ref _appended); break;
            case IngestResult.Duplicate: Interlocked.Increment(ref _duplicate); break;
            case IngestResult.Rejected: Interlocked.Increment(ref _rejected); break;
            case IngestResult.Implausible: Interlocked.Increment(ref _implausible); break;
            default: break;
        }

        Interlocked.Add(ref _totalLatencyMicros, ev.ProcessingMicros);

        // 최댓값 갱신 — 락 대신 CAS 루프.
        // 읽은 값이 그사이 바뀌었으면 CompareExchange 가 실패하므로 다시 읽고 재시도한다.
        long observed = ev.ProcessingMicros;
        while (true)
        {
            long current = Interlocked.Read(ref _maxLatencyMicros);
            if (observed <= current) break;
            if (Interlocked.CompareExchange(ref _maxLatencyMicros, observed, current) == current) break;
        }
    }

    /// <summary>
    /// 지금까지의 누적을 한 덩어리로 읽는다.
    ///
    /// ⚠️ 이 스냅샷은 원자적이지 않다 — 읽는 도중에도 카운터가 오른다.
    ///    표시용 지표에는 무해하지만, 이 값들로 등식을 검사하면 안 된다.
    /// </summary>
    public MetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref _received),
        Interlocked.Read(ref _appended),
        Interlocked.Read(ref _duplicate),
        Interlocked.Read(ref _rejected),
        Interlocked.Read(ref _implausible),
        Interlocked.Read(ref _totalLatencyMicros),
        Interlocked.Read(ref _maxLatencyMicros));
}
