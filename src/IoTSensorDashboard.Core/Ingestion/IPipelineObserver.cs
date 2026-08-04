namespace IoTSensorDashboard.Core.Ingestion;

/// <summary>파이프라인이 이벤트 하나를 처리하고 남긴 자취. 판정에 영향을 주지 않는 곁다리.</summary>
public readonly record struct PipelineEvent(string SensorId, IngestResult Result, long ProcessingMicros);

/// <summary>
/// 파이프라인 관측 플러그인.
///
/// 절대 규칙:
///   ① 결과가 확정된 뒤에만 부른다
///   ② 관측자가 예외를 던져도 판정·저장·후속 이벤트에 영향이 없다
///   ③ 삼키되 ObserverFailures 카운터를 올린다 — 조용히 사라지지 않게
/// </summary>
public interface IPipelineObserver
{
    void OnIngested(in PipelineEvent ev);
}

/// <summary>관측이 필요 없을 때 쓰는 무동작 구현. null 검사를 코드 전체에 흩뿌리지 않기 위한 것.</summary>
public sealed class NullPipelineObserver : IPipelineObserver
{
    public static readonly NullPipelineObserver Instance = new();

    private NullPipelineObserver() { }

    public void OnIngested(in PipelineEvent ev) { }
}
