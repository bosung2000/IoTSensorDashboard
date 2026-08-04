namespace IoTSensorDashboard.Core.Rendering;

/// <summary>
/// 「지금 얼마나 자주 다시 그릴 것인가」.
///
/// 🔑 이게 Core 에 있는 게 이상해 보일 수 있다.
///    여기 있는 것은 <b>그리는 코드가 아니라 「언제 그릴지 판정하는 규칙」</b>이다.
///    판정이므로 순수해야 하고, 순수하므로 검증할 수 있다.
///
/// 📌 근거 — 실측:
///    부하가 <b>15 msg/s 뿐인데 세 앱이 CPU 314%</b> 를 쓰고 있었다.
///    원인은 데이터가 아니라 <b>그리기</b>였다 —
///    세 앱 모두 33ms 마다 화면 전체를 다시 그렸고, 그건 <b>부하와 무관하게 항상</b> 일어났다.
///
/// 🔑 이건 성능 문제이자 <b>주장의 문제</b>이기도 하다.
///    <b>「이 시스템은 효율적이다」라고 말하려면 부하가 없을 때 CPU 도 내려가야 한다.</b>
///    그때는 그 반대였다.
/// </summary>
public static class FramePolicy
{
    /// <summary>보고 있고 값도 움직임 — 부드러움이 값을 하는 유일한 구간.</summary>
    public static readonly TimeSpan Active = TimeSpan.FromMilliseconds(33);      // 30fps

    /// <summary>보고는 있지만 값이 조용함 — 시계·초당 값 갱신에는 충분하다.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(125);       // 8fps

    /// <summary>창이 뒤에 있음 — 아무도 안 보는 화면에 CPU 를 쓰지 않는다.</summary>
    public static readonly TimeSpan Background = TimeSpan.FromMilliseconds(500); // 2fps

    /// <summary>
    /// 애니메이션 끔.
    ///
    /// ⚠️ <b>완전히 세우지는 않는다.</b>
    ///
    /// 📌 세우면 <b>창 크기 변경·데이터 갱신 후 다시 그릴 사람이 없어</b>
    ///    화면이 낡은 채로 남는다. 아주 낮은 빈도로 유지만 한다.
    /// </summary>
    public static readonly TimeSpan AnimationsOff = TimeSpan.FromMilliseconds(1000); // 1fps

    /// <summary>
    /// 지금 상황에 맞는 갱신 간격.
    /// </summary>
    /// <param name="windowActive">이 창이 지금 앞에 있는가.</param>
    /// <param name="busy">값이 실제로 움직이고 있는가.</param>
    /// <param name="animationsOn">사용자가 애니메이션을 켜 두었는가.</param>
    public static TimeSpan IntervalFor(bool windowActive, bool busy, bool animationsOn)
    {
        if (!animationsOn) return AnimationsOff;
        if (!windowActive) return Background;
        return busy ? Active : Idle;
    }
}
