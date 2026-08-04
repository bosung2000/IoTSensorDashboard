namespace IoTSensorDashboard.ControlRoom.Model;

/// <summary>
/// 파이프라인 한 순간의 모습 — 화면이 그리는 <b>불변 스냅샷</b>.
///
/// 🔑 관제실 화면의 질문은 「지금 잘 돌아가는가」가 아니라
///    <b>「어디서 막혔는가」</b>다. 그래서 값 하나가 아니라 <b>단계별</b> 값이 필요하다.
///    수신은 많은데 저장이 안 늘면 저장이 병목이고, 백로그가 차면 워커가 모자란다.
/// </summary>
public sealed record PipelineSnapshot
{
    public required DateTimeOffset TakenAt { get; init; }

    public required bool BrokerRunning { get; init; }
    public required bool IngestConnected { get; init; }

    public required int SensorsOnline { get; init; }
    public required int SensorsTotal { get; init; }

    /// <summary>가동률. <b>분모가 0 이면 null</b> — 「100%」가 아니라 「측정 불가」다.</summary>
    public double? Uptime => SensorsTotal > 0 ? (double)SensorsOnline / SensorsTotal : null;

    /// <summary>큐에 밀려 있는 수 — <b>여기가 차면 뒤가 못 따라오고 있다는 뜻</b>이다.</summary>
    public required int Backlog { get; init; }

    public required int Workers { get; init; }
    public required int MaxWorkers { get; init; }

    /// <summary>초당 수신 — <b>실제 경과 시간</b>으로 나눈 값이어야 한다.</summary>
    public required double ReceiveRate { get; init; }

    /// <summary>초당 저장. 수신과 벌어지면 그 차이가 곧 병목의 크기다.</summary>
    public required double StoreRate { get; init; }

    public required double AvgLatencyMicros { get; init; }

    /// <summary>이번 세션 수신 수.</summary>
    public required long TotalReceived { get; init; }

    /// <summary>
    /// 저장소 전체 행 수 — <b>전체 기간</b>이다(재시작해도 남는다).
    ///
    /// 🔴 <b>세션 카운터와 나란히 놓지 말 것.</b> 실제로 「저장 176만 · 수신 1,720」이
    ///    한 그림에 붙어 <b>저장이 수신의 1,000배</b>로 읽힌 적이 있다.
    ///    계산은 둘 다 맞는데, <b>기간이 다른 값을 붙여 놓은 것</b>만으로 화면이 거짓말을 한다.
    ///    같이 보여줘야 한다면 라벨에 기간을 반드시 적는다.
    /// </summary>
    public required long TotalStored { get; init; }

    /// <summary>
    /// 이번 세션에 <b>실제로 append 된</b> 수 — 흐름 그림은 이 값을 쓴다.
    ///
    /// 🔑 수신과 <b>같은 기간</b>이라 나란히 놓아도 뜻이 통한다.
    ///    수신보다 작으면 그 차이가 중복(I1 이 접은 것)·거부·격리다.
    /// </summary>
    public required long SessionStored { get; init; }

    /// <summary>중복(I1 이 접은 것) — <b>0 이 아니어야 정상</b>이다. 재전송은 늘 있다.</summary>
    public required long Duplicate { get; init; }

    public required long Rejected { get; init; }
    public required long Implausible { get; init; }

    /// <summary>
    /// 과부하로 <b>버린</b> 수.
    ///
    /// 🔴 버리는 경로가 있으면 <b>버린 수를 세고 화면에 내야 한다.</b>
    ///    조용한 폐기는 「유실 0」이라는 거짓말의 씨앗이다.
    /// </summary>
    public required long Dropped { get; init; }

    /// <summary>최근 처리량 추이(오래된 것 → 최신) — 활동 스트립용.</summary>
    public required IReadOnlyList<double> Activity { get; init; }

    /// <summary>감지 피드 — 최신이 앞.</summary>
    public required IReadOnlyList<FeedLine> Feed { get; init; }

    public static PipelineSnapshot Empty() => new()
    {
        TakenAt = DateTimeOffset.Now,
        BrokerRunning = false,
        IngestConnected = false,
        SensorsOnline = 0,
        SensorsTotal = 0,
        Backlog = 0,
        Workers = 0,
        MaxWorkers = 1,
        ReceiveRate = 0,
        StoreRate = 0,
        AvgLatencyMicros = 0,
        TotalReceived = 0,
        TotalStored = 0,
        SessionStored = 0,
        Duplicate = 0,
        Rejected = 0,
        Implausible = 0,
        Dropped = 0,
        Activity = [],
        Feed = [],
    };
}

/// <summary>피드 한 줄.</summary>
/// <param name="Level">정상 · 주의 · 오류 — 화면에서 색으로 갈린다.</param>
public sealed record FeedLine(DateTimeOffset At, FeedLevel Level, string Message);

/// <summary>피드 줄의 심각도.</summary>
public enum FeedLevel
{
    Normal,
    Warn,
    Error
}
