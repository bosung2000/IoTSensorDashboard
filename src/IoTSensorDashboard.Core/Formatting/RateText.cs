using System.Globalization;

namespace IoTSensorDashboard.Core.Formatting;

/// <summary>
/// 「초당」 값을 사람이 읽을 문자열로.
///
/// 🔴 <b>왜 이게 따로 필요한가 — 실측 결함</b>:
///    매장 12곳에 초당 17건이 흩어지면 매장당 <c>1.4/s</c> 다. 이걸 정수로 반올림해
///    <c>0/s</c> 로 그렸더니, 누적은 계속 오르는데 <b>속도는 전부 0</b> 인 화면이 됐다.
///    보는 사람에게 그건 「데이터가 안 들어온다」로 읽힌다 — <b>정상을 장애로 오독</b>하게 만든 셈이다.
///
/// 🔑 규칙은 하나다: <b>0 이 아닌 값을 0 으로 그리지 않는다.</b>
///    자릿수를 아끼려다 「있음」과 「없음」을 뭉개면, 아낀 폭보다 잃는 게 크다.
/// </summary>
public static class RateText
{
    /// <summary>
    /// 초당 값 표기.
    ///
    /// <list type="bullet">
    ///   <item>10 이상 → 정수 (<c>120</c>) — 이 구간에서 소수점은 자리만 먹는다</item>
    ///   <item>0.1 이상 → 소수 한 자리 (<c>1.4</c>)</item>
    ///   <item>0 초과 0.1 미만 → <c>&lt;0.1</c> — <b>0 이 아니라는 사실</b>이 값 자체보다 중요하다</item>
    ///   <item>정확히 0 → <c>0</c></item>
    /// </list>
    /// </summary>
    public static string Format(double rate)
    {
        // NaN·무한대는 계산이 깨졌다는 뜻이다. 0 으로 위장하면 그 사실이 사라진다.
        if (double.IsNaN(rate) || double.IsInfinity(rate)) return "—";

        if (rate <= 0) return "0";

        if (rate >= 10) return rate.ToString("N0", CultureInfo.CurrentCulture);

        if (rate >= 0.1) return rate.ToString("0.0", CultureInfo.CurrentCulture);

        return "<0.1";
    }
}
