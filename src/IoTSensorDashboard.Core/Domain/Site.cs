namespace IoTSensorDashboard.Core.Domain;

/// <summary>
/// 지점 — 총관리자/본점/그룹/매장 계층 트리의 노드. ParentId=null 이면 루트.
///
/// 권한 경계(I4)는 이 트리의 서브트리로 정의된다 — 자기 노드 + 하위 전부.
/// </summary>
public sealed record Site
{
    public required string Id { get; init; }

    public string? ParentId { get; init; }

    public required string Name { get; init; }
}
