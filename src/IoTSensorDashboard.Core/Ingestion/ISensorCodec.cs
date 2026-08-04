using IoTSensorDashboard.Core.Domain;

namespace IoTSensorDashboard.Core.Ingestion;

/// <summary>
/// 센서 기종 플러그인 — 벤더 페이로드를 표준 CountEvent 로 바꾼다.
///
/// 코덱은 "파싱만" 한다. 멱등·저장·집계 책임을 여기 주면
/// 새 센서 기종을 추가할 때마다 불변식이 깨질 기회가 생긴다.
/// 바이트를 CountEvent 로 바꾸기만 하고, 나머지는 전부 Core 가 한다.
/// </summary>
public interface ISensorCodec
{
    /// <summary>라우팅 키. CodecRegistry 가 이 값으로 코덱을 고른다(대소문자 무관).</summary>
    string Vendor { get; }

    /// <summary>
    /// 🔒 절대 throw 하지 않는다. 파싱에 실패하면 빈 리스트를 반환한다.
    ///
    /// 왜: 밖에서 온 한 건의 망가진 payload 가 수집 루프 전체를 멈추면 안 된다.
    ///     이건 "관대함"이 아니라 한 건의 악성 payload 가 수집 전체를 멈추지 못하게 하는 가용성 장치다.
    /// </summary>
    IReadOnlyList<CountEvent> Decode(RawPayload raw);
}
