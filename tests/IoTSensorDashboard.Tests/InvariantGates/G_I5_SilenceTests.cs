using IoTSensorDashboard.Core.Health;
using IoTSensorDashboard.Core.Provisioning;
using Xunit;

namespace IoTSensorDashboard.Tests.InvariantGates;

/// <summary>
/// G-I5 · 침묵은 0이 아니라 「모름」
///
/// Given  프로비저닝 명부가 등록된 상태
/// When   센서가 임계 시간 넘게 무응답
/// Then   상태 = Offline · 집계에 「0」으로 안 섞임
///
/// 🔴 이 불변식에서만 사각지대가 <b>세 번</b> 나왔고, 세 번 다 <b>분모</b> 문제였다.
/// </summary>
public sealed class G_I5_SilenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    // ── 기본 판정 ────────────────────────────────────────────────────────

    [Fact]
    public void 임계를_넘게_조용하면_오프라인이다()
    {
        var tracker = new SensorHealthTracker();
        tracker.Observe("flir-0001", T0);

        Assert.Equal(SensorStatus.Online,
            tracker.Status("flir-0001", T0.AddSeconds(5), HealthPolicy.Offline));
        Assert.Equal(SensorStatus.Offline,
            tracker.Status("flir-0001", T0.AddSeconds(30), HealthPolicy.Offline));
    }

    [Fact]
    public void 한_번도_안_본_센서는_오프라인이_아니라_미확인이다()
    {
        // 🔑 Offline 과 Unknown 을 합치지 말 것.
        //    Offline 은 「보다가 끊긴 것」이라 장애 조치 대상이고,
        //    Unknown 은 「한 번도 신호가 없던 것」이라 설치·배선 문제다.
        //    「끊김」 목록에 미설치 센서가 섞이면 담당자가 헛걸음한다.
        var tracker = new SensorHealthTracker();
        tracker.Expect(["flir-0001"]);

        Assert.Equal(SensorStatus.Unknown,
            tracker.Status("flir-0001", T0, HealthPolicy.Offline));
    }

    [Fact]
    public void 임계와_정확히_같은_시각은_온라인이다()
    {
        // 📌 '<=' 인지 '<' 인지가 여기서 갈린다.
        //    경계에서 깜빡이면 장애 알림이 의미를 잃는다.
        var tracker = new SensorHealthTracker();
        tracker.Observe("flir-0001", T0);

        var exactly = T0 + HealthPolicy.Offline.Floor;

        Assert.Equal(SensorStatus.Online, tracker.Status("flir-0001", exactly, HealthPolicy.Offline));
        Assert.Equal(SensorStatus.Offline,
            tracker.Status("flir-0001", exactly.AddMilliseconds(1), HealthPolicy.Offline));
    }

    // ── 🔴 분모 — 세 번 틀린 자리 ────────────────────────────────────────

    [Fact]
    public void 한_번도_안_본_센서가_분모에_남는다()
    {
        // 🔴 이게 이 파일에서 가장 중요한 테스트다.
        //
        // 📌 실제로 무슨 일이 있었나:
        //    분모가 「관측된 센서」라 처음부터 죽어 있던 센서가 통째로 빠졌다.
        //    1,000대 중 50대가 무응답인데 화면은 **950 / 950 = 100%** 를 띄웠다.
        //
        //    이 부류가 가장 찾기 어렵다 — 처음부터 없는 것은 영원히 안 보이기 때문이다.
        //    화면에 아무 이상 징후가 없고, 숫자는 오히려 완벽해 보인다.
        var tracker = new SensorHealthTracker();

        var all = Enumerable.Range(0, 1_000).Select(SiteProvisioning.SensorIdFor).ToList();
        tracker.Expect(all);

        // 950대만 신호를 보냈다. 50대는 처음부터 죽어 있다.
        foreach (var id in all.Take(950)) tracker.Observe(id, T0);

        var (online, offline, total) = tracker.Summary(T0, HealthPolicy.Offline);

        Assert.Equal(950, online);
        Assert.Equal(50, offline);
        Assert.Equal(1_000, total);     // ✅ 분모는 「있어야 할 수」다
    }

    [Fact]
    public void 명부를_등록하지_않으면_분모가_관측_기반이_된다()
    {
        // 이 테스트는 "명부 등록을 빼먹으면 어떻게 되는가"를 명시적으로 보여 준다.
        // 950/950 = 100% 라는 옛 버그의 모습 그대로다.
        var tracker = new SensorHealthTracker();

        foreach (var id in Enumerable.Range(0, 950).Select(SiteProvisioning.SensorIdFor))
            tracker.Observe(id, T0);

        var (online, offline, total) = tracker.Summary(T0, HealthPolicy.Offline);

        Assert.Equal(950, online);
        Assert.Equal(0, offline);
        Assert.Equal(950, total);       // ⚠️ 완벽해 보이지만 50대가 사라졌다

        // 🧭 그래서 호출부는 반드시 Expect 를 부른다.
        //    비율을 그리는 코드를 보면 분모의 출처를 물을 것.
    }

    [Fact]
    public void 죽은_센서도_분모에_남는다()
    {
        // 보다가 끊긴 센서가 분모에서 빠지면 가동률이 실제보다 좋아 보인다.
        var tracker = new SensorHealthTracker();
        tracker.Expect(["a", "b", "c", "d"]);

        tracker.Observe("a", T0);
        tracker.Observe("b", T0);
        tracker.Observe("c", T0.AddMinutes(-10));   // 오래전에 끊김

        var (online, offline, total) = tracker.Summary(T0, HealthPolicy.Offline);

        Assert.Equal(2, online);        // a, b
        Assert.Equal(2, offline);       // c(끊김) + d(미확인)
        Assert.Equal(4, total);
    }

    [Fact]
    public void 명부에_없던_센서가_나타나면_분모가_늘어난다()
    {
        // 분모 = 명부 ∪ 관측. 명부에 없어도 실제로 신호가 오면 세어야 한다.
        var tracker = new SensorHealthTracker();
        tracker.Expect(["a", "b"]);
        tracker.Observe("c", T0);       // 명부에 없는 센서

        Assert.Equal(3, tracker.Summary(T0, HealthPolicy.Offline).Total);
    }

    [Fact]
    public void 미확인_센서를_끊김과_분리해_보여줄_수_있다()
    {
        var tracker = new SensorHealthTracker();
        tracker.Expect(["a", "b", "c"]);
        tracker.Observe("a", T0);

        var neverSeen = tracker.NeverSeenIds();

        Assert.Equal(2, neverSeen.Count);
        Assert.Contains("b", neverSeen);
        Assert.Contains("c", neverSeen);
    }

    // ── 도착 시각 기준 ───────────────────────────────────────────────────

    [Fact]
    public void 큐_대기_메시지가_죽은_센서를_되살리지_않는다()
    {
        // 🔴 헬스는 **도착 시각**으로 판정한다. 처리 시각이 아니다.
        //
        // 📌 큐가 깊어지면 메시지가 수십 초 대기한다.
        //    처리 시각으로 관측하면 그게 처리되는 순간 「방금 신호 받음」으로 찍혀
        //    이미 죽은 센서가 살아 있는 것으로 보인다.
        //
        //    그러면 오프라인 감지가 임계(12초)가 아니라 (백로그 지연 + 12초)로 조용히 늘어난다 —
        //    하필 부하가 큰 구간에서. **관제가 가장 필요한 순간에 관제가 거짓말을 한다.**
        var tracker = new SensorHealthTracker();

        var arrivedAt = T0;                    // 메시지가 도착한 시각
        var processedAt = T0.AddMinutes(2);    // 큐에 밀려 2분 뒤에 처리됨

        tracker.Observe("flir-0001", arrivedAt);   // ✅ 도착 시각으로 관측

        Assert.Equal(SensorStatus.Offline,
            tracker.Status("flir-0001", processedAt, HealthPolicy.Offline));
    }

    [Fact]
    public void 백필이_마지막_수신_시각을_뒤로_밀지_않는다()
    {
        // 🔒 복구된 센서가 과거 데이터를 몰아 보낼 때,
        //    그 과거 시각으로 마지막 수신을 갱신하면
        //    **살아 있는 센서가 죽은 것처럼** 보인다.
        var tracker = new SensorHealthTracker();

        tracker.Observe("flir-0001", T0.AddSeconds(100));   // 최신
        tracker.Observe("flir-0001", T0);                   // 백필(과거)

        Assert.Equal(T0.AddSeconds(100), tracker.LastSeen("flir-0001"));
    }

    [Fact]
    public void 기기의_미래_시각에_오염되지_않는다()
    {
        // 📌 헬스는 호스트 도착 시각 기준이다.
        //    기기 시계가 틀려 미래 시각이 오면 `now - last` 가 계속 음수라
        //    그 센서는 **영원히 온라인**으로 오염된다.
        //
        //    이 테스트는 "호스트 시각만 넣는다"는 계약을 문서화한다 —
        //    수집 층이 RawPayload.ReceivedAt(호스트 시각)을 넘기는 것이 그 이행이다.
        var tracker = new SensorHealthTracker();

        var hostArrival = T0;
        tracker.Observe("flir-0001", hostArrival);

        Assert.Equal(SensorStatus.Offline,
            tracker.Status("flir-0001", T0.AddMinutes(5), HealthPolicy.Offline));
    }

    // ── 핑 대상 선정 ─────────────────────────────────────────────────────

    [Fact]
    public void 한_번도_안_본_센서도_핑_대상에_포함된다()
    {
        // 📌 이걸 빼면 생기는 일(테스트가 출하 전에 잡은 회귀):
        //    기동 직후처럼 아직 아무 신호도 없으면 목록이 비고,
        //    「물어볼 대상 0」으로 읽혀 **아무에게도 묻지 않게 된다.**
        //    → 센서가 영원히 미확인으로 남는다.
        var tracker = new SensorHealthTracker();
        tracker.Expect(["a", "b", "c"]);

        var targets = tracker.ProbeTargets(T0, HealthPolicy.Probe);

        Assert.Equal(3, targets.Count);
    }

    [Fact]
    public void 데이터가_도는_동안_핑_대상은_비어_간다()
    {
        // 🔑 「데이터가 곧 생존 증거」 — 부하가 클 때 핑 트래픽이 0 에 수렴하는 성질.
        //    1,000대에게 2.5초마다 물으면 그 트래픽이 실제 데이터를 압도한다.
        var tracker = new SensorHealthTracker();
        tracker.Expect(["a", "b", "c"]);

        foreach (var id in new[] { "a", "b", "c" }) tracker.Observe(id, T0);

        Assert.Empty(tracker.ProbeTargets(T0.AddSeconds(1), HealthPolicy.Probe));
    }

    [Fact]
    public void 핑_임계가_오프라인_임계보다_먼저_걸린다()
    {
        // 🔑 오프라인으로 판정하기 **전에** 먼저 물어봐야 한다.
        var tracker = new SensorHealthTracker();
        tracker.Expect(["a"]);
        tracker.Observe("a", T0);

        var between = T0.AddSeconds(8);   // Probe Floor(6초) 초과, Offline Floor(12초) 미만

        Assert.Contains("a", tracker.ProbeTargets(between, HealthPolicy.Probe));
        Assert.Equal(SensorStatus.Online, tracker.Status("a", between, HealthPolicy.Offline));
    }

    // ── 커버리지 ─────────────────────────────────────────────────────────

    [Fact]
    public void 명부의_모든_매장에_센서가_있다()
    {
        // 센서 0대인 매장이 생기면 그 매장은 영원히 「측정 불가」인데,
        // 그 사실이 화면에 드러나지 않으면 아무도 모른다.
        var provisioning = new SiteProvisioning();

        Assert.All(provisioning.StoreIds,
            id => Assert.True(provisioning.SensorCountOf(id) > 0));
    }

    [Fact]
    public void 명부_전체를_등록하면_분모가_1000이다()
    {
        var provisioning = new SiteProvisioning();
        var tracker = new SensorHealthTracker();

        tracker.Expect(provisioning.SensorIds);

        Assert.Equal(1_000, tracker.ExpectedCount);
        Assert.Equal(1_000, tracker.Summary(T0, HealthPolicy.Offline).Total);
    }
}
