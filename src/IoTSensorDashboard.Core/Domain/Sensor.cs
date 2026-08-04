namespace IoTSensorDashboard.Core.Domain;

/// <summary>센서 — 어느 지점에 속하는지. 이벤트 스코핑(I4)의 연결고리.</summary>
public sealed record Sensor
{
    public required string Id { get; init; }

    public required string SiteId { get; init; }

    public string? Vendor { get; init; }
}
