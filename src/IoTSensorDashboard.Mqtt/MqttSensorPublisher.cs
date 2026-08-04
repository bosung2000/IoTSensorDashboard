using System.Text;
using IoTSensorDashboard.Core.Ingestion;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace IoTSensorDashboard.Mqtt;

/// <summary>
/// 센서 팜의 발행 클라이언트.
///
/// 🔑 센서 1,000대가 <b>이 연결 하나</b>를 공유한다.
///    센서마다 연결을 열면 1,000개의 TCP 연결과 1,000번의 TLS 핸드셰이크가 생긴다.
///    실제 현장도 게이트웨이 하나가 여러 센서를 대신 발행하는 구조라 이쪽이 현실적이다.
/// </summary>
public sealed class MqttSensorPublisher : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private long _published;
    private bool _disposed;

    public MqttSensorPublisher(
        string clientId = MqttEndpoint.SensorFarmClientId,
        string host = MqttEndpoint.Host,
        int port = MqttEndpoint.Port,
        bool useTls = true)
    {
        _client = new MqttFactory().CreateMqttClient();
        _options = MqttClientOptionsFactory.Create(clientId, host, port, useTls);
    }

    public bool IsConnected => _client.IsConnected;

    /// <summary>
    /// 이번 세션에 발행한 메시지 수.
    ///
    /// ⚠️ <b>세션</b> 값이다. 관제실의 "누적 수신"은 전체 기간이라 나란히 놓으면 크게 달라 보인다.
    ///    실제로 24배 차이가 나서 "데이터가 유실됐다"로 읽힌 적이 있다.
    ///    → 화면 라벨에 반드시 기간을 명시할 것.
    /// </summary>
    public long Published => Interlocked.Read(ref _published);

    /// <summary>
    /// 핑을 받았을 때 부를 것. 본문은 "*"(전체) 또는 줄바꿈으로 구분된 센서 ID 목록.
    ///
    /// 🔒 이 핸들러는 fire-and-forget 으로 호출된다 — 수신 루프를 막지 않기 위해서다.
    ///    핑 한 번에 최대 1,000개의 ACK 를 발행할 수 있는데,
    ///    그걸 수신 핸들러 안에서 await 하면 그동안 MQTT 수신이 통째로 멈춘다.
    ///
    ///    대신 <b>이 핸들러 안에서 예외를 반드시 흡수</b>해야 한다.
    ///    관측되지 않는 Task 의 예외는 프로세스를 죽일 수 있다.
    /// </summary>
    public event Func<string, Task>? PingReceived;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _client.ConnectedAsync += async _ =>
        {
            try
            {
                // 핑은 QoS 0 — 놓쳐도 다음 주기에 다시 묻는다.
                await _client.SubscribeAsync(SensorTopic.HealthPing, MqttQualityOfServiceLevel.AtMostOnce, ct)
                             .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 다음 재연결에서 재시도.
            }
        };

        _client.ApplicationMessageReceivedAsync += e =>
        {
            if (e.ApplicationMessage.Topic == SensorTopic.HealthPing)
            {
                var body = e.ApplicationMessage.ConvertPayloadToString() ?? SensorTopic.PingAll;
                _ = InvokePingAsync(body);   // 🔑 기다리지 않는다
            }
            return Task.CompletedTask;
        };

        _client.EnableAutoReconnect(_options, ct);

        try
        {
            await _client.ConnectAsync(_options, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 관제실이 아직 안 떴을 수 있다. 붙을 때까지 재시도한다.
            await RetryUntilConnectedAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 센서 데이터 한 건 발행.
    ///
    /// QoS 1 · retain 없음.
    ///
    /// 🚫 retain 을 쓰지 않는 이유: retain 은 마지막 값을 브로커가 붙잡고 있다가
    ///    새 구독자에게 준다. 그러면 <b>죽은 센서의 마지막 값이 방금 온 값처럼</b> 보인다.
    ///    "모르는 것을 아는 것처럼 그리지 마라"에 정면으로 어긋난다.
    /// </summary>
    public async Task PublishAsync(string vendor, string siteId, string sensorId, string payload,
                                   CancellationToken ct = default)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(SensorTopic.For(vendor, siteId, sensorId))
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        await _client.PublishAsync(message, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _published);
    }

    /// <summary>
    /// 생존 응답.
    ///
    /// 🔒 <b>살아 있는 센서만</b> 부를 것. 죽은 센서가 ACK 를 보내면
    ///    관제실이 그 센서를 영원히 못 찾는다.
    /// </summary>
    public async Task PublishAckAsync(string sensorId, CancellationToken ct = default)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(SensorTopic.AckFor(sensorId))
            .WithPayload(Encoding.UTF8.GetBytes(SensorTopic.AckBody))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .WithRetainFlag(false)
            .Build();

        await _client.PublishAsync(message, ct).ConfigureAwait(false);
    }

    private async Task InvokePingAsync(string body)
    {
        try
        {
            var handler = PingReceived;
            if (handler is not null) await handler(body).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 🔒 관측되지 않는 Task 의 예외는 프로세스를 죽일 수 있다. 반드시 여기서 흡수한다.
        }
    }

    private async Task RetryUntilConnectedAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_client.IsConnected)
        {
            try
            {
                await Task.Delay(MqttReconnect.DefaultBackoff, ct).ConfigureAwait(false);
                await _client.ConnectAsync(_options, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // 계속 기다린다.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_client.IsConnected) await _client.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 종료 중.
        }

        _client.Dispose();
    }
}
