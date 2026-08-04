using System.Globalization;
using IoTSensorDashboard.Core.Notification;
using Xunit;

// 이 파일의 네임스페이스(…Tests.Notification)와 타입 이름(Notification)이 겹친다.
// alias 로 어느 쪽인지 명시한다.
using CoreNotification = IoTSensorDashboard.Core.Notification.Notification;

namespace IoTSensorDashboard.Tests.Notification;

/// <summary>
/// 통지 채널 — <b>실패를 삼키지 않는다</b>.
///
/// 📌 실제 사건: 화면은 「⚠ 미확인 → 자동 통지됨」이라 쓰는데 담당자는 아무것도 못 받았다.
///    Notify 가 void 이고 구현이 예외를 삼켜서 **호출자가 실패를 알 방법이 없었다.**
///    통지는 드물게 일어나 주기 하트비트로도 못 잡는다.
///
/// > 장애를 알리는 장치가 조용히 실패하면, 장애가 두 번 일어나는 셈이다.
/// </summary>
public sealed class FileNotifierTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir;
    private readonly string _path;

    public FileNotifierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "iotsd-notify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "escalations.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static CoreNotification Note(
        string sensorId = "flir-0001",
        int level = 1,
        EscalationSeverity severity = EscalationSeverity.Warning) =>
        new(sensorId, "강남점", "010-0000-0000", "센서 무응답", T0, level, severity);

    // ── 기본 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 보낸_것을_다시_읽을_수_있다()
    {
        // 🔒 파일에 쓴 「길이」를 성공의 증거로 쓰지 말 것.
        //    실제로 이 함정에 빠진 적이 있다 — 검증 도구가 파일 길이만 보고
        //    **자기 자신에게 그린을 줬다.** 쓴 다음 **다시 읽어** 확인한다.
        var notifier = new FileNotifier(_path);

        var result = notifier.Notify(Note());

        Assert.True(result.Delivered);
        Assert.Null(result.Error);
        Assert.Equal(1, notifier.Sent);
        Assert.Equal(0, notifier.Failed);

        var view = notifier.Read();
        var record = Assert.Single(view.Records);

        Assert.Equal("flir-0001", record.SensorId);
        Assert.Equal("강남점", record.Store);
        Assert.Equal("센서 무응답", record.Message);
        Assert.True(record.Delivered);
    }

    [Fact]
    public void 최신순으로_돌려준다()
    {
        var notifier = new FileNotifier(_path);

        for (int i = 0; i < 5; i++)
            notifier.Notify(Note($"flir-{i:D4}") with { At = T0.AddMinutes(i) });

        var records = notifier.Read().Records;

        Assert.Equal("flir-0004", records[0].SensorId);
        Assert.Equal("flir-0000", records[^1].SensorId);
    }

    [Fact]
    public void 단계와_심각도가_보존된다()
    {
        var notifier = new FileNotifier(_path);

        notifier.Notify(Note(level: 3, severity: EscalationSeverity.Critical));

        var record = Assert.Single(notifier.Read().Records);

        Assert.Equal(3, record.Level);
        Assert.Equal(EscalationSeverity.Critical, record.Severity);
    }

    // ── 🔴 전부인 척하지 않는다 ──────────────────────────────────────────

    [Fact]
    public void 목록이_잘려도_전체_건수를_함께_알려준다()
    {
        // 🔴 표시 건수만 주면 화면이 「이게 전부」라고 오해하게 만든다.
        var notifier = new FileNotifier(_path);

        for (int i = 0; i < 20; i++)
            notifier.Notify(Note($"flir-{i:D4}") with { At = T0.AddMinutes(i) });

        var view = notifier.Read(max: 5);

        Assert.Equal(5, view.Records.Count);
        Assert.Equal(20, view.Total);       // 「전체 20건 중 최근 5건」이라고 쓸 수 있다
    }

    [Fact]
    public void 치명_건수는_표시_상한과_무관한_전체_기준이다()
    {
        // 🔑 최근 5건에 치명이 없다고 「치명 0건」이라 쓰면 거짓말이다.
        var notifier = new FileNotifier(_path);

        // 오래된 것 중에 치명이 하나 있다.
        notifier.Notify(Note(level: 3, severity: EscalationSeverity.Critical) with { At = T0 });

        for (int i = 1; i <= 10; i++)
            notifier.Notify(Note() with { At = T0.AddMinutes(i) });

        var view = notifier.Read(max: 5);

        Assert.DoesNotContain(view.Records, r => r.Severity == EscalationSeverity.Critical);
        Assert.Equal(1, view.Critical);     // ✅ 전체 기준으로는 1건이다
    }

    [Fact]
    public void 비어_있으면_0건이다()
    {
        var view = new FileNotifier(_path).Read();

        Assert.Empty(view.Records);
        Assert.Equal(0, view.Total);
        Assert.Equal(0, view.Malformed);
    }

    // ── 옛 포맷 호환 ─────────────────────────────────────────────────────

    [Fact]
    public void 옛_5열_포맷도_읽는다()
    {
        // 🔑 옛 줄을 못 읽어 버리면 「그때는 아무 일도 없었다」로 읽힌다 —
        //    추적 가능성이 존재 이유인 기록에서 이건 최악이다.
        var iso = T0.ToString("o", CultureInfo.InvariantCulture);
        File.WriteAllText(_path, $"{iso}\t강남점\tflir-0001\t010-0000-0000\t센서 무응답\n");

        var view = new FileNotifier(_path).Read();
        var record = Assert.Single(view.Records);

        Assert.Equal("강남점", record.Store);
        Assert.True(record.Delivered);      // 옛 포맷엔 이 열이 없으니 전달된 것으로 본다
        Assert.Equal(0, view.Malformed);
    }

    [Fact]
    public void 옛_6열_7열도_읽는다()
    {
        var iso = T0.ToString("o", CultureInfo.InvariantCulture);
        File.WriteAllText(_path,
            $"{iso}\t강남점\tflir-0001\t010\t메시지\t0\n" +                 // 6열
            $"{iso}\t잠실점\tflir-0002\t010\t메시지\t0\t전송 실패\n");       // 7열

        var view = new FileNotifier(_path).Read();

        Assert.Equal(2, view.Records.Count);
        Assert.Equal(0, view.Malformed);
        Assert.Equal(2, view.Undelivered);
        Assert.Contains(view.Records, r => r.Error == "전송 실패");
    }

    [Fact]
    public void 못_읽은_줄은_조용히_버리지_않고_센다()
    {
        // 🔴 Malformed > 0 이면 화면이 「로그가 손상됐다」고 경고해야 한다.
        //    빈 목록으로 뭉개면 「아무 일도 없었다」로 읽힌다.
        var iso = T0.ToString("o", CultureInfo.InvariantCulture);
        File.WriteAllText(_path,
            $"{iso}\t강남점\tflir-0001\t010\t메시지\t1\t\t1\t0\n" +
            "이건 로그 형식이 아니다\n" +
            "너무\t짧다\n");

        var view = new FileNotifier(_path).Read();

        Assert.Single(view.Records);
        Assert.Equal(2, view.Malformed);
    }

    // ── 🔴 실패를 드러낸다 ───────────────────────────────────────────────

    [Fact]
    public void 쓸_수_없으면_실패를_반환하고_센다()
    {
        // 경로가 디렉터리라 파일로 쓸 수 없는 상황을 만든다.
        var notifier = new FileNotifier(_dir);

        var result = notifier.Notify(Note());

        Assert.False(result.Delivered);
        Assert.NotNull(result.Error);
        Assert.Equal(0, notifier.Sent);
        Assert.Equal(1, notifier.Failed);
        Assert.NotNull(notifier.LastError);
        Assert.NotNull(notifier.LastFailureAt);
    }

    [Fact]
    public void 파일에_못_남긴_것도_조회에_드러난다()
    {
        // 🔒 "일어난 일"이 사라지면 안 된다. 메모리에라도 남겨 미전달로 표시한다.
        var notifier = new FileNotifier(_dir);

        notifier.Notify(Note());

        var view = notifier.Read();

        Assert.Single(view.Records);
        Assert.False(view.Records[0].Delivered);
        Assert.Equal(1, view.Undelivered);
    }

    [Fact]
    public void 실패해도_던지지_않는다()
    {
        // 🔒 통지가 실패했다고 관제 루프가 멈추면
        //    장애 하나가 시스템 전체를 세우는 셈이 된다.
        var notifier = new FileNotifier(_dir);

        var exception = Record.Exception(() => notifier.Notify(Note()));

        Assert.Null(exception);
    }

    [Fact]
    public void 구분자가_섞인_메시지도_한_줄을_깨지_않는다()
    {
        // 메시지에 탭이나 줄바꿈이 들어오면 한 줄이 여러 줄로 쪼개져 해석이 깨진다.
        var notifier = new FileNotifier(_path);

        notifier.Notify(Note() with { Message = "줄바꿈\n과\t탭이\r있다" });

        var view = notifier.Read();

        Assert.Single(view.Records);
        Assert.Equal(0, view.Malformed);
    }
}
