using System.Net;

namespace IoTSensorDashboard.Mqtt;

/// <summary>
/// 브로커 접속 지점.
///
/// 🔴 이 값이 세 앱에서 같아야 한다. 하나만 다르면 앱은 각각 잘 뜨는데 데이터가 안 흐른다.
/// </summary>
public static class MqttEndpoint
{
    /// <summary>브로커 포트.</summary>
    public const int Port = 5281;

    /// <summary>
    /// 접속 호스트. 루프백 — 이 컴퓨터 자신.
    ///
    /// 🔒 외부 노출 금지. 브로커가 루프백에만 바인딩되므로
    ///    바깥 네트워크에서는 접속 자체가 불가능하다.
    ///    이번 범위에 연결 인증이 없는데도 안전한 이유가 이것이다 — TLS 가 아니라 이 바인딩이다.
    /// </summary>
    public const string Host = "127.0.0.1";

    public static IPAddress BindAddress => IPAddress.Loopback;

    /// <summary>수집 클라이언트 ID.</summary>
    public const string IngestClientId = "ingest-main";

    /// <summary>헬스 프로브 클라이언트 ID.</summary>
    public const string HealthClientId = "health-probe";

    /// <summary>센서 팜 클라이언트 ID.</summary>
    public const string SensorFarmClientId = "sensor-farm";
}
