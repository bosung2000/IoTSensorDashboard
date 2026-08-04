using System.Globalization;

namespace IoTSensorDashboard.Core.Formatting;

/// <summary>
/// 좁은 자리에 큰 수를 넣기 — 「만 / 억」 단위 축약.
///
/// 🔑 <b>왜 필요한가</b>: 누적값은 시간이 갈수록 자릿수가 늘어난다.
///    폭은 그대로인데 숫자만 길어지므로, <b>언젠가 반드시</b> 자리가 모자라는 순간이 온다.
///
/// 🔴 <b>축약은 잘림과 다르다.</b>
///    <c>55,643</c> 이 <c>55,6…</c> 로 잘리면 그건 다른 값이 아니라 <b>읽을 수 없는 값</b>이다.
///    <c>5.6만</c> 은 정밀도를 잃되 <b>뜻은 남는다</b> — 관제 화면에서 그 차이가 크다.
///
/// 📌 그래서 <b>정밀도가 중요한 자리에는 쓰지 않는다.</b>
///    KPI 본문처럼 정확한 값이 근거가 되는 자리는 <see cref="Rendering"/> 의 글자 축소를 쓰고,
///    카드·범례·축 눈금처럼 <b>규모만 알면 되는</b> 자리에 이걸 쓴다.
/// </summary>
public static class CompactNumber
{
    private const long Man = 10_000L;          // 만
    private const long Eok = 100_000_000L;     // 억

    /// <summary>
    /// 축약 문자열. 1만 미만은 <b>축약하지 않는다</b>(축약해도 짧아지지 않는다).
    /// </summary>
    /// <param name="value">원값. 음수도 부호를 유지한 채 축약한다.</param>
    public static string Format(long value)
    {
        // 🔑 부호를 먼저 떼고 크기만 다룬다 — 음수 분기를 아래 전부에 퍼뜨리지 않으려고.
        string sign = value < 0 ? "-" : "";

        // ⚠️ long.MinValue 는 부호를 뒤집을 수 없다(절댓값이 표현 범위를 넘는다).
        //    Math.Abs 가 예외를 던지는 유일한 입력이라 먼저 걸러낸다.
        if (value == long.MinValue) return "-922경";

        long abs = Math.Abs(value);

        if (abs >= Eok) return sign + Scaled(abs, Eok, "억");
        if (abs >= Man) return sign + Scaled(abs, Man, "만");

        return value.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// 단위로 나눈 값.
    ///
    /// 🔑 소수 한 자리는 <b>10 미만일 때만</b> 붙인다.
    ///    <c>1.2만</c> 은 의미가 있지만 <c>12.3만</c> 은 자리만 먹고 판단을 못 바꾼다.
    ///    (12만과 12.3만 중 어느 쪽을 보든 관제 판단은 같다.)
    /// </summary>
    private static string Scaled(long abs, long unit, string suffix)
    {
        double scaled = (double)abs / unit;

        return scaled < 10
            ? scaled.ToString("0.#", CultureInfo.CurrentCulture) + suffix
            : Math.Round(scaled).ToString("N0", CultureInfo.CurrentCulture) + suffix;
    }
}
