using IoTSensorDashboard.Core.Domain;

namespace IoTSensorDashboard.Dashboard.Model;

/// <summary>
/// 한 프레임이 그리는 <b>불변 스냅샷</b>.
///
/// 🔑 <b>왜 스냅샷을 따로 만드는가</b>: 패널이 10장이다. 각 패널이 모델을 직접 읽으면
///    같은 프레임 안에서 <b>패널마다 다른 시점의 값</b>을 그리게 된다
///    (그리는 사이에도 수집 스레드는 계속 값을 올린다).
///    그러면 「합계 ≠ 부분의 합」 같은 <b>설명할 수 없는 화면</b>이 나온다.
///    한 번 떠서 전부에게 같은 것을 넘기면 그 부류가 원천 봉쇄된다.
///
/// 🔒 <c>record</c> + <c>init</c> — 만든 뒤 아무도 못 바꾼다. 패널은 그리기만 한다(멍청한 화면).
/// </summary>
public sealed record DashboardSnapshot
{
    /// <summary>이 스냅샷을 뜬 순간 — <b>시점 도장</b>.</summary>
    /// <remarks>
    /// 🔑 화면이 자기 값의 <b>나이</b>를 스스로 말하게 하는 근거다.
    ///    갱신이 멈추면 이 값과 현재 시각의 차이가 스스로 자라므로
    ///    <b>멈춘 화면이 정상처럼 보이지 않는다.</b>
    /// </remarks>
    public required DateTimeOffset TakenAt { get; init; }

    public required Role Role { get; init; }

    /// <summary>지금 보고 있는 범위의 표시 이름(예: "전체", "수도권본부").</summary>
    public required string ScopeLabel { get; init; }

    public required long TotalIn { get; init; }
    public required long TotalOut { get; init; }

    /// <summary>
    /// 현재 체류 추정 = 유입 − 유출.
    ///
    /// 🔑 <b>음수는 0 으로 본다.</b> 센서 오차로 유출이 유입을 넘을 수 있는데,
    ///    「−37명이 매장에 있다」는 읽는 사람을 혼란시킬 뿐 아무 정보도 주지 않는다.
    /// </summary>
    public long Stay => Math.Max(0, TotalIn - TotalOut);

    /// <summary>I1 「정확히 1회」로 센 이벤트 누적 — 중복 제거가 실제로 동작한다는 증거.</summary>
    public required long UniqueEvents { get; init; }

    public required int OnlineSensors { get; init; }
    public required int TotalSensors { get; init; }

    /// <summary>마지막으로 데이터가 도착한 순간. 화면 갱신과 <b>별개</b>로 「데이터가 살아 있는가」.</summary>
    public DateTimeOffset? LastEventAt { get; init; }

    public required IReadOnlyList<GroupStat> Groups { get; init; }
    public required IReadOnlyList<StoreStat> Stores { get; init; }

    /// <summary>처리량(유입+유출) 상위 센서.</summary>
    public required IReadOnlyList<SensorStat> TopThroughput { get; init; }
    public required IReadOnlyList<SensorStat> TopIn { get; init; }
    public required IReadOnlyList<SensorStat> TopOut { get; init; }

    /// <summary>매장별 최근 레이트 추이(스파크라인용). 오래된 것 → 최신 순.</summary>
    public required IReadOnlyList<StoreTrend> Trends { get; init; }

    /// <summary>분 단위 버킷(I3 표시 시각 기준). 오래된 것 → 최신 순.</summary>
    public required IReadOnlyList<MinutePoint> Minutes { get; init; }

    /// <summary>상태 변화 기록 — 최신이 앞.</summary>
    public required IReadOnlyList<LogEntry> Log { get; init; }

    public static DashboardSnapshot Empty(Role role, string label) => new()
    {
        TakenAt = DateTimeOffset.Now,
        Role = role,
        ScopeLabel = label,
        TotalIn = 0,
        TotalOut = 0,
        UniqueEvents = 0,
        OnlineSensors = 0,
        TotalSensors = 0,
        Groups = [],
        Stores = [],
        TopThroughput = [],
        TopIn = [],
        TopOut = [],
        Trends = [],
        Minutes = [],
        Log = [],
    };
}

/// <summary>본부 한 곳의 집계.</summary>
public sealed record GroupStat(string Id, string Name, long In, long Out, int Online, int Total)
{
    /// <summary>
    /// 가동률. <b>분모가 0 이면 null</b> — 「100%」가 아니다.
    ///
    /// 🔴 관측하지 못한 것을 만점으로 그리면 <b>가장 비싼 거짓말</b>이 된다.
    ///    "장애 기록이 없다"는 "무사했다"가 아니라 <b>"기록이 없다"</b>일 뿐이다.
    /// </summary>
    public double? Uptime => Total > 0 ? (double)Online / Total : null;
}

/// <summary>
/// 매장 한 곳의 집계. <b>누적값</b>이다 — 초당 레이트는 표시 계층이 델타로 계산한다.
/// </summary>
public sealed record StoreStat(
    string Id, string Name, string GroupId, long In, long Out, int Online, int Total)
{
    /// <summary>
    /// 사람이 읽을 상태 문구.
    ///
    /// 🔑 <b>「센서 없음」·「측정 불가」·「정상」을 구분한다.</b>
    ///    셋을 뭉개면 전부 0 으로 보이고, 그러면 「손님이 없었다」와
    ///    「우리가 못 봤다」가 같은 화면이 된다.
    /// </summary>
    public string StatusText => Total == 0
        ? "센서 없음"
        : Online == 0
            ? "측정 불가 (전부 무응답)"
            : Online < Total
                ? $"일부 오프라인 {Total - Online:N0}대"
                : "정상";
}

/// <summary>센서 순위 한 칸.</summary>
public sealed record SensorStat(string SensorId, string StoreName, long In, long Out)
{
    public long Throughput => In + Out;
}

/// <summary>매장 하나의 최근 레이트 추이.</summary>
/// <param name="In">초당 유입 레이트 시계열(오래된 것 → 최신).</param>
/// <remarks>
/// 🔑 <see cref="Id"/> 를 들고 다니는 이유: <see cref="DashboardSnapshot.Stores"/> 와
///    <b>인덱스가 맞을 것이라 가정하지 않기 위해서</b>다. 지금은 같은 루프에서 나오지만,
///    나중에 어느 한쪽만 정렬·필터를 걸면 그 가정이 조용히 깨지고
///    <b>다른 매장의 그래프에 다른 매장 이름이 붙는다</b> — 눈으로는 못 잡는 부류다.
/// </remarks>
public sealed record StoreTrend(string Id, string Name, IReadOnlyList<double> In, IReadOnlyList<double> Out)
{
    /// <summary>표시용 평균을 낼 표본 수.</summary>
    private const int Window = 5;

    /// <summary>
    /// 화면에 적는 유입 레이트 — <b>최근 몇 표본의 평균</b>.
    ///
    /// 🔴 <b>맨 마지막 표본 하나를 쓰면 안 된다.</b> 이벤트는 정수라 초당 1.4건인 매장의
    ///    표본은 <c>1, 2, 1, 1, 2</c> 처럼 튄다. 마지막 값만 보여주면 같은 상황인데도
    ///    화면 숫자가 <b>매초 널뛰어</b> 「무슨 일이 났나」로 읽힌다.
    ///    평균을 내면 <c>1.4</c> 라는, 실제에 가까운 값이 안정적으로 남는다.
    ///
    /// 📌 이건 값을 부드럽게 <b>꾸미는</b> 것이 아니라 <b>측정 창을 넓히는</b> 것이다 —
    ///    짧은 창에서 「초당」은 애초에 잴 수 없는 양이다.
    /// </summary>
    public double RecentIn => Average(In);

    /// <summary>화면에 적는 유출 레이트 — 최근 몇 표본의 평균.</summary>
    public double RecentOut => Average(Out);

    private static double Average(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;

        int take = Math.Min(Window, values.Count);
        double sum = 0;

        for (int i = values.Count - take; i < values.Count; i++) sum += values[i];

        return sum / take;
    }
}

/// <summary>분 버킷 한 칸.</summary>
public sealed record MinutePoint(DateTimeOffset Bucket, long In, long Out);

/// <summary>상태 변화 한 줄.</summary>
/// <param name="Kind">분류(피드·센서·범위) — 화면에서 색으로 구분한다.</param>
public sealed record LogEntry(DateTimeOffset At, string Kind, string Message);
