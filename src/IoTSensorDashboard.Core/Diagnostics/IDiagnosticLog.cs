namespace IoTSensorDashboard.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>
/// 진단 로그.
///
/// 🔑 이 시스템의 결함은 대부분 <b>「조용히 실패」</b>였다.
///    화면 카운터가 그 일차 방어선이고, 이 로그가 <b>이차 방어선</b>이다 —
///    카운터는 "몇 건인가"만 말하지만 로그는 <b>"무엇이 왜"</b>를 말한다.
///
/// 🔒 로그 자체가 조용히 실패하면 안 되므로 <see cref="WriteFailures"/> 를 센다.
/// </summary>
public interface IDiagnosticLog
{
    void Write(LogLevel level, string source, string message, Exception? exception = null);

    /// <summary>기록된 경고 수.</summary>
    long Warnings { get; }

    /// <summary>기록된 오류 수.</summary>
    long Errors { get; }

    /// <summary>로그를 쓰지 못한 횟수. 🔒 0 이 아니면 로그를 믿을 수 없다.</summary>
    long WriteFailures { get; }
}

/// <summary>로그를 남기지 않는 구현. 테스트와 기본값용.</summary>
public sealed class NullDiagnosticLog : IDiagnosticLog
{
    public static readonly NullDiagnosticLog Instance = new();

    private NullDiagnosticLog() { }

    public void Write(LogLevel level, string source, string message, Exception? exception = null) { }

    public long Warnings => 0;

    public long Errors => 0;

    public long WriteFailures => 0;
}

/// <summary>
/// 어디서든 부를 수 있는 진입점.
///
/// 앱이 시작할 때 <see cref="Current"/> 를 파일 로그로 바꾼다.
/// 설정하지 않으면 아무 일도 하지 않으므로, 라이브러리 코드가 로그 유무를 신경 쓰지 않아도 된다.
/// </summary>
public static class Diag
{
    private static IDiagnosticLog _current = NullDiagnosticLog.Instance;

    public static IDiagnosticLog Current
    {
        get => _current;
        set => _current = value ?? NullDiagnosticLog.Instance;
    }

    public static void Debug(string source, string message) =>
        _current.Write(LogLevel.Debug, source, message);

    public static void Info(string source, string message) =>
        _current.Write(LogLevel.Info, source, message);

    public static void Warn(string source, string message, Exception? exception = null) =>
        _current.Write(LogLevel.Warn, source, message, exception);

    public static void Error(string source, string message, Exception? exception = null) =>
        _current.Write(LogLevel.Error, source, message, exception);
}
