using IoTSensorDashboard.Core.Domain;

namespace IoTSensorDashboard.Core.Authorization;

/// <summary>
/// 역할이 어디까지 볼 수 있는가 (I4).
///
/// 🔴 스코프 누수는 <b>한 번 나면 되돌릴 수 없다.</b>
///    타 지점 데이터가 노출된 사실은 지워지지 않는다.
///    그래서 이 층은 <b>모르면 거부(fail-closed)</b>로 만든다.
/// </summary>
public sealed class ScopePolicy
{
    private readonly SiteTree _tree;

    public ScopePolicy(SiteTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _tree = tree;
    }

    /// <summary>
    /// 이 역할·배정으로 볼 수 있는 지점들.
    ///
    /// | 역할 | 범위 |
    /// |---|---|
    /// | TotalAdmin | 전체 |
    /// | HeadOffice | 자기 서브트리(그룹·매장) |
    /// | Group | 자기 서브트리(매장) |
    /// | Store | **자기 노드만** ⚠ |
    /// </summary>
    public IReadOnlySet<string> VisibleSites(Role role, string? assignedSiteId)
    {
        return role switch
        {
            Role.TotalAdmin => _tree.AllIds,

            // 🔴 Store 만 Subtree 를 쓰지 않는 이유 — 권한 상승(escalation) 차단.
            //
            // 📌 매장 역할 계정이 실수로 본부 노드에 배정되면,
            //    Subtree 를 쓸 경우 그 매장 직원이 **본부 산하 전 매장**을 보게 된다.
            //
            //    Store 는 어디에 배정되든 자기 노드 하나만 본다.
            //    오배정이 권한 확대로 이어지지 않는다.
            //
            //    역할 등급과 배정 지점의 정합은 프로비저닝의 책임이지만,
            //    **Core 는 그걸 믿지 않는다.** 믿지 않는 쪽이 안전하다.
            Role.Store => _tree.Exists(assignedSiteId)
                ? new HashSet<string>(StringComparer.Ordinal) { assignedSiteId! }
                : new HashSet<string>(StringComparer.Ordinal),

            _ => _tree.Subtree(assignedSiteId)
        };
    }

    /// <summary>이 역할·배정으로 저 지점을 볼 수 있는가.</summary>
    public bool CanAccess(Role role, string? assignedSiteId, string? targetSiteId) =>
        targetSiteId is not null && VisibleSites(role, assignedSiteId).Contains(targetSiteId);
}
