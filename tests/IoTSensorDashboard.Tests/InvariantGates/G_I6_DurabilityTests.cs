using IoTSensorDashboard.Core.Audit;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Storage;
using IoTSensorDashboard.Tests.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.InvariantGates;

/// <summary>
/// G-I6 · 지속성
///
/// Given  이벤트 · 장애 · 감사를 저장한 뒤
/// When   저장소를 닫았다 다시 엶
/// Then   같은 값 · 같은 타임존 offset 이 읽힌다
///
/// 깨지면: 관제실을 껐다 켜면 세어 둔 값이 사라진다.
///        리포트·SLA 가 아예 성립하지 않는다.
/// </summary>
public sealed class G_I6_DurabilityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 닫았다_다시_열어도_같은_값이_읽힌다()
    {
        using var temp = new TempDb();

        using (var store = temp.OpenStore())
        {
            store.TryAppend(new CountEvent { SensorId = "flir-0001", OccurredAt = T0, Count = 3, Direction = "in" });
            store.TryAppend(new CountEvent { SensorId = "flir-0001", OccurredAt = T0, Count = 2, Direction = "out" });
        }

        using (var reopened = temp.OpenStore())
        {
            Assert.Equal(2L, reopened.Count);

            var events = reopened.Snapshot();
            Assert.Equal("flir-0001", events[0].SensorId);
            Assert.Equal(3, events[0].Count);
            Assert.Equal("in", events[0].Direction);
            Assert.Equal(2, events[1].Count);
            Assert.Equal("out", events[1].Direction);
        }
    }

    [Fact]
    public void 저장한_타임존_offset_이_그대로_돌아온다()
    {
        // 🔴 값이 "같은 순간"인 것으로는 부족하다. 저장한 표기 그대로 돌아와야 왕복이 성립한다.
        //    읽을 때 UTC 로 바꿔 돌려주면 "저장한 것과 읽은 것이 다른" 저장소가 된다.
        using var temp = new TempDb();

        var kst = new DateTimeOffset(2026, 7, 9, 18, 0, 0, TimeSpan.FromHours(9));

        using (var store = temp.OpenStore())
            store.TryAppend(new CountEvent { SensorId = "flir-0001", OccurredAt = kst, Count = 1, Direction = "in" });

        using (var reopened = temp.OpenStore())
        {
            var e = Assert.Single(reopened.Snapshot());
            Assert.Equal(TimeSpan.FromHours(9), e.OccurredAt.Offset);
            Assert.Equal(kst, e.OccurredAt);
        }
    }

    [Fact]
    public void 재시작해도_누적_수신이_0으로_돌아가지_않는다()
    {
        // 손 확인 시나리오 8번: "관제실 재시작 → 누적 수신이 0 으로 안 돌아감".
        using var temp = new TempDb();

        using (var store = temp.OpenStore())
            for (int i = 0; i < 50; i++)
                store.TryAppend(new CountEvent { SensorId = "flir-0001", OccurredAt = T0.AddSeconds(i), Count = 1 });

        using (var reopened = temp.OpenStore())
            Assert.Equal(50L, reopened.Count);
    }

    [Fact]
    public void 장애_이력도_프로세스를_넘어_읽힌다()
    {
        // 📌 관제실이 기록한 장애를 대시보드 프로세스가 읽어 SLA 를 계산한다.
        //    그래서 같은 파일에 있어야 한다.
        using var temp = new TempDb();

        var born = T0;
        var resolved = T0.AddMinutes(7);

        using (var log = temp.OpenOutageLog())
            log.Record(new OutageRecord("flir-0001", "강남점", born, resolved));

        using (var reopened = temp.OpenOutageLog())
        {
            var r = Assert.Single(reopened.Snapshot());
            Assert.Equal("강남점", r.Store);
            Assert.Equal(TimeSpan.FromMinutes(7), r.Duration);
        }
    }

    [Fact]
    public void 감사_로그도_프로세스를_넘어_읽힌다()
    {
        using var temp = new TempDb();

        using (var log = temp.OpenAuditLog())
            log.Record(new AuditEntry("user-1", "Store", "ViewDashboard", "강남점", "g1-s0", T0));

        using (var reopened = temp.OpenAuditLog())
        {
            Assert.Equal(1L, reopened.Count);
            var e = Assert.Single(reopened.Recent());
            Assert.Equal("ViewDashboard", e.Action);
            Assert.Equal("g1-s0", e.Scope);
        }
    }

    [Fact]
    public void 세_기록이_같은_파일에_공존한다()
    {
        // 🔑 파일이 나뉘어 있으면 "관제실이 기록한 것을 대시보드가 읽는" 연결이 끊긴다.
        using var temp = new TempDb();

        using (var store = temp.OpenStore())
        using (var outages = temp.OpenOutageLog())
        using (var audit = temp.OpenAuditLog())
        {
            store.TryAppend(new CountEvent { SensorId = "flir-0001", OccurredAt = T0, Count = 1 });
            outages.Record(new OutageRecord("flir-0001", "강남점", T0, T0.AddMinutes(1)));
            audit.Record(new AuditEntry("user-1", "Store", "ViewDashboard", "강남점", "g1-s0", T0));
        }

        // 파일 하나만 다시 열어도 셋 다 있다.
        using (var store = temp.OpenStore())
        using (var outages = temp.OpenOutageLog())
        using (var audit = temp.OpenAuditLog())
        {
            Assert.Equal(1L, store.Count);
            Assert.Single(outages.Snapshot());
            Assert.Equal(1L, audit.Count);
        }
    }
}
