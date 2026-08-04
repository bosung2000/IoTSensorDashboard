using IoTSensorDashboard.Core.Storage;

namespace IoTSensorDashboard.Core.Reporting;

/// <summary>
/// 가동률 · 장애 건수 · MTTR.
///
/// 이 화면은 스스로를 「구매 판단 근거」라고 부른다.
/// 그런데 이 시스템에서 <b>가장 크게 거짓말한 숫자</b>가 바로 여기서 나왔다.
/// </summary>
public static class SlaCalculator
{
    /// <summary>
    /// 매장별 가동 실적.
    ///
    /// <code>가동률 = 1 − (매장 다운타임 union) / 관측창</code>
    /// </summary>
    /// <param name="observedStores">
    /// 🔴 <b>기본값을 두지 않는다.</b>
    ///
    /// 📌 근거: 기본값이 있으면 호출부가 <b>무심코 옛 동작으로 되돌아가</b>
    ///    버그가 <b>조용히 재발</b>한다. 필수 인자로 두면 <b>컴파일러가 막는다.</b>
    ///
    /// > 이건 「방어적 프로그래밍」이 아니라 <b>회귀를 구조적으로 불가능하게</b> 만드는 설계다.
    /// > 같은 판단을 다른 위험한 인자에도 적용할 것.
    /// </param>
    public static IReadOnlyList<SlaStoreStat> Compute(
        IReadOnlyList<OutageRecord> outages,
        IEnumerable<string> storeNames,
        DateTimeOffset windowStart,
        DateTimeOffset now,
        IReadOnlySet<string> observedStores)
    {
        ArgumentNullException.ThrowIfNull(outages);
        ArgumentNullException.ThrowIfNull(storeNames);
        ArgumentNullException.ThrowIfNull(observedStores);

        double windowSec = Math.Max(1, (now - windowStart).TotalSeconds);
        var result = new List<SlaStoreStat>();

        foreach (var store in storeNames)
        {
            if (!observedStores.Contains(store))
            {
                // 지켜보지 못한 매장 — 장애가 없었다고 말할 근거가 없다.
                // 🔑 목록에서 빼지 않는다. 사라지면 「그런 매장이 없구나」가 된다.
                result.Add(new SlaStoreStat(store, null, 0, 0, 0));
                continue;
            }

            var mine = outages.Where(o => o.Store == store && o.ResolvedAt > windowStart).ToList();

            var intervals = mine
                .Select(o => (
                    A: o.BornAt < windowStart ? windowStart : o.BornAt,      // 창 시작으로 자른다
                    B: o.ResolvedAt > now ? now : o.ResolvedAt))             // 현재로 자른다
                .Where(iv => iv.B > iv.A)
                .ToList();

            double down = UnionSeconds(intervals);

            // 창 밖 시간을 세면 가동률이 음수가 될 수 있다. Clamp 가 최종 방어다.
            double uptime = Math.Clamp(1 - down / windowSec, 0, 1);

            double mttr = mine.Count > 0 ? mine.Average(o => o.Duration.TotalSeconds) : 0;

            result.Add(new SlaStoreStat(store, uptime, mine.Count, down, mttr));
        }

        return result;
    }

    /// <summary>
    /// 겹치는 구간을 병합해 순(net) 다운타임을 구한다.
    ///
    /// 📌 근거: 한 매장에 센서가 여러 대인데 <b>동시에</b> 다운되면,
    ///    단순 합산 시 같은 시간을 <b>여러 번</b> 센다 → 가동률이 실제보다 훨씬 낮게 나온다.
    /// </summary>
    public static double UnionSeconds(List<(DateTimeOffset A, DateTimeOffset B)> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        if (intervals.Count == 0) return 0;

        intervals.Sort((x, y) => x.A.CompareTo(y.A));

        double total = 0;
        var curA = intervals[0].A;
        var curB = intervals[0].B;

        for (int i = 1; i < intervals.Count; i++)
        {
            if (intervals[i].A <= curB)
            {
                // 겹친다 → 병합
                if (intervals[i].B > curB) curB = intervals[i].B;
            }
            else
            {
                total += (curB - curA).TotalSeconds;
                curA = intervals[i].A;
                curB = intervals[i].B;
            }
        }

        return total + (curB - curA).TotalSeconds;
    }

    /// <summary>
    /// 전체 요약.
    ///
    /// 🔴 <b>측정 불가(null)는 평균에서 제외</b>한다 — 분모에서도 뺀다.
    ///    그리고 몇 곳이 빠졌는지를 함께 돌려준다.
    ///    그 숫자 없이 평균만 보여주면 평균이 다시 거짓말한다.
    /// </summary>
    public static SlaSummary Summarize(IReadOnlyList<SlaStoreStat> stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        var measured = stats.Where(s => s.Uptime is not null).ToList();
        int unmeasurable = stats.Count - measured.Count;

        return new SlaSummary(
            AverageUptime: measured.Count > 0 ? measured.Average(s => s.Uptime!.Value) : null,
            UnmeasurableCount: unmeasurable,
            MeasuredCount: measured.Count,
            TotalIncidents: stats.Sum(s => s.Incidents));
    }
}
