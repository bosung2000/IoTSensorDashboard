using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace IoTSensorDashboard.Tests.Mqtt;

/// <summary>
/// 통합 테스트용 보조 도구.
///
/// 🔒 실제 브로커 포트(5281)를 쓰지 않는다.
///    테스트가 개발 중인 앱과 포트를 다투면 원인 모를 실패가 난다.
/// </summary>
internal static class MqttTestHelpers
{
    /// <summary>
    /// 지금 비어 있는 포트를 하나 얻는다.
    ///
    /// ⚠️ 얻은 뒤 실제로 쓰기까지 아주 짧은 틈이 있어 이론상 경합이 가능하다.
    ///    테스트 목적에는 충분하고, 고정 포트를 쓰는 것보다 훨씬 안전하다
    ///    (고정 포트는 테스트가 병렬로 돌면 반드시 부딪힌다).
    /// </summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// 조건이 참이 될 때까지 기다린다.
    ///
    /// 🔑 고정된 시간만큼 자고 확인하는 방식(Task.Delay 후 Assert)은 flaky 하다 —
    ///    빠른 기계에서는 낭비고 느린 기계에서는 실패한다.
    ///    "될 때까지 짧게 자며 확인"이 옳다.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(20).ConfigureAwait(false);
        }

        return condition();
    }

    /// <summary>기본 대기 한도. CI 가 느릴 수 있어 넉넉히 준다.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
}
