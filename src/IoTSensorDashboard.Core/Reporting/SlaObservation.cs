using IoTSensorDashboard.Core.Provisioning;

namespace IoTSensorDashboard.Core.Reporting;

/// <summary>
/// 「이 매장을 실제로 지켜봤는가」 판정.
///
/// 🔒 이 판정이 UI 안에 있으면 안 된다.
///
/// 📌 가동률의 정직함은 <b>전적으로 이 집합에 달려 있다.</b>
///    여기서 매장을 잘못 넣으면 지켜보지도 않은 매장이 다시 「가동률 100%」로 둔갑한다.
///
///    그래서 UI 쪽에 두지 않고 Core 로 꺼내 헤드리스로 못박는다 —
///    대시보드 분모 버그가 <b>UI 프로젝트라 테스트를 못 하던 것</b>과 같은 교훈이다.
///
/// 🧭 일반 규칙: <b>「이 숫자가 틀리면 큰일 나는」 판정은 전부 Core 로 꺼낸다.</b>
///    UI 에 남아 있으면 자동 검증이 불가능하고, 자동 검증이 없으면 조용히 틀린다.
/// </summary>
public static class SlaObservation
{
    /// <summary>
    /// 관측 창에 이벤트를 남긴 센서 ID 들 → 그 센서가 속한 매장 이름 집합.
    ///
    /// 프로비저닝에 없는 센서는 무시한다 —
    /// 어느 매장 것인지 말할 수 없으므로 <b>관측 근거가 못 된다.</b>
    /// </summary>
    public static IReadOnlySet<string> FromActiveSensors(
        IEnumerable<string> activeSensorIds, SiteProvisioning provisioning)
    {
        ArgumentNullException.ThrowIfNull(activeSensorIds);
        ArgumentNullException.ThrowIfNull(provisioning);

        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in activeSensorIds)
        {
            if (id is null) continue;
            if (provisioning.SiteOf(id) is { } siteId) set.Add(provisioning.SiteName(siteId));
        }

        return set;
    }
}
