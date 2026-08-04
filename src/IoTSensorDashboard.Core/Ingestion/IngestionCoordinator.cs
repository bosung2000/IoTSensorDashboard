namespace IoTSensorDashboard.Core.Ingestion;

/// <summary>
/// 채널과 파이프라인을 잇는 유일한 경로: raw → decode → ingest.
///
/// 🔑 이 경로를 우회하는 저장이 있어선 안 된다. 우회하면 I1·I2 가 강제되지 않는다.
/// </summary>
public sealed class IngestionCoordinator
{
    private readonly CodecRegistry _registry;
    private readonly IngestionPipeline _pipeline;

    public IngestionCoordinator(CodecRegistry registry, IngestionPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pipeline);

        _registry = registry;
        _pipeline = pipeline;
    }

    /// <summary>채널을 돌린다. 채널은 받은 원본을 Feed 로 흘려보낸다.</summary>
    public Task RunAsync(IIngestionSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.RunAsync(FeedAsync, ct);
    }

    /// <summary>원본 한 건을 해석해 전부 판정한다.</summary>
    public void Feed(RawPayload raw)
    {
        foreach (var e in _registry.Decode(raw))
            _pipeline.Ingest(e);
    }

    private Task FeedAsync(RawPayload raw)
    {
        Feed(raw);
        return Task.CompletedTask;
    }
}
