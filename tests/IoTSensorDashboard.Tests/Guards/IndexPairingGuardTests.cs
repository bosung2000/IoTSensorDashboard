using IoTSensorDashboard.Core.Health;
using IoTSensorDashboard.Core.Provisioning;
using Xunit;

namespace IoTSensorDashboard.Tests.Guards;

/// <summary>
/// 명부의 두 목록이 <b>같은 순서</b>임을 못박는다.
///
/// 🔴 <b>왜 필요한가</b>: 화면이 센서 상태를 <b>락 한 번</b>으로 받아 오면서
///    <c>SensorIds[i]</c> 의 결과를 <c>Sensors[i]</c> 에 대응시킨다.
///    그 순서가 어긋나면 <b>다른 센서의 상태가 다른 매장에 집계된다</b> —
///    예외도 안 나고 숫자만 조용히 틀린다. 눈으로는 절대 못 잡는 부류다.
///
/// 📌 이 대응은 <see cref="SiteProvisioning"/> 이 두 목록을 같은 루프에서 만들기에
///    지금은 성립한다. 나중에 한쪽만 정렬·필터가 붙는 날 여기서 걸린다.
/// </summary>
public sealed class IndexPairingGuardTests
{
    [Fact]
    public void 센서_명부와_ID_목록은_같은_순서다()
    {
        var provisioning = new SiteProvisioning();

        Assert.Equal(provisioning.Sensors.Count, provisioning.SensorIds.Count);

        for (int i = 0; i < provisioning.Sensors.Count; i++)
            Assert.Equal(provisioning.Sensors[i].Id, provisioning.SensorIds[i]);
    }

    /// <summary>
    /// 배치 조회가 하나씩 묻는 것과 <b>같은 답</b>을 줘야 한다.
    /// 빠르게 만들면서 결과가 달라지면 최적화가 아니라 결함이다.
    /// </summary>
    [Fact]
    public void 배치_조회는_개별_조회와_같은_답을_준다()
    {
        var tracker = new SensorHealthTracker();
        var now = DateTimeOffset.UtcNow;

        string[] ids = ["a", "b", "c", "d"];
        tracker.Expect(ids);

        // a=방금, b=오래전, c·d=한 번도 못 봄
        tracker.Observe("a", now);
        tracker.Observe("b", now - TimeSpan.FromMinutes(10));

        var batch = new SensorStatus[ids.Length];
        tracker.StatusesInto(ids, now, HealthPolicy.Offline, batch);

        for (int i = 0; i < ids.Length; i++)
            Assert.Equal(tracker.Status(ids[i], now, HealthPolicy.Offline), batch[i]);

        // 세 상태가 실제로 다 나오는지도 확인 — 전부 같은 값이면 위 비교가 무의미하다.
        Assert.Equal(SensorStatus.Online, batch[0]);
        Assert.Equal(SensorStatus.Offline, batch[1]);
        Assert.Equal(SensorStatus.Unknown, batch[2]);
    }

    /// <summary>결과 배열이 짧으면 <b>조용히 자르지 않고</b> 던진다.</summary>
    [Fact]
    public void 결과_배열이_짧으면_던진다()
    {
        var tracker = new SensorHealthTracker();
        string[] ids = ["a", "b", "c"];

        Assert.Throws<ArgumentException>(() =>
            tracker.StatusesInto(ids, DateTimeOffset.UtcNow, HealthPolicy.Offline, new SensorStatus[2]));
    }
}
