namespace IoTSensorDashboard.Core.Ingestion;

/// <summary>파이프라인의 판정 결과 4종.</summary>
public enum IngestResult
{
    /// <summary>새 이벤트로 저장됨.</summary>
    Appended,

    /// <summary>이미 저장된 정체성 — 무시됨(멱등, I1).</summary>
    Duplicate,

    /// <summary>경계 검증 실패(형식 오류·null·음수·오버플로 위험).</summary>
    Rejected,

    /// <summary>형식은 맞지만 물리적으로 불가능한 값 — 격리(I7).</summary>
    Implausible
}

/// <summary>
/// 수집 요약.
///
/// 🔒 Rejected 와 Implausible 을 합치지 말 것.
///    전자는 "데이터가 망가졌다", 후자는 "데이터는 멀쩡한데 현실적으로 불가능하다"이다.
///    원인이 다르므로 대응도 다르다(전자=발신 측 버그, 후자=센서 글리치). 화면에도 각각 표시한다.
///
/// readonly record struct 인 이유: 초당 수만 건이 지나가는 경로라 힙 할당을 피한다.
/// </summary>
public readonly record struct IngestSummary(int Appended, int Duplicate, int Rejected, int Implausible = 0)
{
    public int Received => Appended + Duplicate + Rejected + Implausible;
}
