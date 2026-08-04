using System.Globalization;

namespace IoTSensorDashboard.Core.Codecs;

/// <summary>
/// 시간 해석의 단일 규약(I3). 모든 코덱이 이 함수 하나를 쓴다.
///
/// 코덱마다 각자 파싱하면 한쪽만 규약을 어겨도 눈에 띄지 않는다 —
/// 그리고 그 어긋남은 "시간대별 통계가 조금 이상하다" 정도로만 나타나 아주 늦게 발견된다.
/// </summary>
internal static class CodecTime
{
    /// <summary>
    /// ISO-8601 문자열 → 절대 순간.
    ///
    /// AssumeUniversal: offset 이 없는 타임스탬프는 UTC 로 간주한다.
    ///   기본 파싱은 offset 이 없으면 호스트 로컬 시각으로 본다. 그러면 관제실을
    ///   어느 시간대의 PC 에서 돌리느냐에 따라 같은 데이터가 다른 시각으로 저장된다.
    ///
    /// AdjustToUniversal: 파싱 결과를 UTC 로 통일한다.
    ///   "+09:00" 표기와 "Z" 표기가 같은 순간이면 같은 이벤트여야 하고(DedupKey 가 그렇게 비교한다),
    ///   저장 포맷도 하나여야 한다.
    /// </summary>
    public static DateTimeOffset ParseIso(string? text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return DateTimeOffset.Parse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
}
