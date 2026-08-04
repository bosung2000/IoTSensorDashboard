using System.Text;
using IoTSensorDashboard.Core.Ingestion;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace IoTSensorDashboard.Mqtt;

/// <summary>
/// 관제실 쪽 생존 확인 — <b>조용한 센서에게만</b> 묻는다.
///
/// 📌 왜 전체에게 매번 묻지 않나: 1,000대에게 2.5초마다 물으면
///    그 트래픽이 실제 데이터 트래픽을 압도한다.
///
///    데이터가 들어오는 센서는 <b>그 데이터 자체가 생존 증거</b>다. 모르는 것만 묻는다.
///    그래서 부하가 클 때 핑 트래픽은 자연히 0 에 수렴한다.
/// </summary>
public sealed class MqttHealthProbe : IAsyncDisposable
{
    /// <summary>핑 주기.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2.5);

    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private long _pingsSent;
    private long _acksReceived;
    private bool _disposed;

    public MqttHealthProbe(
        string clientId = MqttEndpoint.HealthClientId,
        string host = MqttEndpoint.Host,
        int port = MqttEndpoint.Port,
        bool useTls = true)
    {
        _client = new MqttFactory().CreateMqttClient();
        _options = MqttClientOptionsFactory.Create(clientId, host, port, useTls);
    }

    public bool IsConnected => _client.IsConnected;

    public long PingsSent => Interlocked.Read(ref _pingsSent);

    public long AcksReceived => Interlocked.Read(ref _acksReceived);

    /// <summary>살아 있다고 응답한 센서 ID.</summary>
    public event Action<string>? AckReceived;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _client.ConnectedAsync += async _ =>
        {
            try
            {
                await _client.SubscribeAsync(SensorTopic.HealthAckPrefix + "#",
                                             MqttQualityOfServiceLevel.AtMostOnce, ct)
                             .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 다음 재연결에서 재시도.
            }
        };

        _client.ApplicationMessageReceivedAsync += e =>
        {
            var sensorId = SensorTopic.SensorIdOfAck(e.ApplicationMessage.Topic);
            if (sensorId is not null)
            {
                Interlocked.Increment(ref _acksReceived);

                try
                {
                    AckReceived?.Invoke(sensorId);
                }
                catch (Exception)
                {
                    // 구독자 쪽 오류가 수신 루프를 멈추게 두지 않는다.
                }
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
            await RetryUntilConnectedAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 지정한 센서들에게 생존을 묻는다.
    /// </summary>
    /// <param name="sensorIds">
    /// 비어 있으면 <b>전체</b>에게 묻는다("*").
    ///
    /// ⚠️ "물어볼 대상이 없어서 빈 목록"인 경우와 구분해야 한다.
    ///    호출 측(헬스 추적기)이 대상을 계산하며, 대상이 0 이면 아예 부르지 않는 것이 맞다.
    /// </param>
    public async Task PingAsync(IReadOnlyList<string>? sensorIds = null, CancellationToken ct = default)
    {
        var body = sensorIds is null || sensorIds.Count == 0
            ? SensorTopic.PingAll
            : string.Join('\n', sensorIds);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(SensorTopic.HealthPing)
            .WithPayload(Encoding.UTF8.GetBytes(body))
            // QoS 0 — 놓쳐도 다음 주기에 다시 묻는다. 확인 응답 비용이 아깝다.
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .WithRetainFlag(false)
            .Build();

        await _client.PublishAsync(message, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _pingsSent);
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
