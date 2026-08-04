namespace IoTSensorDashboard.Core.Health;

/// <summary>
/// 「얼마나 조용하면 죽은 것으로 볼 것인가」.
///
/// 🔴 임계는 고정값이 아니라 <b>센서가 관측시켜 준 발신 주기에서 나온다.</b>
///
/// 📌 근거 — 고정 임계가 무너진 실측:
///    임계가 12초 고정이었다.
///    사람 한 명마다 쏘는 센서라면 12초 침묵은 이상하지만,
///    <b>1분마다 묶어 보내는 센서</b>에게 12초 침묵은 완전히 정상이다.
///
///    배치 모드로 바꾸자 정상 센서가 계속 오프라인으로 잡혔고,
///    핑 대상이 <b>5대에서 322~742대로 폭증</b>했다.
///
///    「데이터가 곧 생존 증거」라는 설계가, 데이터가 드물어지는 순간
///    <b>상시 최대 비용</b>으로 뒤집힌 것이다.
///
/// > 침묵의 뜻은 센서마다 다르고, 임계는 관측된 발신 주기에서 나와야 한다.
/// </summary>
/// <param name="Floor">아무리 짧아도 이보다 빨리 죽었다고 하지 않는다.</param>
/// <param name="Multiplier">관측된 주기의 몇 배까지 기다릴 것인가.</param>
/// <param name="Ceiling">아무리 길어도 이보다 오래 기다리지 않는다.</param>
public readonly record struct HealthPolicy(TimeSpan Floor, double Multiplier, TimeSpan Ceiling)
{
    /// <summary>오프라인 판정 — 넉넉하게.</summary>
    public static readonly HealthPolicy Offline =
        new(TimeSpan.FromSeconds(12), 2.5, TimeSpan.FromMinutes(3));

    /// <summary>
    /// 능동 핑 대상 선정 — 더 짧게.
    ///
    /// 🔑 Probe 가 Offline 보다 짧은 이유: <b>오프라인으로 판정하기 전에 먼저 물어보려고.</b>
    /// </summary>
    public static readonly HealthPolicy Probe =
        new(TimeSpan.FromSeconds(6), 1.5, TimeSpan.FromMinutes(2));

    /// <summary>
    /// 이 센서에게 허용할 침묵 시간.
    /// </summary>
    /// <param name="cadence">
    /// 관측된 발신 주기. 아직 모르면 null → <see cref="Floor"/> 를 쓴다.
    /// </param>
    public TimeSpan For(TimeSpan? cadence)
    {
        if (cadence is not { } c || c <= TimeSpan.Zero) return Floor;

        var scaled = TimeSpan.FromSeconds(c.TotalSeconds * Multiplier);

        if (scaled < Floor) return Floor;
        if (scaled > Ceiling) return Ceiling;
        return scaled;
    }
}
