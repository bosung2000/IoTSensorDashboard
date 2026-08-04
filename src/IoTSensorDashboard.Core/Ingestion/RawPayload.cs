namespace IoTSensorDashboard.Core.Ingestion;

/// <summary>아직 해석되지 않은 외부 데이터. 신뢰 경계 밖에서 온 것이므로 형식도 값도 다 의심한다.</summary>
public sealed record RawPayload
{
    /// <summary>라우팅 힌트(코덱 선택 키): "flir" / "milesight". 채널이 토픽에서 채운다.</summary>
    public required string Vendor { get; init; }

    /// <summary>원문 본문(JSON 텍스트).</summary>
    public required string Body { get; init; }

    /// <summary>출처 표기(진단용): "mqtt:sensors/milesight/store-01/ms-0002".</summary>
    public string? Source { get; init; }

    /// <summary>
    /// 이 원본이 호스트에 도착한 순간. 헬스 판정의 기준 시각.
    ///
    /// ⚠️ "도착 시각"이지 "처리 시각"이 아니다.
    ///
    /// 왜 이걸 틀리면 위험한가 — 부하가 큰 순간에만 조용히 틀린다:
    ///   큐가 깊어지면 메시지가 수십 초 대기한다. 처리(dequeue) 시각을 헬스 기준으로 쓰면
    ///   그게 처리되는 순간 "방금 신호 받음"으로 찍혀 이미 죽은 센서가 살아 있는 것으로 보인다.
    ///   그러면 오프라인 감지가 임계(12초)가 아니라 (백로그 배수 지연 + 12초)로 조용히 늘어난다 —
    ///   하필 부하가 큰 구간에서. 관제가 가장 필요한 순간에 관제가 거짓말을 한다.
    ///
    /// 구현 규약: 수집 채널이 메시지를 받아 큐에 넣는 그 지점에서 찍는다.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; init; }
}
