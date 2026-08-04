using IoTSensorDashboard.Core.Notification;

namespace IoTSensorDashboard.Core.Provisioning;

/// <summary>
/// 통지를 받을 사람.
/// </summary>
/// <param name="IsFallback">
/// 🔑 담당자 정보가 없을 때 쓰는 대체 연락처인가.
///
/// 📌 <b>「연락처가 없어서 안 보냄」이 되면 안 된다.</b> 그건 조용한 실패다.
///    누구에게든 가야 하고, 대신 「대체 연락처로 갔다」는 사실이 남아야 한다.
/// </param>
public sealed record DutyContact(string Role, string Name, string Phone, bool IsFallback);

/// <summary>연락처 명부. 프로비저닝의 일부다.</summary>
public static class DutyRoster
{
    /// <summary>본사 당직 — 최후의 수신자. 항상 존재한다.</summary>
    public static readonly DutyContact HeadquartersDuty =
        new("HeadquartersDuty", "본사 당직", "02-0000-0000", IsFallback: false);

    /// <summary>
    /// 아무 담당자도 못 찾았을 때.
    ///
    /// 🔒 null 을 돌려주지 않는다 — 호출부가 「없으니 안 보냄」으로 처리하게 두지 않기 위해서다.
    /// </summary>
    public static readonly DutyContact Fallback =
        new("Fallback", "미지정 담당(대체)", "02-0000-0000", IsFallback: true);

    /// <summary>
    /// 역할과 매장으로 연락처를 찾는다.
    ///
    /// 이번 범위에서는 이름을 규칙으로 만든다(사람 명부 관리는 범위 밖).
    /// 핵심은 <b>어떤 경우에도 null 이 아니라는 것</b>이다.
    /// </summary>
    public static DutyContact For(EscalationRole role, string? storeName, string? groupName)
    {
        return role switch
        {
            EscalationRole.StoreManager => string.IsNullOrWhiteSpace(storeName)
                ? Fallback
                : new DutyContact("StoreManager", $"{storeName} 점장", "010-0000-0000", false),

            EscalationRole.GroupManager => string.IsNullOrWhiteSpace(groupName)
                ? Fallback
                : new DutyContact("GroupManager", $"{groupName} 관리자", "010-0000-0001", false),

            EscalationRole.HeadquartersDuty => HeadquartersDuty,

            _ => Fallback
        };
    }
}
