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

    /// <summary>
    /// 센서 <b>응답률</b>. <b>분모가 0 이면 null</b> — 「100%」가 아니라 「측정 불가」다.
    ///
    /// ⚠️ 이건 <b>「응답하는가」</b>이지 <b>「일하고 있는가」</b>가 아니다.
    ///    핑에 답만 해도 온라인으로 세므로, 발신이 멈춰도 100% 가 나온다.
    ///    작업량은 <see cref="DataFlowing"/> 과 수신 레이트로 판단한다.
    /// </summary>
    public double? Uptime => SensorsTotal > 0 ? (double)SensorsOnline / SensorsTotal : null;

    /// <summary>
    /// <b>데이터를 보내고 있는</b> 센서 수 — 핑 응답만 하는 센서는 빠진다.
    ///
    /// 🔑 <see cref="SensorsOnline"/> 과 <b>나란히 놓으라고</b> 있는 값이다.
    ///    두 숫자의 차이가 곧 「살아 있지만 안 보내는 센서」다.
    ///    문장으로 설명하는 것보다 숫자 두 개를 붙여 놓는 편이 빨리 읽힌다.
    /// </summary>
    public required int SensorsSending { get; init; }

    /// <summary>데이터 기준 비율. 분모가 0 이면 null(측정 불가).</summary>
    public double? DataRate => SensorsTotal > 0 ? (double)SensorsSending / SensorsTotal : null;

    /// <summary>
    /// 최근에 <b>실제 데이터</b>가 들어오고 있는가.
    ///
    /// 🔴 <b>이 값이 없으면 화면이 거짓말을 한다 — 실측으로 재현했다.</b>
    ///    발신을 완전히 멈춘 상태에서 관제실은 이렇게 보였다:
    ///    가동률 100.00% · 센서 1,000/1,000 온라인 · 정합 OK·유실 0 · 장애 0건.
    ///    <b>데이터는 한 건도 안 들어오는데</b> 화면 전체가 초록이었다.
    ///
    /// 🧭 「전부 초록불 대시보드가 최약점을 감춘다」의 정확한 사례다.
    ///    안심 문구 옆에는 그것을 <b>반증할 수 있는 값</b>이 반드시 있어야 한다.
    /// </summary>
    public required bool DataFlowing { get; init; }

    /// <summary>큐에 밀려 있는 수 — <b>여기가 차면 뒤가 못 따라오고 있다는 뜻</b>이다.</summary>
    public required int Backlog { get; init; }

    public required int Workers { get; init; }
    public required int MaxWorkers { get; init; }

    /// <summary>
    /// 초당 수신 — <b>메시지</b> 단위다(센서 팜의 발신 레이트와 같은 단위).
    ///
    /// 🔑 이벤트가 아니라 메시지인 이유: 이 값은 팜 화면과 <b>나란히 비교</b>하라고 있는 것이다.
    ///    단위가 다르면 정상인데도 2배 차이가 나서 유실·중복으로 읽힌다.
    /// </summary>
    public required double ReceiveRate { get; init; }

    /// <summary>
    /// 초당 저장 — <b>이벤트</b> 단위다(한 메시지가 in·out 두 건을 낳는다).
    ///
    /// ⚠️ <see cref="ReceiveRate"/> 와 <b>단위가 다르다</b>. 나란히 놓고 빼지 말 것.
    /// </summary>
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
        SensorsSending = 0,
        DataFlowing = false,
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
