using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Server;

namespace IoTSensorDashboard.Mqtt;

/// <summary>
/// 관제실 프로세스 안에서 도는 MQTT 브로커.
///
/// 📌 왜 앱에 내장하나: 데모·시연·검증이 "설치 안내서 없이 exe 만 실행하면 되는" 상태여야 한다.
///    외부 브로커(Mosquitto 등)를 요구하는 순간 재현성이 무너진다.
///
/// ⚠️ 이 앱이 브로커를 소유하므로 <b>가장 먼저 떠야</b> 하고,
///    죽으면 나머지 둘은 붙을 곳이 없다(클라이언트가 무한 재시도하므로 돌아오면 자동 복구).
/// </summary>
public sealed class EmbeddedMqttBroker : IAsyncDisposable
{
    private readonly MqttServer _server;
    private readonly X509Certificate2? _cert;
    private bool _disposed;

    /// <param name="tlsCertificate">
    /// null 이면 평문 엔드포인트(테스트 전용). 주면 TLS 전용이 된다.
    /// </param>
    public EmbeddedMqttBroker(
        int port = MqttEndpoint.Port,
        IPAddress? bind = null,
        X509Certificate2? tlsCertificate = null)
    {
        _cert = tlsCertificate;
        var address = bind ?? MqttEndpoint.BindAddress;

        var builder = new MqttServerOptionsBuilder();

        if (tlsCertificate is null)
        {
            builder
                .WithDefaultEndpoint()
                .WithDefaultEndpointBoundIPAddress(address)
                .WithDefaultEndpointPort(port);
        }
        else
        {
            // 🔒 TLS 를 켜면 평문 포트를 같이 열지 않는다.
            //    둘 다 열면 TLS 는 장식이다 — 아무나 평문으로 붙으면 그만이다.
            builder
                .WithoutDefaultEndpoint()
                .WithEncryptedEndpoint()
                .WithEncryptedEndpointBoundIPAddress(address)
                .WithEncryptedEndpointPort(port)
                .WithEncryptionCertificate(tlsCertificate)
                .WithEncryptionSslProtocol(SslProtocols.Tls12 | SslProtocols.Tls13);
        }

        _server = new MqttFactory().CreateMqttServer(builder.Build());
    }

    /// <summary>TLS 전용으로 떠 있는가.</summary>
    public bool IsEncrypted => _cert is not null;

    public bool IsStarted => _server.IsStarted;

    public Task StartAsync() => _server.StartAsync();

    public Task StopAsync() => _server.StopAsync();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_server.IsStarted) await _server.StopAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 종료 중 오류는 삼킨다 — 이미 내려가는 중이라 할 수 있는 일이 없다.
            // 다만 여기서 무언가를 "성공했다"고 보고하지는 않는다.
        }

        _server.Dispose();
        _cert?.Dispose();
    }
}
