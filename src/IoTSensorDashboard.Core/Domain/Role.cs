namespace IoTSensorDashboard.Core.Domain;

/// <summary>권한 역할. 데이터 노출 범위를 결정(I4).</summary>
public enum Role
{
    /// <summary>전체.</summary>
    TotalAdmin,

    /// <summary>본점 — 자기 서브트리(그룹·매장).</summary>
    HeadOffice,

    /// <summary>그룹 — 자기 서브트리(매장).</summary>
    Group,

    /// <summary>
    /// 매장 — 자기 노드만. ⚠ 서브트리가 아니다.
    /// 매장 역할 계정이 실수로 상위 노드에 배정돼도 권한이 확대되지 않게 하는 장치.
    /// </summary>
    Store
}
