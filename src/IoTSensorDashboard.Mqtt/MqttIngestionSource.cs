using IoTSensorDashboard.Core.Ingestion;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace IoTSensorDashboard.Mqtt;

/// <summary>
/// MQTT 수집 채널 — 브로커를 구독해 원본을 흘려보낸다. "수신만" 한다.
///
/// 판정·저장은 sink 뒤쪽(파이프라인)의 몫이다.
/// 채널이 판정에 손을 대면 새 채널을 붙일 때마다 불변식이 깨질 기회가 생긴다.
/// </summary>
public sealed class MqttIngestionSource : IIngestionSource, IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private readonly string _topicFilter;
    private long _received;
    private bool _disposed;

    public MqttIngestionSource(
        string clientId = MqttEndpoint.IngestClientId,
        string host = MqttEndpoint.Host,
        int port = MqttEndpoint.Port,
        bool useTls = true,
        string topicFilter = SensorTopic.SensorFilter)
    {
        _client = new MqttFactory().CreateMqttClient();
        _options = MqttClientOptionsFactory.Create(clientId, host, port, useTls);
        _topicFilter = topicFilter;
    }

    public string Name => "mqtt";

    public bool IsConnected => _client.IsConnected;

    /// <summary>이 채널이 받은 메시지 수. 유입 레이트의 원재료.</summary>
    public long ReceivedMessages => Interlocked.Read(ref _received);

    /// <summary>
    /// 취소될 때까지 구독을 유지한다.
    /// </summary>
    /// <param name="sink">
    /// 🔒 <b>즉시 반환해야 한다.</b> 여기서 오래 걸리면 브로커에 확인 응답이 늦어지고,
    ///    브로커가 QoS1 재전송을 시작해 그 재전송이 다시 부하를 만든다(잼 고착).
    ///
    ///    호출 측은 "큐에 넣고 바로 반환"하는 형태여야 한다.
    /// </param>
    public async Task RunAsync(Func<RawPayload, Task> sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);

        // 매 연결마다 재구독한다. 지속 세션이라도 안전한 쪽으로 붙는 선택이다.
        _client.ConnectedAsync += async _ =>
        {
            try
            {
                await _client.SubscribeAsync(_topicFilter, MqttQualityOfServiceLevel.AtLeastOnce, ct)
                             .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 다음 재연결에서 다시 시도한다.
            }
        };

        _client.ApplicationMessageReceivedAsync += async e =>
        {
            var topic = e.ApplicationMessage.Topic;
            if (!SensorTopic.IsSensorData(topic)) return;

            var vendor = SensorTopic.VendorOf(topic);
            if (vendor is null) return;   // 코덱을 고를 수 없는 메시지는 흘려보낼 곳이 없다

            Interlocked.Increment(ref _received);

            var raw = new RawPayload
            {
                Vendor = vendor,
                Body = e.ApplicationMessage.ConvertPayloadToString() ?? "",
                Source = $"mqtt:{topic}",

                // 🔑 도착 시각을 <b>지금</b> 찍는다.
                //    뒤쪽 큐에서 꺼낼 때 찍으면 대기한 시간만큼 헬스가 거짓으로 젊어지고,
                //    이미 죽은 센서가 살아 있는 것으로 보인다 — 하필 부하가 큰 구간에서.
                ReceivedAt = DateTimeOffset.UtcNow
            };

            await sink(raw).ConfigureAwait(false);
        };

        _client.EnableAutoReconnect(_options, ct);

        try
        {
            await _client.ConnectAsync(_options, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 첫 연결이 실패해도 여기서 끝내지 않는다.
            //
            // 🔴 그런데 주의: DisconnectedAsync 는 "연결됐던 세션이 끊길 때"만 발생하므로,
            //    한 번도 연결된 적이 없으면 재연결 루프가 스스로 시작되지 않는다.
            //    그래서 아래에서 직접 첫 연결을 재시도한다.
            await RetryUntilConnectedAsync(ct).ConfigureAwait(false);
        }

        // 취소될 때까지 유지한다. 실제 수신은 이벤트 핸들러에서 일어난다.
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    /// <summary>
    /// 첫 연결 재시도.
    ///
    /// 📌 이게 없으면 "관제실보다 먼저 뜬 앱"이 영원히 안 붙는다.
    ///    기동 순서를 지키면 안 생기는 문제지만, <b>순서에 의존하는 시스템은 언젠가 깨진다.</b>
    /// </summary>
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
                // 브로커가 아직 안 떴다 — 계속 기다린다.
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
            // 종료 중 오류는 할 수 있는 일이 없다.
        }

        _client.Dispose();
    }
}
