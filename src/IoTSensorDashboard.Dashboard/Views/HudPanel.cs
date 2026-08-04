using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Dashboard.Model;
using IoTSensorDashboard.Ui.Common.Rendering;

namespace IoTSensorDashboard.Dashboard.Views;

/// <summary>
/// 대시보드 패널 — 공통 뼈대(<see cref="RenderPanel"/>)에 <b>스냅샷 보관</b>만 얹는다.
///
/// 🔑 스냅샷을 패널이 들고 있는 이유: 다시 그리기는 <b>우리가 부르는 게 아니라</b>
///    WPF 가 자기 차례에 부른다(창 크기 변경·가림 해제 등). 그때도 마지막 값을
///    그릴 수 있어야 하므로 패널이 자기 데이터를 쥐고 있어야 한다.
/// </summary>
public abstract class HudPanel : RenderPanel
{
    /// <summary>지금 그리고 있는 스냅샷.</summary>
    protected DashboardSnapshot Snapshot { get; private set; } =
        DashboardSnapshot.Empty(Role.TotalAdmin, "전체");

    /// <summary>새 스냅샷으로 다시 그린다.</summary>
    public void Update(DashboardSnapshot snapshot)
    {
        Snapshot = snapshot ?? DashboardSnapshot.Empty(Role.TotalAdmin, "전체");
        Redraw();
    }
}
