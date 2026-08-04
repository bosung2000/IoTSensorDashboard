namespace IoTSensorDashboard.Core.Storage;

/// <summary>
/// 보존·정리 정책. 판정이므로 Core 가 소유한다(구현이 아니라 규칙이다).
///
/// 📌 이 수치들은 전부 실측에서 나왔다. "기본값이 더 자연스럽다"로 되돌리면 그 사고가 재현된다.
/// </summary>
public static class RetentionPolicy
{
    /// <summary>
    /// 원본 이벤트를 그대로 두는 기간. 이보다 오래된 것은 시간별 집계로 승격하고 원본은 지운다.
    ///
    /// 📌 근거: 보존·롤업 없이 7시간 돌렸더니 DB 가 778MB 로 불어나 시스템이 스스로 느려졌다.
    ///    "데이터를 다 보관한다"가 미덕이 아니라 자가 다운의 원인이었다.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(3);

    /// <summary>유지보수 루프 주기.</summary>
    public static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 한 번에 롤업·삭제할 최대 행 수.
    ///
    /// 📌 근거: 예전에는 조건에 맞는 모든 행을 한 트랜잭션으로 처리했다.
    ///    저부하에서는 순식간이지만 폭주(5,000/s)에서는 수십만 행이 되고,
    ///    그동안 저장소 락을 쥐고 있어 수집 워커가 통째로 멈췄다 — 백로그 8만, 화면 수십 초 정지.
    ///    "정리가 수집을 죽이는" 형태라 정리 쪽이 양보해야 한다.
    ///    조각 사이에 락이 풀리므로 워커가 끼어들 틈이 생긴다.
    /// </summary>
    public const int PruneChunkRows = 20_000;

    /// <summary>한 유지보수 주기가 쓸 수 있는 시간. 넘으면 다음 주기로 미룬다.</summary>
    public static readonly TimeSpan MaintenanceBudget = TimeSpan.FromSeconds(2);

    /// <summary>백로그가 이보다 크면 이번 주기는 최소한만 한다 — 수집이 우선이다.</summary>
    public const int MaintenanceBusyBacklog = 2_000;

    /// <summary>매 주기 조금씩 OS 에 돌려줄 페이지 수.</summary>
    public const int ReclaimIncrementalPages = 2_000;

    /// <summary>전체 재작성(VACUUM)을 허용하는 낭비율.</summary>
    public const double ReclaimFullWasteRatio = 0.35;

    /// <summary>전체 재작성을 허용하는 최소 빈 공간(128MB).</summary>
    public const long ReclaimFullMinFreeBytes = 128L * 1024 * 1024;

    /// <summary>지금 시각 기준으로 "이보다 오래된 것"의 경계.</summary>
    public static DateTimeOffset CutoffFor(DateTimeOffset now) => now - Retention;

    /// <summary>
    /// 전체 재작성(VACUUM)을 할 때인가.
    ///
    /// 🔒 아무 때나 부르면 안 된다. VACUUM 은 파일 전체를 다시 쓰므로
    ///    그동안 저장소가 멈춘다(실측 2,882MB → 321MB 에 4.5초).
    ///
    /// 두 조건을 모두 만족할 때만 한다:
    ///   ① 낭비율이 높다        — 조금 비어 있는 정도로는 재작성 값어치가 없다
    ///   ② 절대량도 충분히 크다  — 작은 파일에서 35% 는 몇 MB 에 불과하다
    /// </summary>
    public static bool ShouldReclaimFull(StorageStats stats) =>
        stats.WasteRatio >= ReclaimFullWasteRatio &&
        stats.FreeBytes >= ReclaimFullMinFreeBytes;
}
