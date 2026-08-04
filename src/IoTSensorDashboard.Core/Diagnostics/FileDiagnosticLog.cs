using System.Globalization;
using System.Text;
using IoTSensorDashboard.Core.Storage;

namespace IoTSensorDashboard.Core.Diagnostics;

/// <summary>
/// 파일에 남기는 진단 로그.
///
/// 위치: <c>%LOCALAPPDATA%\IoTSensorDashboard\logs\{앱}.log</c>
///
/// 🔴 <b>로그도 무한히 자라면 안 된다.</b>
///    「데이터를 다 보관한다」가 자가 다운의 원인이었던 것과 같은 이유다(778MB 사고).
///    상한을 넘으면 직전 것을 <c>.1</c> 로 밀고 새로 시작한다 — 최근 두 개만 남는다.
/// </summary>
public sealed class FileDiagnosticLog : IDiagnosticLog, IDisposable
{
    /// <summary>파일 하나의 상한(5MB).</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly LogLevel _minimum;

    private long _warnings;
    private long _errors;
    private long _writeFailures;
    private bool _disposed;

    /// <param name="app">controlroom · sensorfarm · dashboard</param>
    /// <param name="minimum">
    /// 이 수준 미만은 버린다.
    ///
    /// 🔑 기본이 Info 인 이유: Debug 를 고빈도 경로에서 남발하면
    ///    <b>로그가 그 자체로 병목</b>이 된다. 진단하려고 켠 것이 진단 대상을 바꾸면 안 된다.
    /// </param>
    /// <param name="directory">null 이면 표준 데이터 폴더.</param>
    public FileDiagnosticLog(string app, LogLevel minimum = LogLevel.Info, string? directory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);

        var dir = directory ?? Path.Combine(DbPaths.DataDirectory, "logs");
        Directory.CreateDirectory(dir);

        _path = Path.Combine(dir, $"{app}.log");
        _minimum = minimum;

        Write(LogLevel.Info, "log", $"=== 시작 · {app} · pid {Environment.ProcessId} ===");
    }

    public string Path_ => _path;

    public long Warnings => Interlocked.Read(ref _warnings);

    public long Errors => Interlocked.Read(ref _errors);

    public long WriteFailures => Interlocked.Read(ref _writeFailures);

    public void Write(LogLevel level, string source, string message, Exception? exception = null)
    {
        if (level < _minimum) return;

        switch (level)
        {
            case LogLevel.Warn: Interlocked.Increment(ref _warnings); break;
            case LogLevel.Error: Interlocked.Increment(ref _errors); break;
            default: break;
        }

        var line = Format(level, source, message, exception);

        lock (_gate)
        {
            try
            {
                RollIfNeededLocked();
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch (Exception)
            {
                // 🔒 로그를 쓰다 실패했다고 앱이 죽으면 안 된다.
                //    하지만 조용히 넘기지도 않는다 — 이 카운터가 0 이 아니면 로그를 믿을 수 없다.
                Interlocked.Increment(ref _writeFailures);
            }
        }
    }

    private void RollIfNeededLocked()
    {
        if (!File.Exists(_path)) return;
        if (new FileInfo(_path).Length < MaxBytes) return;

        var previous = _path + ".1";

        // 직전 것을 덮어쓴다 — 최근 두 개만 남긴다.
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(_path, previous);
    }

    private static string Format(LogLevel level, string source, string message, Exception? exception)
    {
        var builder = new StringBuilder();

        builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        builder.Append('\t').Append(Tag(level));
        builder.Append('\t').Append(source);
        builder.Append('\t').Append(message.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '));

        if (exception is not null)
        {
            // 예외는 종류와 메시지를 먼저 — 스택은 그 뒤에 들여쓴다.
            builder.Append('\t').Append(exception.GetType().Name).Append(": ").Append(exception.Message);

            if (exception.StackTrace is { } stack)
            {
                builder.AppendLine();
                foreach (var stackLine in stack.Split('\n'))
                    builder.Append("        ").AppendLine(stackLine.TrimEnd());

                return builder.ToString();
            }
        }

        builder.AppendLine();
        return builder.ToString();
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "???"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Write(LogLevel.Info, "log", "=== 종료 ===");
    }
}
