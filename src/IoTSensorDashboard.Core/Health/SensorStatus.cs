namespace IoTSensorDashboard.Core.Health;

/// <summary>
/// 센서의 생사.
///
/// 🔑 <see cref="Offline"/> 과 <see cref="Unknown"/> 을 합치지 말 것.
///
/// | 상태     | 뜻                        | 누가 처리하나            |
/// |----------|---------------------------|--------------------------|
/// | Offline  | 보다가 끊긴 것             | 장애 조치 — 알림·에스컬레이션 |
/// | Unknown  | 한 번도 신호가 없었던 것    | 설치·배선·프로비저닝 문제  |
///
/// 원인이 완전히 다르므로 감지 피드에서도 섞지 않는다.
/// 「끊김」 목록에 미설치 센서가 섞이면 담당자가 헛걸음한다.
/// </summary>
public enum SensorStatus
{
    /// <summary>최근 수신 있음 = 살아 있다.</summary>
    Online,

    /// <summary>임계 시간 넘게 미수신 = 「0」이 아니라 **「모름」**.</summary>
    Offline,

    /// <summary>한 번도 수신된 적 없음 = 미지.</summary>
    Unknown
}
