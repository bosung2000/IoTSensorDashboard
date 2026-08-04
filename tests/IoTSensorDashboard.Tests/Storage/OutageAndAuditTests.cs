using IoTSensorDashboard.Core.Audit;
using IoTSensorDashboard.Core.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.Storage;

/// <summary>
/// 장애 이력과 감사 로그 — 둘 다 append-only 이고 이벤트와 같은 파일에 산다.
/// </summary>
public sealed class OutageAndAuditTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    // ── 장애 이력 ────────────────────────────────────────────────────────

    [Fact]
    public void 장애_구간의_길이가_계산된다()
    {
        using var temp = new TempDb();
        using var log = temp.OpenOutageLog();

        log.Record(new OutageRecord("flir-0001", "강남점", T0, T0.AddMinutes(12)));

        Assert.Equal(TimeSpan.FromMinutes(12), Assert.Single(log.Snapshot()).Duration);
    }

    [Fact]
    public void 장애_이력은_기록_순서를_보존한다()
    {
        using var temp = new TempDb();
        using var log = temp.OpenOutageLog();

        for (int i = 0; i < 5; i++)
            log.Record(new OutageRecord($"flir-{i:D4}", "강남점", T0.AddMinutes(i), T0.AddMinutes(i + 1)));

        var ids = log.Snapshot().Select(o => o.SensorId).ToArray();
        Assert.Equal(["flir-0000", "flir-0001", "flir-0002", "flir-0003", "flir-0004"], ids);
    }

    [Fact]
    public void 같은_센서의_장애가_여러_번이면_전부_남는다()
    {
        // 🔑 SLA 는 "몇 번" 과 "얼마나 오래" 를 둘 다 쓴다. 합쳐 버리면 장애 건수를 잃는다.
        using var temp = new TempDb();
        using var log = temp.OpenOutageLog();

        log.Record(new OutageRecord("flir-0001", "강남점", T0, T0.AddMinutes(5)));
        log.Record(new OutageRecord("flir-0001", "강남점", T0.AddHours(1), T0.AddHours(1).AddMinutes(3)));

        Assert.Equal(2, log.Snapshot().Count);
    }

    // ── 감사 로그 ────────────────────────────────────────────────────────

    [Fact]
    public void 감사_로그는_최신순으로_돌려준다()
    {
        using var temp = new TempDb();
        using var log = temp.OpenAuditLog();

        for (int i = 0; i < 5; i++)
            log.Record(new AuditEntry($"user-{i}", "Store", "ViewDashboard", "강남점", "g1-s0", T0.AddMinutes(i)));

        var actors = log.Recent().Select(e => e.Actor).ToArray();
        Assert.Equal(["user-4", "user-3", "user-2", "user-1", "user-0"], actors);
    }

    [Fact]
    public void 전체_건수는_표시_상한과_별개다()
    {
        // 🔴 표시 건수만 주면 화면이 "이게 전부"라고 오해하게 만든다.
        //    전체 건수를 따로 알 수 있어야 "전체 N건 중 최근 M건" 이라고 말할 수 있다.
        using var temp = new TempDb();
        using var log = temp.OpenAuditLog();

        for (int i = 0; i < 20; i++)
            log.Record(new AuditEntry($"user-{i}", "Store", "ViewDashboard", "강남점", "g1-s0", T0.AddMinutes(i)));

        Assert.Equal(5, log.Recent(5).Count);
        Assert.Equal(20L, log.Count);
    }

    [Fact]
    public void 상한이_0_이하면_빈_목록이다()
    {
        using var temp = new TempDb();
        using var log = temp.OpenAuditLog();

        log.Record(new AuditEntry("user-1", "Store", "ViewDashboard", "강남점", "g1-s0", T0));

        Assert.Empty(log.Recent(0));
        Assert.Empty(log.Recent(-1));
        Assert.Equal(1L, log.Count);   // 있는데 안 보여줄 뿐이다
    }

    [Fact]
    public void 스코프_접근_기록이_전부_남는다()
    {
        // 감사 로그의 존재 이유 — 누가 어느 범위를 봤는지.
        using var temp = new TempDb();
        using var log = temp.OpenAuditLog();

        log.Record(new AuditEntry("user-1", "Store", "ViewDashboard", "강남점", "g1-s0", T0));
        log.Record(new AuditEntry("user-1", "TotalAdmin", "ExportCsv", "전체", "*", T0.AddMinutes(1)));

        var recent = log.Recent();
        Assert.Equal("ExportCsv", recent[0].Action);
        Assert.Equal("*", recent[0].Scope);
        Assert.Equal("TotalAdmin", recent[0].Role);
        Assert.Equal("ViewDashboard", recent[1].Action);
    }

    [Fact]
    public void 비어_있으면_0건이다()
    {
        // 🔒 "빈 목록"과 "못 읽음"은 다른 사실이다.
        //    여기서는 정말로 비어 있는 경우가 어떤 모양인지 못박아 둔다.
        using var temp = new TempDb();
        using var log = temp.OpenAuditLog();

        Assert.Empty(log.Recent());
        Assert.Equal(0L, log.Count);
    }
}
