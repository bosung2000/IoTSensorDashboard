using System.Globalization;
using IoTSensorDashboard.Core.Formatting;
using Xunit;

namespace IoTSensorDashboard.Tests.Formatting;

/// <summary>
/// 화면 숫자 표기 규칙.
///
/// 🔴 이 테스트가 지키는 것은 「예쁘게 보이기」가 아니라 <b>오독 방지</b>다.
///    실제로 <c>1.4/s</c> 를 <c>0/s</c> 로 그려 <b>정상을 장애로 읽히게</b> 한 결함이 있었다.
///    매장 12곳에 초당 17건이 흩어지면 매장당 1.4/s 인데, 정수 반올림이 그걸 전부 0 으로 만들었다.
/// </summary>
public sealed class RateTextTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(-3, "0")]           // 음수 레이트는 있을 수 없다 — 0 으로 접는다
    [InlineData(0.5, "0.5")]
    [InlineData(1.44, "1.4")]
    [InlineData(17, "17")]
    [InlineData(1234, "1,234")]
    public void 구간별_표기(double rate, string expected) =>
        Assert.Equal(expected, InKorean(() => RateText.Format(rate)));

    /// <summary>
    /// 🔑 핵심 불변식: <b>0 이 아닌 값은 절대 "0" 으로 그리지 않는다.</b>
    ///    「있음」과 「없음」이 같은 글자가 되는 순간 그 화면은 거짓말을 시작한다.
    /// </summary>
    [Theory]
    [InlineData(0.0001)]
    [InlineData(0.04)]
    [InlineData(0.099)]
    public void 아주_작은_값도_0_으로_그리지_않는다(double rate)
    {
        var text = RateText.Format(rate);

        Assert.NotEqual("0", text);
        Assert.Equal("<0.1", text);
    }

    /// <summary>계산이 깨진 것을 0 으로 위장하면 그 사실이 사라진다.</summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 계산_불능은_숫자로_위장하지_않는다(double rate) =>
        Assert.Equal("—", RateText.Format(rate));

    /// <summary>
    /// 표기 규칙은 문화권에 따라 자릿점이 달라지므로, 판정을 한 문화권에 고정한다.
    /// (그러지 않으면 이 테스트가 <b>실행하는 PC 의 지역 설정</b>에 따라 통과·실패한다.)
    /// </summary>
    private static string InKorean(Func<string> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ko-KR");

        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

/// <summary>
/// 좁은 자리의 큰 수 — 만/억 축약.
///
/// 🔑 축약은 <b>잘림과 다르다</b>. 55,643 → "55,6…" 은 읽을 수 없는 값이지만
///    "5.6만" 은 정밀도를 잃되 뜻은 남는다.
/// </summary>
public sealed class CompactNumberTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(9_999L, "9,999")]        // 1만 미만은 축약해도 짧아지지 않는다
    [InlineData(12_000L, "1.2만")]
    [InlineData(123_456L, "12만")]       // 10 이상이면 소수점이 판단을 못 바꾼다
    [InlineData(-12_000L, "-1.2만")]
    [InlineData(150_000_000L, "1.5억")]
    public void 만_억_단위로_접는다(long value, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ko-KR");

        try
        {
            Assert.Equal(expected, CompactNumber.Format(value));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// <see cref="long.MinValue"/> 는 절댓값이 표현 범위를 넘어 <see cref="Math.Abs(long)"/> 가 던진다.
    /// <b>표시 코드가 예외로 화면을 죽이면 안 된다.</b>
    /// </summary>
    [Fact]
    public void 최솟값에서_던지지_않는다() =>
        Assert.False(string.IsNullOrEmpty(CompactNumber.Format(long.MinValue)));
}
