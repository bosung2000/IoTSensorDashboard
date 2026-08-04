using IoTSensorDashboard.Core.Domain;

namespace IoTSensorDashboard.Core.Authorization;

/// <summary>
/// 조직 트리 — 본사 → 본부 → 매장.
///
/// 권한 경계(I4)는 이 트리의 <b>서브트리</b>로 정의된다 — 자기 노드 + 하위 전부.
/// </summary>
public sealed class SiteTree
{
    private readonly Dictionary<string, Site> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _childrenOf = new(StringComparer.Ordinal);

    public SiteTree(IEnumerable<Site> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);

        foreach (var site in sites)
        {
            if (site is null || string.IsNullOrEmpty(site.Id)) continue;

            _byId[site.Id] = site;
            if (!_childrenOf.ContainsKey(site.Id)) _childrenOf[site.Id] = [];
        }

        foreach (var site in _byId.Values)
        {
            if (site.ParentId is null) continue;
            if (!_childrenOf.TryGetValue(site.ParentId, out var children)) continue;

            children.Add(site.Id);
        }

        AllIds = _byId.Keys.ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlySet<string> AllIds { get; }

    public bool Exists(string? siteId) =>
        !string.IsNullOrEmpty(siteId) && _byId.ContainsKey(siteId);

    /// <summary>
    /// rootId 노드 + 그 아래 모든 후손 지점 ID.
    ///
    /// 세 가지를 방어한다.
    ///
    /// | 방어 | 왜 |
    /// |---|---|
    /// | null·빈 문자열 → **빈 집합** | 「모르면 안 보여준다」 쪽으로 붙는다 |
    /// | 미지 지점 → **빈 집합** | 존재하지 않는 지점에 배정된 계정이 **전체를 보게** 되면 안 된다 |
    /// | 순환 방어 | 데이터가 잘못돼 A → B → A 가 되면 **무한 루프**가 된다 |
    /// </summary>
    public IReadOnlySet<string> Subtree(string? rootId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        // 🔒 fail-closed — 모르는 것은 거부한다.
        if (string.IsNullOrEmpty(rootId) || !_byId.ContainsKey(rootId)) return result;

        var stack = new Stack<string>();
        stack.Push(rootId);

        while (stack.Count > 0)
        {
            var id = stack.Pop();

            // 이미 방문했으면 건너뛴다 — 순환 데이터에서 무한 루프를 막는다.
            if (!result.Add(id)) continue;

            if (_childrenOf.TryGetValue(id, out var children))
                foreach (var child in children) stack.Push(child);
        }

        return result;
    }

    /// <summary>지점 이름. 모르는 지점이면 null.</summary>
    public string? NameOf(string? siteId) =>
        siteId is not null && _byId.TryGetValue(siteId, out var site) ? site.Name : null;
}
