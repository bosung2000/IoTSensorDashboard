using System.Security.Authentication;
using MQTTnet.Client;

namespace IoTSensorDashboard.Mqtt;

/// <summary>
/// 클라이언트 접속 옵션을 <b>한 곳에서</b> 만든다.
///
/// 세 앱이 각자 옵션을 조립하면 한 곳만 바뀌었을 때 조용히 어긋난다 —
/// 예를 들어 한 앱만 keepalive 를 기본값으로 두면 그 앱만 절전 복귀 후 안 붙는다.
/// </summary>
public static class MqttClientOptionsFactory
{
    /// <param name="clientId">
    /// 🔒 고정 ID 를 준다. 랜덤 금지.
    ///
    /// 📌 근거: WithCleanSession(false) 와 짝이다.
    ///    브로커는 단절 중 QoS1 메시지를 <b>그 클라이언트 앞으로</b> 큐잉했다가 재연결 시 재전달한다.
    ///    ID 가 랜덤이면 재연결할 때마다 다른 클라이언트가 되어 그 큐가 통째로 버려진다.
    /// </param>
    public static MqttClientOptions Create(
        string clientId,
        string host = MqttEndpoint.Host,
        int port = MqttEndpoint.Port,
        bool useTls = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId(clientId)
            .WithCleanSession(false)
            .WithKeepAlivePeriod(MqttReconnect.RecommendedKeepAlive);

        if (useTls)
        {
            builder.WithTlsOptions(o => o
                .UseTls()
                .WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13)

                // ⚠️ 인증서 검증을 통과시킨다. 이건 "검증을 끈 것"이므로 정직하게 적어 둔다.
                //
                //    브로커 인증서가 자체서명이라 신뢰 사슬을 검증할 방법이 없다.
                //    이게 안전한 이유는 인증서가 아니라 <b>루프백 바인딩</b>이다 —
                //    통신이 이 컴퓨터 밖으로 나가지 않으므로 중간자가 낄 자리가 없다.
                //
                // 🔴 브로커를 다른 호스트로 분리하는 순간 이 줄은 진짜 취약점이 된다.
                //    그때는 인증서를 신뢰 저장소에 등록하거나 지문(thumbprint)을 고정 검증해야 한다.
                .WithCertificateValidationHandler(_ => true));
        }

        return builder.Build();
    }
}
