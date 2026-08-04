using MQTTnet.Client;

namespace IoTSensorDashboard.Mqtt;

/// <summary>
/// 끊기면 <b>연결될 때까지</b> 다시 붙는다.
///
/// ⭐ 이 파일이 이 프로젝트에서 가장 비쌌던 사고의 봉합이다.
/// </summary>
public static class MqttReconnect
{
    /// <summary>
    /// keepalive 5초 (기본 15초 아님).
    ///
    /// 📌 근거 — 실제로 겪은 사건:
    ///    PC 절전에서 깨어나면 소켓이 죽어 있는데 클라이언트가 그걸 모른다.
    ///    TCP 는 양쪽 + NIC 이 모두 살아 있어야 유지되는데, 절전 중 그게 깨진다.
    ///    그런데도 IsConnected 는 keepalive 타임아웃 전까지 <b>거짓 참</b>을 돌려준다(좀비 소켓).
    ///
    ///    keepalive 주기 안에 감지해야 DisconnectedAsync 가 발화하고 재연결 루프가 돈다.
    ///
    ///    · 15초면 감지가 늦다
    ///    · 1~2초면 CPU 부하 시 오탐 단절이 난다
    ///    · 5초가 회복 속도와 안정성의 균형점이다
    ///
    ///    전원 이벤트 훅(SystemEvents.PowerModeChanged)보다 안전하다 —
    ///    라이브러리 내부 타이머라 스레드 안전하고, Modern Standby(S0)에서도 동작한다.
    /// </summary>
    public static readonly TimeSpan RecommendedKeepAlive = TimeSpan.FromSeconds(5);

    /// <summary>재연결 시도 간격.</summary>
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 끊김을 감지하면 연결될 때까지 백오프 루프를 돈다.
    ///
    /// 🔴 반드시 <c>while</c> 이어야 한다. <c>if</c> 나 단발 호출로 바꾸지 말 것.
    ///
    /// 📌 근거 — 실측 회귀(가장 비쌌던 사고):
    ///
    ///    MQTTnet 의 DisconnectedAsync 는 <b>"연결됐던 세션이 끊길 때"만</b> 발생한다.
    ///    핸들러 안에서 ConnectAsync 를 한 번만 호출하고 실패를 삼키면,
    ///    클라이언트는 (재)연결된 적이 없으므로 <b>DisconnectedAsync 가 다시 발생하지 않는다.</b>
    ///    → 영구 정지.
    ///
    ///    절전 7시간 복귀 후 수집과 대시보드가 멈췄고, 센서 팜만 살아 있어
    ///    "발행은 되는데 아무도 수신 못 함" 상태가 됐다.
    ///    화면은 멀쩡해 보여서 "네트워크 문제"로 오진하기 딱 좋았다.
    /// </summary>
    public static void EnableAutoReconnect(
        this IMqttClient client,
        MqttClientOptions options,
        CancellationToken ct,
        TimeSpan? backoff = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var delay = backoff ?? DefaultBackoff;

        // 재연결 루프가 겹쳐 도는 것을 막는다.
        // 끊김 이벤트가 연달아 오면 루프가 여러 개 생겨 서로를 방해한다.
        int reconnecting = 0;

        client.DisconnectedAsync += async _ =>
        {
            if (ct.IsCancellationRequested) return;
            if (Interlocked.Exchange(ref reconnecting, 1) == 1) return;

            try
            {
                while (!ct.IsCancellationRequested && !client.IsConnected)
                {
                    try
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        await client.ConnectAsync(options, ct).ConfigureAwait(false);
                        // 성공하면 ConnectedAsync 가 재구독한다.
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // 브로커가 아직 안 떴거나 순간 블립 — 계속 재시도한다.
                        // 🔒 여기서 루프를 벗어나면 그게 곧 영구 정지다.
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref reconnecting, 0);
            }
        };
    }
}
