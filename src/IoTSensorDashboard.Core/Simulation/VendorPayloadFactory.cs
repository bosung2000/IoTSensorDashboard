using System.Globalization;

namespace IoTSensorDashboard.Core.Simulation;

/// <summary>
/// 발행 측 페이로드의 단일 소스 — 코덱이 기대하는 형태를 여기 한 곳에서 만든다.
///
/// 왜 한 곳인가: 발행 코드와 파싱 코드가 각각 포맷을 알고 있으면 한쪽만 바뀌었을 때 조용히 어긋난다.
/// 발행은 되는데 파싱이 빈 리스트를 돌려주고, 아무 오류도 안 난다.
///
/// 생성이 문자열 보간인 이유: 고빈도 경로라 직렬화기를 태우지 않는다(파싱은 System.Text.Json).
/// </summary>
public static class VendorPayloadFactory
{
    public static readonly IReadOnlyList<string> KnownVendors = ["flir", "milesight"];

    /// <summary>
    /// 벤더 형식의 JSON 한 건.
    ///
    /// 🔒 미지원 벤더는 예외를 던진다. 여기서만은 관대하지 않다 —
    ///    발행 측은 우리가 만든 코드이므로 오타는 즉시 터져야 한다.
    ///    (수신 측 CodecRegistry 는 반대로 빈 리스트를 돌려준다. 밖에서 오는 것은 불신하되 죽지는 않는다.)
    /// </summary>
    public static string Build(string vendor, string sensorId, DateTimeOffset ts, int inCount, int outCount)
    {
        // ISO-8601 round-trip — I3 의 저장·전송 포맷.
        var iso = ts.ToString("o", CultureInfo.InvariantCulture);

        return vendor switch
        {
            "flir" => $"{{\"sensorId\":\"{sensorId}\",\"timestamp\":\"{iso}\"," +
                      $"\"lines\":[{{\"direction\":\"in\",\"count\":{inCount}}},{{\"direction\":\"out\",\"count\":{outCount}}}]}}",

            "milesight" => $"{{\"deviceId\":\"{sensorId}\",\"time\":\"{iso}\"," +
                           $"\"periodIn\":{inCount},\"periodOut\":{outCount}}}",

            _ => throw new ArgumentException($"알 수 없는 벤더: {vendor}", nameof(vendor))
        };
    }
}
