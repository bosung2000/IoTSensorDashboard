using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.Storage;

/// <summary>
/// 공간 회수와 그 판정.
///
/// 📌 왜 필요한가: SQLite 는 DELETE 해도 파일을 줄이지 않고 free list 에 넣어 재사용만 한다.
///    보존창이 <b>정상 동작하는데도</b> 파일이 2,882MB 였고 그중 88% 가 빈 페이지였다.
///    "지우고 있는데 파일이 안 줄어드는" 상태라 원인을 찾기 어렵다.
/// </summary>
public sealed class ReclaimAndPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    private static CountEvent Event(int i) =>
        new() { SensorId = $"flir-{i % 20:D4}", OccurredAt = T0.AddSeconds(i), Count = i % 50, Direction = "in" };

    // ── 저장소 상태 보고 ─────────────────────────────────────────────────

    [Fact]
    public void 저장소가_자기_상태를_보고한다()
    {
        // 🔑 화면에 "회수 가능 N MB" 를 띄우려면 저장소가 자기 상태를 말할 수 있어야 한다.
        //    파일이 커지는데 이유를 말 못 하면 사용자는 "고장났나" 하고 앱을 끈다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        for (int i = 0; i < 200; i++) store.TryAppend(Event(i));

        var stats = store.Stats();

        Assert.True(stats.FileBytes > 0);
        Assert.Equal(stats.FileBytes, stats.UsedBytes + stats.FreeBytes);
        Assert.InRange(stats.WasteRatio, 0, 1);
        Assert.Equal(2, stats.AutoVacuumMode);   // INCREMENTAL
    }

    [Fact]
    public void 빈_저장소의_낭비율은_0으로_계산된다()
    {
        // 0 으로 나누는 자리다. NaN 이 화면에 뜨면 그것도 "모르는 것을 아는 척"이다.
        Assert.Equal(0, new StorageStats(0, 0, 0, 2).WasteRatio);
    }

    // ── 회수 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 증분_회수를_해도_롤업이_보존된다()
    {
        // 🔴 공간을 줄이려다 데이터를 잃으면 아무 의미가 없다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        for (int i = 0; i < 300; i++) store.TryAppend(Event(i));
        store.RollupAndPrune(T0.AddHours(1), RetentionPolicy.PruneChunkRows);

        var before = store.Count;
        var sumBefore = store.SumBySensor(DateTimeOffset.MinValue).Sum(b => b.Sum);

        store.ReclaimIncremental(RetentionPolicy.ReclaimIncrementalPages);

        Assert.Equal(before, store.Count);
        Assert.Equal(sumBefore, store.SumBySensor(DateTimeOffset.MinValue).Sum(b => b.Sum));
    }

    [Fact]
    public void 전체_회수를_해도_데이터와_설정이_보존된다()
    {
        // ⚠️ VACUUM 은 auto_vacuum 설정을 파일에 다시 굽는다.
        //    실행 전에 PRAGMA 를 다시 걸지 않으면 설정이 풀리고,
        //    그때부터는 지워도 파일이 안 줄어드는 상태로 되돌아간다.
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        for (int i = 0; i < 300; i++) store.TryAppend(Event(i));
        store.RollupAndPrune(T0.AddHours(1), RetentionPolicy.PruneChunkRows);

        var before = store.Count;

        store.ReclaimFull();

        Assert.Equal(before, store.Count);
        Assert.Equal(2, store.Stats().AutoVacuumMode);   // 설정이 풀리지 않았다
    }

    [Fact]
    public void 체크포인트는_양쪽_모드_모두_동작한다()
    {
        using var temp = new TempDb();
        using var store = temp.OpenStore();

        for (int i = 0; i < 100; i++) store.TryAppend(Event(i));

        store.Checkpoint(truncate: false);   // 바쁠 때
        store.Checkpoint(truncate: true);    // 한가할 때

        Assert.Equal(100L, store.Count);
    }

    // ── 판정 (순수 함수 — Core 소유) ─────────────────────────────────────

    [Fact]
    public void 전체_회수는_두_조건을_모두_만족할_때만_한다()
    {
        // 🔒 VACUUM 은 파일 전체를 다시 쓴다. 실측 2,882MB → 321MB 에 4.5초가 걸렸고
        //    그동안 저장소가 멈춘다. 아무 때나 부르면 안 된다.
        const long MB = 1024 * 1024;

        // 낭비율 높음 + 절대량 충분 → 한다
        Assert.True(RetentionPolicy.ShouldReclaimFull(
            new StorageStats(FileBytes: 1000 * MB, UsedBytes: 500 * MB, FreeBytes: 500 * MB, 2)));

        // 낭비율은 높은데 절대량이 작다 → 안 한다
        //   작은 파일에서 50% 는 몇 MB 에 불과하다. 4.5초를 멈출 값어치가 없다.
        Assert.False(RetentionPolicy.ShouldReclaimFull(
            new StorageStats(FileBytes: 10 * MB, UsedBytes: 5 * MB, FreeBytes: 5 * MB, 2)));

        // 절대량은 큰데 낭비율이 낮다 → 안 한다
        //   대부분이 실제 데이터라 다시 써 봐야 별로 안 줄어든다.
        Assert.False(RetentionPolicy.ShouldReclaimFull(
            new StorageStats(FileBytes: 10_000 * MB, UsedBytes: 9_800 * MB, FreeBytes: 200 * MB, 2)));
    }

    [Fact]
    public void 보존창_경계는_지금_시각에서_뒤로_센다()
    {
        var now = new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 7, 9, 9, 0, 0, TimeSpan.Zero),
                     RetentionPolicy.CutoffFor(now));
    }

    [Fact]
    public void 보존_정책_수치가_명세와_일치한다()
    {
        // 이 수치들은 전부 실측에서 나왔다. 바뀌면 그 사고가 재현된다.
        // 테스트로 잠가 두면 "왜 이 값인지" 모르는 사람이 무심코 바꿀 수 없다.
        Assert.Equal(TimeSpan.FromHours(3), RetentionPolicy.Retention);
        Assert.Equal(TimeSpan.FromMinutes(3), RetentionPolicy.MaintenanceInterval);
        Assert.Equal(20_000, RetentionPolicy.PruneChunkRows);
        Assert.Equal(TimeSpan.FromSeconds(2), RetentionPolicy.MaintenanceBudget);
        Assert.Equal(2_000, RetentionPolicy.MaintenanceBusyBacklog);
        Assert.Equal(2_000, RetentionPolicy.ReclaimIncrementalPages);
        Assert.Equal(0.35, RetentionPolicy.ReclaimFullWasteRatio);
        Assert.Equal(128L * 1024 * 1024, RetentionPolicy.ReclaimFullMinFreeBytes);
    }
}
