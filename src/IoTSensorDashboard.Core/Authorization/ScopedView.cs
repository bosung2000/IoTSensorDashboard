using IoTSensorDashboard.Core.Domain;

namespace IoTSensorDashboard.Core.Authorization;

/// <summary>
/// I4 가 실제로 강제되는 지점 — 이벤트를 권한 범위로 거른다.
/// </summary>
public sealed class ScopedView
{
    private readonly ScopePolicy _policy;
    private readonly Dictionary<string, string> _sensorSite;

    public ScopedView(ScopePolicy policy, IEnumerable<Sensor> sensors)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sensors);

        _policy = policy;
        _sensorSite = sensors
            .Where(s => s is not null && !string.IsNullOrEmpty(s.Id))
            .ToDictionary(s => s.Id, s => s.SiteId, StringComparer.Ordinal);
    }

    /// <summary>이 역할·배정으로 볼 수 있는 센서들.</summary>
    public IReadOnlySet<string> VisibleSensors(Role role, string? assignedSiteId)
    {
        var sites = _policy.VisibleSites(role, assignedSiteId);

        return _sensorSite
            .Where(kv => sites.Contains(kv.Value))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// 권한 범위 밖 이벤트를 걸러낸다.
    ///
    /// 🔒 <b>매핑 없는 센서(미등록)는 제외</b>한다 — 경계 밖으로 취급한다.
    ///    「모르니까 일단 보여주자」가 아니라 <b>「모르니까 안 보여준다」</b>이다.
    ///
    /// ⚠️ 이 규칙이 I5(「사라지게 하지 마라」)와 충돌하는 것처럼 보일 수 있다.
    ///
    ///    해소 방법: 소비 측이 <b>프로비저닝 명부로 폴백 배정</b>을 한다.
    ///    스트림에서 받은 사이트를 우선하되, 없으면 명부에서 찾는다.
    ///    같은 단일 진실원에서 나오므로 「발명」이 아니다.
    ///
    ///    즉 여기서 거르는 것은 <b>정말로 어디에도 없는</b> 센서뿐이다.
    /// </summary>
    public IReadOnlyList<CountEvent> Filter(
        IEnumerable<CountEvent> events, Role role, string? assignedSiteId)
    {
        ArgumentNullException.ThrowIfNull(events);

        var visible = VisibleSensors(role, assignedSiteId);
        return events.Where(e => e is not null && visible.Contains(e.SensorId)).ToList();
    }

    /// <summary>이 센서가 속한 지점. 매핑이 없으면 null.</summary>
    public string? SiteOf(string sensorId) =>
        sensorId is not null && _sensorSite.TryGetValue(sensorId, out var siteId) ? siteId : null;
}
