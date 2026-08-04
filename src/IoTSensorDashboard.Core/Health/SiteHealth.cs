using System.Globalization;

namespace IoTSensorDashboard.Core.Health;

/// <summary>
/// 사이트(매장·본부) 한 곳의 건강 등급.
///
/// 🔑 <see cref="Unknown"/> 과 <see cref="Down"/> 을 <b>합치지 말 것</b>.
///    <see cref="SensorStatus"/> 가 센서 한 대에 대해 지키는 구분을,
///    이 등급이 <b>사이트 단위</b>에서 그대로 이어받는다.
///
/// | 등급     | 뜻                       | 누가 처리하나              |
/// |----------|--------------------------|----------------------------|
/// | Unknown  | 한 번도 신호를 못 봤다    | 설치·배선·프로비저닝       |
/// | Down     | 보다가 전부 끊겼다        | 장애 조치 — 알림·에스컬레이션 |
/// </summary>
public enum SiteHealth
{
    /// <summary>명부에 센서가 없다.</summary>
    None,

    /// <summary>한 번도 못 봤다 — 판정이 아니라 <b>미관측</b>이다.</summary>
    Unknown,

    /// <summary>봤는데 전부 침묵 — 진짜 장애.</summary>
    Down,

    /// <summary>일부만 살아 있다.</summary>
    Partial,

    /// <summary>전부 정상.</summary>
    Ok
}

/// <summary>
/// 등급 판정과 문구 — <b>단일 출처</b>.
///
/// 🔑 <b>이게 Core 에 있는 이유</b>는 <see cref="Rendering.FramePolicy"/> 와 같다.
///    여기 있는 것은 그리는 코드가 아니라 <b>「무엇으로 볼 것인지 정하는 규칙」</b>이다.
///    판정이므로 순수해야 하고, 순수하므로 검증할 수 있다.
///
/// 🔴 화면 네 곳(카드·상태표·토폴로지·게이지)이 이 판정을 공유한다.
///    각자 <c>if</c> 를 쓰면 한 곳만 고치고 나머지를 잊어,
///    <b>같은 매장이 화면마다 다른 색</b>으로 보인다.
/// </summary>
public static class SiteHealthRule
{
    /// <summary>
    /// 등급 판정 — <b>미관측을 장애보다 먼저</b> 본다.
    ///
    /// 🔴 순서가 중요하다. 「전부 침묵」을 먼저 보면 아직 한 번도 못 본 사이트가
    ///    <b>장애로 잡혀</b> 담당자가 헛걸음한다.
    ///
    /// 📌 기동 직후가 정확히 그 상황이다: 센서 1,000대를 17건/s 로 돌리면
    ///    한 바퀴에 약 59초, 침묵 허용치가 자리잡기까지 약 2분이 걸린다.
    ///    그 2분을 「대량 장애」로 그리면 <b>경보가 죽는다</b> —
    ///    남발된 경보 속에서는 진짜 장애가 눈에 띄지 않는다.
    /// </summary>
    /// <param name="total">있어야 할 센서 수(명부 기준 = 분모).</param>
    /// <param name="online">지금 살아 있는 수.</param>
    /// <param name="unknown">한 번도 신호를 못 본 수.</param>
    public static SiteHealth Of(int total, int online, int unknown)
    {
        if (total <= 0) return SiteHealth.None;
        if (unknown >= total) return SiteHealth.Unknown;
        if (online <= 0) return SiteHealth.Down;
        if (online < total) return SiteHealth.Partial;

        return SiteHealth.Ok;
    }

    /// <summary>
    /// 사람이 읽을 상태 문구.
    ///
    /// 🔑 부분 상태에서는 <b>끊김과 미관측을 따로</b> 적는다 — 할 일이 다르므로.
    /// </summary>
    public static string Describe(int total, int online, int unknown)
    {
        var health = Of(total, online, unknown);

        return health switch
        {
            SiteHealth.None => "센서 없음",

            // 🔴 「장애」라고 쓰지 않는다. 아직 못 본 것뿐이고, 원인은 설치·배선·명부 쪽이다.
            SiteHealth.Unknown => "미관측 (아직 신호 없음)",

            SiteHealth.Down => "측정 불가 (전부 무응답)",

            SiteHealth.Partial => unknown > 0
                ? string.Format(CultureInfo.CurrentCulture, "오프라인 {0:N0} · 미관측 {1:N0}",
                    Math.Max(0, total - online - unknown), unknown)
                : string.Format(CultureInfo.CurrentCulture, "일부 오프라인 {0:N0}대", total - online),

            _ => "정상"
        };
    }
}
