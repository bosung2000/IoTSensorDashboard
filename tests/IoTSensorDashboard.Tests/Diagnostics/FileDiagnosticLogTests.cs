using IoTSensorDashboard.Core.Diagnostics;
using Xunit;

namespace IoTSensorDashboard.Tests.Diagnostics;

/// <summary>
/// 진단 로그 — 「조용히 실패」의 이차 방어선.
///
/// 화면 카운터는 "몇 건인가"만 말하지만, 로그는 <b>"무엇이 왜"</b>를 말한다.
/// 그래서 로그 자체가 조용히 실패하면 안 된다.
/// </summary>
public sealed class FileDiagnosticLogTests : IDisposable
{
    private readonly string _dir;

    public FileDiagnosticLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "iotsd-log", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private FileDiagnosticLog NewLog(LogLevel minimum = LogLevel.Info) =>
        new("test", minimum, _dir);

    [Fact]
    public void 기록한_것을_파일에서_다시_읽을_수_있다()
    {
        // 🔒 "파일 길이"를 성공의 증거로 쓰지 않는다. 실제로 읽어서 확인한다.
        using var log = NewLog();

        log.Write(LogLevel.Error, "test.source", "무언가 실패했다");

        var text = File.ReadAllText(log.Path_);

        Assert.Contains("무언가 실패했다", text, StringComparison.Ordinal);
        Assert.Contains("test.source", text, StringComparison.Ordinal);
        Assert.Contains("ERR", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 예외의_종류와_메시지가_남는다()
    {
        using var log = NewLog();

        Exception captured;
        try { throw new InvalidOperationException("발행 실패(모의)"); }
        catch (Exception ex) { captured = ex; }

        log.Write(LogLevel.Error, "farm.publish", "발행 루프 오류", captured);

        var text = File.ReadAllText(log.Path_);

        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("발행 실패(모의)", text, StringComparison.Ordinal);
        Assert.Contains("발행 루프 오류", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 경고와_오류를_각각_센다()
    {
        // 화면이 "로그에 오류 N건"을 말할 수 있어야 한다.
        using var log = NewLog();

        log.Write(LogLevel.Info, "s", "정보");
        log.Write(LogLevel.Warn, "s", "경고");
        log.Write(LogLevel.Warn, "s", "경고");
        log.Write(LogLevel.Error, "s", "오류");

        Assert.Equal(2, log.Warnings);
        Assert.Equal(1, log.Errors);
        Assert.Equal(0, log.WriteFailures);
    }

    [Fact]
    public void 최소_수준_미만은_버린다()
    {
        // 📌 Debug 를 고빈도 경로에서 남발하면 로그가 그 자체로 병목이 된다.
        //    진단하려고 켠 것이 진단 대상을 바꾸면 안 된다.
        using var log = NewLog(LogLevel.Warn);

        log.Write(LogLevel.Debug, "s", "디버그다");
        log.Write(LogLevel.Info, "s", "정보다");
        log.Write(LogLevel.Warn, "s", "경고다");

        var text = File.ReadAllText(log.Path_);

        Assert.DoesNotContain("디버그다", text, StringComparison.Ordinal);
        Assert.DoesNotContain("정보다", text, StringComparison.Ordinal);
        Assert.Contains("경고다", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 줄바꿈이_섞인_메시지도_한_줄을_깨지_않는다()
    {
        using var log = NewLog();

        log.Write(LogLevel.Info, "s", "여러\n줄\r짜리\t메시지");

        // 헤더 줄 + 이 줄 = 2줄이어야 한다(시작 배너 포함).
        var lines = File.ReadAllLines(log.Path_).Where(l => l.Length > 0).ToList();

        Assert.All(lines, line => Assert.DoesNotContain("여러\n", line, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("여러 줄 짜리 메시지", StringComparison.Ordinal));
    }

    [Fact]
    public void 쓸_수_없어도_던지지_않고_센다()
    {
        // 🔒 로그를 쓰다 실패했다고 앱이 죽으면 안 된다.
        //    하지만 조용히 넘기지도 않는다.
        using var log = NewLog();

        // 파일을 다른 프로세스가 잠근 상황을 흉내 낸다.
        using (var locked = new FileStream(log.Path_, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var exception = Record.Exception(() => log.Write(LogLevel.Error, "s", "잠긴 동안"));

            Assert.Null(exception);
            Assert.True(log.WriteFailures > 0, "실패를 세지 않으면 로그를 믿을 수 없다");
        }
    }

    [Fact]
    public void 상한을_넘으면_직전_것을_밀고_새로_시작한다()
    {
        // 🔴 로그도 무한히 자라면 안 된다.
        //    「데이터를 다 보관한다」가 자가 다운의 원인이었던 것과 같은 이유다.
        using var log = NewLog();

        var chunk = new string('x', 64 * 1024);
        long written = 0;

        while (written < FileDiagnosticLog.MaxBytes + 128 * 1024)
        {
            log.Write(LogLevel.Info, "bulk", chunk);
            written += chunk.Length;
        }

        var rolled = log.Path_ + ".1";

        Assert.True(File.Exists(rolled), "직전 파일이 .1 로 밀려야 한다");
        Assert.True(new FileInfo(log.Path_).Length < FileDiagnosticLog.MaxBytes,
                    "현재 파일은 상한 아래여야 한다");
    }

    [Fact]
    public void 설정하지_않으면_아무_일도_하지_않는다()
    {
        // 라이브러리 코드가 로그 유무를 신경 쓰지 않아도 되게.
        var previous = Diag.Current;

        try
        {
            Diag.Current = null!;   // null 을 넣어도 안전해야 한다

            var exception = Record.Exception(() =>
            {
                Diag.Info("s", "아무 데도 안 감");
                Diag.Error("s", "이것도", new InvalidOperationException());
            });

            Assert.Null(exception);
            Assert.Equal(0, Diag.Current.Errors);
        }
        finally
        {
            Diag.Current = previous;
        }
    }
}
