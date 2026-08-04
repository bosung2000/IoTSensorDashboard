namespace IoTSensorDashboard.Core.Ingestion;

/// <summary>
/// 토픽 문자열 규약 — 세 앱이 서로를 아는 유일한 통로.
///
/// 🔴 여기 적힌 문자열이 한 글자라도 다르면 앱은 각각 잘 뜨는데
///    <b>데이터가 흐르지 않는다</b> — 오류 메시지도 없이.
///
/// 파싱이 순수 함수라 MQTT 라이브러리 없이 검증할 수 있다. 그래서 Core 에 둔다.
/// </summary>
public static class SensorTopic
{
    /// <summary>구독 필터 — sensors 아래 전부.</summary>
    public const string SensorFilter = "sensors/#";

    /// <summary>관제실 → 팜. 조용한 센서에게 생존 확인.</summary>
    public const string HealthPing = "health/ping";

    /// <summary>팜 → 관제실. 살아 있는 센서만 응답한다.</summary>
    public const string HealthAckPrefix = "health/ack/";

    /// <summary>핑 페이로드가 이 값이면 "전체 센서에게 묻는다".</summary>
    public const string PingAll = "*";

    /// <summary>ACK 본문. 내용에 의미는 없고 "왔다"는 사실만 쓴다.</summary>
    public const string AckBody = "1";

    /// <summary>
    /// 발행 토픽을 만든다.
    ///
    /// <code>sensors / {vendor} / {siteId} / {sensorId}</code>
    /// <code>   [0]       [1]        [2]         [3]     </code>
    ///
    /// 📌 사이트를 토픽에 싣는 이유: 소비 측(대시보드)이 <b>스트림에서 받은 사이트로</b> 집계할 수 있다.
    ///    맥락이 파이프를 통해 흐르므로 소비자가 별도 조회를 하지 않아도 된다.
    /// </summary>
    public static string For(string vendor, string siteId, string sensorId) =>
        $"sensors/{vendor}/{siteId}/{sensorId}";

    public static string AckFor(string sensorId) => HealthAckPrefix + sensorId;

    /// <summary>
    /// 토픽에서 벤더를 뽑는다. 코덱 라우팅 키다.
    ///
    /// 🔒 <c>parts.Length &gt;= 2</c> 로 방어한다.
    ///    옛 3세그먼트 형태(<c>sensors/{vendor}/{sensorId}</c>)에서도 벤더는 같은 자리이므로
    ///    파싱은 관대하게 두되, <b>발행은 반드시 4세그먼트</b>로 한다.
    /// </summary>
    public static string? VendorOf(string? topic) => SegmentAt(topic, 1);

    /// <summary>
    /// 토픽에서 사이트를 뽑는다. 4세그먼트가 아니면 null 이다.
    ///
    /// null 이면 소비 측이 프로비저닝 명부로 폴백한다 —
    /// 건너뛰면 그 센서가 속한 매장이 화면에서 통째로 사라진다.
    /// </summary>
    public static string? SiteOf(string? topic) => SegmentAt(topic, 2);

    /// <summary>토픽에서 센서 ID 를 뽑는다. 4세그먼트가 아니면 null.</summary>
    public static string? SensorIdOf(string? topic) => SegmentAt(topic, 3);

    /// <summary>health/ack/{sensorId} 에서 센서 ID 를 뽑는다.</summary>
    public static string? SensorIdOfAck(string? topic) =>
        topic is not null && topic.StartsWith(HealthAckPrefix, StringComparison.Ordinal)
            ? NullIfBlank(topic[HealthAckPrefix.Length..])
            : null;

    /// <summary>이 토픽이 센서 데이터인가.</summary>
    public static bool IsSensorData(string? topic) =>
        topic is not null && topic.StartsWith("sensors/", StringComparison.Ordinal);

    private static string? SegmentAt(string? topic, int index)
    {
        if (string.IsNullOrEmpty(topic)) return null;

        var parts = topic.Split('/');
        return parts.Length > index ? NullIfBlank(parts[index]) : null;
    }

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
