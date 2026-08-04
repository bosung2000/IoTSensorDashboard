using System.Globalization;
using IoTSensorDashboard.Core.Storage;

namespace IoTSensorDashboard.Core.Notification;

/// <summary>
/// 파일에 append-only 로 남기는 통지 채널.
///
/// 이번 범위의 유일한 구현이다(이메일·SMS·웹훅은 계약만 열어 둠).
/// </summary>
public sealed class FileNotifier : INotifier
{
    /// <summary>현재 포맷의 열 수.</summary>
    private const int CurrentColumns = 9;

    private const char Separator = '\t';

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<EscalationRecord> _memoryBuffer = [];

    private long _sent;
    private long _failed;

    public FileNotifier(string? path = null)
    {
        _path = path ?? DbPaths.EscalationLog;

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public long Sent => Interlocked.Read(ref _sent);

    public long Failed => Interlocked.Read(ref _failed);

    public string? LastError { get; private set; }

    public DateTimeOffset? LastFailureAt { get; private set; }

    /// <summary>
    /// 한 건 기록한다.
    ///
    /// 🔒 실패해도 <b>던지지 않는다.</b> 통지가 실패했다고 관제 루프가 멈추면
    ///    장애 하나가 시스템 전체를 세우는 셈이 된다.
    ///    대신 결과로 알리고 카운터를 올린다.
    /// </summary>
    public NotifyResult Notify(in Notification n)
    {
        var record = new EscalationRecord(
            n.At, n.Store, n.SensorId, n.Contact, n.Message,
            Delivered: true, Error: null, n.Level, n.Severity);

        lock (_gate)
        {
            try
            {
                File.AppendAllText(_path, Encode(record) + Environment.NewLine);

                Interlocked.Increment(ref _sent);
                return NotifyResult.Ok();
            }
            catch (Exception ex)
            {
                // 파일에 못 남겼어도 "일어난 일"은 사라지면 안 된다.
                // 메모리에 남겨 조회에서 「✗ 미전달」로 드러낸다.
                _memoryBuffer.Add(record with { Delivered = false, Error = ex.Message });

                Interlocked.Increment(ref _failed);
                LastError = ex.Message;
                LastFailureAt = n.At;

                return NotifyResult.Fail(ex.Message);
            }
        }
    }

    /// <summary>
    /// 통지 궤적을 읽는다.
    ///
    /// 🔒 파일에 쓴 「길이」를 성공의 증거로 쓰지 말 것.
    ///    실제로 이 함정에 빠진 적이 있다 — 검증 도구가 파일 길이만 보고
    ///    <b>자기 자신에게 그린을 줬다.</b> 쓴 다음 <b>다시 읽어</b> 확인한다.
    /// </summary>
    public EscalationLogView Read(int max = 500)
    {
        lock (_gate)
        {
            var parsed = new List<EscalationRecord>();
            int malformed = 0;

            if (File.Exists(_path))
            {
                foreach (var line in File.ReadAllLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (TryDecode(line, out var record)) parsed.Add(record);
                    else malformed++;   // 🔴 못 읽은 줄을 조용히 버리지 않는다
                }
            }

            // 파일에 못 남긴 것도 함께 보여준다.
            parsed.AddRange(_memoryBuffer);

            var ordered = parsed.OrderByDescending(r => r.At).ToList();

            return new EscalationLogView(
                Records: max > 0 ? ordered.Take(max).ToList() : [],
                Total: ordered.Count,
                Malformed: malformed,
                Undelivered: ordered.Count(r => !r.Delivered),

                // 🔑 표시 상한과 무관한 전체 기준이다.
                //    최근 500건에 치명이 없다고 「치명 0건」이라 쓰면 거짓말이다.
                Critical: ordered.Count(r => r.Severity == EscalationSeverity.Critical));
        }
    }

    private static string Encode(EscalationRecord r) => string.Join(Separator,
        r.At.ToString("o", CultureInfo.InvariantCulture),
        Clean(r.Store),
        Clean(r.SensorId),
        Clean(r.Contact),
        Clean(r.Message),
        r.Delivered ? "1" : "0",
        Clean(r.Error ?? ""),
        r.Level.ToString(CultureInfo.InvariantCulture),
        ((int)r.Severity).ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// 한 줄을 해석한다.
    ///
    /// 🔑 <b>옛 포맷도 읽는다.</b> 이 로그는 5열 → 6열 → 7열 → 9열로 늘어났다.
    ///    옛 줄을 못 읽어 버리면 「그때는 아무 일도 없었다」로 읽힌다 —
    ///    추적 가능성이 존재 이유인 기록에서 이건 최악이다.
    ///
    ///    | 열 수 | 그때의 포맷 |
    ///    |---|---|
    ///    | 5 | 시각 · 매장 · 센서 · 연락처 · 메시지 |
    ///    | 6 | + 전달 여부 |
    ///    | 7 | + 오류 |
    ///    | 9 | + 단계 · 심각도 (현재) |
    /// </summary>
    private static bool TryDecode(string line, out EscalationRecord record)
    {
        record = default;

        var parts = line.Split(Separator);
        if (parts.Length < 5) return false;

        if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                                     DateTimeStyles.AssumeUniversal, out var at))
            return false;

        bool delivered = parts.Length < 6 || parts[5] == "1";
        string? error = parts.Length >= 7 && !string.IsNullOrEmpty(parts[6]) ? parts[6] : null;

        int level = 1;
        if (parts.Length >= 8) int.TryParse(parts[7], CultureInfo.InvariantCulture, out level);
        if (level <= 0) level = 1;

        var severity = EscalationSeverity.Warning;
        if (parts.Length >= CurrentColumns
            && int.TryParse(parts[8], CultureInfo.InvariantCulture, out var s)
            && Enum.IsDefined(typeof(EscalationSeverity), s))
        {
            severity = (EscalationSeverity)s;
        }

        record = new EscalationRecord(at, parts[1], parts[2], parts[3], parts[4],
                                      delivered, error, level, severity);
        return true;
    }

    /// <summary>구분자와 줄바꿈을 지운다 — 한 줄이 여러 줄로 쪼개지면 해석이 깨진다.</summary>
    private static string Clean(string value) =>
        value.Replace(Separator, ' ').Replace('\r', ' ').Replace('\n', ' ');
}
