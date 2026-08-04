using IoTSensorDashboard.Core.Authorization;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Provisioning;
using Xunit;

namespace IoTSensorDashboard.Tests.InvariantGates;

/// <summary>
/// G-I4 · 권한 경계는 절대 새지 않는다
///
/// Given  조직 트리와 역할 배정
/// When   매장 역할로 타 매장 데이터 요청
/// Then   0건 반환 (예외가 아니라 빈 결과)
///
/// 🔴 스코프 누수는 <b>한 번 나면 되돌릴 수 없다.</b>
///    타 지점 데이터가 노출된 사실은 지워지지 않는다.
/// </summary>
public sealed class G_I4_ScopeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    private static (ScopePolicy Policy, ScopedView View, SiteProvisioning Prov) NewScope()
    {
        var prov = new SiteProvisioning(sensorCount: 24);
        var tree = new SiteTree(prov.Sites);
        var policy = new ScopePolicy(tree);
        return (policy, new ScopedView(policy, prov.Sensors), prov);
    }

    private static CountEvent Event(string sensorId) =>
        new() { SensorId = sensorId, OccurredAt = T0, Count = 1, Direction = "in" };

    // ── 기본 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 매장_역할로_타_매장을_요청하면_0건이다()
    {
        var (policy, view, prov) = NewScope();

        var myStore = prov.StoreIds[0];
        var otherStore = prov.StoreIds[1];

        Assert.False(policy.CanAccess(Role.Store, myStore, otherStore));

        // 타 매장 센서의 이벤트는 한 건도 안 나온다.
        var otherSensors = prov.Sensors.Where(s => s.SiteId == otherStore).Select(s => s.Id);
        var events = otherSensors.Select(Event).ToList();

        Assert.Empty(view.Filter(events, Role.Store, myStore));
    }

    [Fact]
    public void 권한_밖_요청은_예외가_아니라_빈_결과다()
    {
        // 📌 예외를 던지면 **「그 지점이 존재한다」는 사실**이 새어 나간다.
        //    존재 여부 자체가 정보인 경우가 있다. 빈 결과가 안전하다.
        var (policy, view, prov) = NewScope();

        var result = view.Filter([Event("flir-0000")], Role.Store, prov.StoreIds[5]);

        Assert.Empty(result);   // 던지지 않는다
        Assert.False(policy.CanAccess(Role.Store, prov.StoreIds[5], prov.StoreIds[0]));
    }

    [Fact]
    public void 전체_관리자는_전부_본다()
    {
        var (policy, view, prov) = NewScope();

        var visible = policy.VisibleSites(Role.TotalAdmin, null);

        Assert.Equal(prov.Sites.Count, visible.Count);
        Assert.Equal(prov.Sensors.Count, view.Filter(prov.Sensors.Select(s => Event(s.Id)).ToList(),
                                                     Role.TotalAdmin, null).Count);
    }

    [Fact]
    public void 본점은_자기_서브트리_전부를_본다()
    {
        var (policy, _, _) = NewScope();

        var visible = policy.VisibleSites(Role.HeadOffice, "g1");

        Assert.Contains("g1", visible);
        Assert.Contains("g1-s0", visible);
        Assert.Contains("g1-s5", visible);
        Assert.DoesNotContain("g2", visible);
        Assert.DoesNotContain("g2-s0", visible);
    }

    // ── 🔴 fail-closed ───────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("존재하지-않는-지점")]
    public void 배정이_없거나_모르는_지점이면_아무것도_안_보인다(string? assigned)
    {
        // 🔴 fail-closed — 「전부 보임」이 아니라 「아무것도 안 보임」이다.
        //    권한 판정에서 모르는 것은 거부한다.
        //
        //    이걸 반대로 만들면(모르면 전부 보임) 설정 실수 하나가 전면 노출이 된다.
        var (policy, view, prov) = NewScope();

        foreach (var role in new[] { Role.HeadOffice, Role.Group, Role.Store })
        {
            Assert.Empty(policy.VisibleSites(role, assigned));
            Assert.Empty(view.Filter(prov.Sensors.Select(s => Event(s.Id)).ToList(), role, assigned));
        }
    }

    // ── 🔴 권한 상승 차단 ────────────────────────────────────────────────

    [Fact]
    public void 매장_역할이_상위_노드에_배정돼도_자기_노드만_본다()
    {
        // 🔴 이게 Store 만 Subtree 를 안 쓰는 이유다.
        //
        // 📌 매장 역할 계정이 실수로 본부 노드에 배정되면,
        //    Subtree 를 쓸 경우 그 매장 직원이 **본부 산하 전 매장**을 보게 된다.
        //
        //    역할 등급과 배정 지점의 정합은 프로비저닝의 책임이지만,
        //    **Core 는 그걸 믿지 않는다.** 믿지 않는 쪽이 안전하다.
        var (policy, _, _) = NewScope();

        var visible = policy.VisibleSites(Role.Store, "g1");   // 매장 역할인데 본부에 배정됨

        Assert.Single(visible);
        Assert.Contains("g1", visible);
        Assert.DoesNotContain("g1-s0", visible);   // 산하 매장은 안 보인다
    }

    [Fact]
    public void 매장_역할이_루트에_배정돼도_전체를_보지_않는다()
    {
        var (policy, _, prov) = NewScope();

        var visible = policy.VisibleSites(Role.Store, SiteProvisioning.HeadquartersId);

        Assert.Single(visible);
        Assert.True(visible.Count < prov.Sites.Count);
    }

    // ── 경계 무누출 ──────────────────────────────────────────────────────

    [Fact]
    public void 모든_역할_지점_조합에서_경계_밖_센서가_한_건도_안_섞인다()
    {
        // 조합을 전부 돌려 본다 — 한 조합만 새도 그건 누수다.
        var (policy, view, prov) = NewScope();

        var allEvents = prov.Sensors.Select(s => Event(s.Id)).ToList();
        var sensorSite = prov.Sensors.ToDictionary(s => s.Id, s => s.SiteId, StringComparer.Ordinal);

        foreach (var role in new[] { Role.TotalAdmin, Role.HeadOffice, Role.Group, Role.Store })
        {
            foreach (var siteId in prov.Sites.Select(s => s.Id))
            {
                var visibleSites = policy.VisibleSites(role, siteId);
                var filtered = view.Filter(allEvents, role, siteId);

                Assert.All(filtered, e =>
                    Assert.Contains(sensorSite[e.SensorId], visibleSites));
            }
        }
    }

    [Fact]
    public void 매핑_없는_센서는_필터에서_제외된다()
    {
        // 🔒 어느 지점 소속인지 모르는 센서는 경계 밖으로 취급한다.
        //    「모르니까 일단 보여주자」가 아니라 「모르니까 안 보여준다」이다.
        var (_, view, _) = NewScope();

        var unknown = view.Filter([Event("axis-9999")], Role.TotalAdmin, null);

        Assert.Empty(unknown);
        Assert.Null(view.SiteOf("axis-9999"));
    }

    // ── 트리 방어 ────────────────────────────────────────────────────────

    [Fact]
    public void 순환_데이터에서도_무한_루프에_빠지지_않는다()
    {
        // 데이터가 잘못돼 A → B → A 가 되면 순진한 구현은 영원히 돈다.
        var cyclic = new List<Site>
        {
            new() { Id = "a", ParentId = "b", Name = "A" },
            new() { Id = "b", ParentId = "a", Name = "B" },
        };

        var tree = new SiteTree(cyclic);
        var subtree = tree.Subtree("a");

        Assert.Equal(2, subtree.Count);
    }

    [Fact]
    public void 부모가_없는_지점도_트리에_들어간다()
    {
        // 프로비저닝이 깨져 고아 노드가 생겨도 트리 생성이 실패하면 안 된다 —
        // 그러면 앱이 아예 안 뜬다.
        var orphan = new List<Site>
        {
            new() { Id = "root", ParentId = null, Name = "루트" },
            new() { Id = "lost", ParentId = "없는-부모", Name = "고아" },
        };

        var tree = new SiteTree(orphan);

        Assert.Contains("lost", tree.AllIds);
        Assert.Single(tree.Subtree("root"));       // 고아는 루트 밑에 안 붙는다
        Assert.Single(tree.Subtree("lost"));
    }

    [Fact]
    public void 빈_트리에서도_안전하다()
    {
        var tree = new SiteTree([]);
        var policy = new ScopePolicy(tree);

        Assert.Empty(tree.AllIds);
        Assert.Empty(policy.VisibleSites(Role.TotalAdmin, null));
        Assert.Empty(policy.VisibleSites(Role.Store, "무엇이든"));
    }
}
