using IoTSensorDashboard.Core.Domain;

namespace IoTSensorDashboard.Core.Ingestion;

/// <summary>
/// 벤더 → 코덱 라우팅.
///
/// 새 센서 기종 추가 = 코덱 구현 1클래스 + 등록 1줄. 다른 파일 수정 0.
/// </summary>
public sealed class CodecRegistry
{
    // 대소문자 무관: 토픽이 "FLIR" 로 와도 같은 코덱을 찾아야 한다.
    private readonly Dictionary<string, ISensorCodec> _byVendor = new(StringComparer.OrdinalIgnoreCase);

    public CodecRegistry(params ISensorCodec[] codecs)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        foreach (var c in codecs) Register(c);
    }

    public void Register(ISensorCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (string.IsNullOrWhiteSpace(codec.Vendor))
            throw new ArgumentException("코덱의 Vendor 가 비어 있으면 라우팅 키가 없어 영원히 선택되지 않는다.", nameof(codec));

        _byVendor[codec.Vendor] = codec;
    }

    public IReadOnlyCollection<string> Vendors => _byVendor.Keys;

    /// <summary>
    /// 원본을 표준 이벤트로.
    ///
    /// 미지 벤더는 빈 리스트다 — 예외를 던지지 않는다.
    /// 밖에서 온 토픽 하나가 수집 루프를 멈추게 할 수는 없기 때문이다.
    /// (반대로 발행 측은 우리가 만든 코드이므로 오타가 즉시 터져야 한다 — VendorPayloadFactory 참조.)
    /// </summary>
    public IReadOnlyList<CountEvent> Decode(RawPayload raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (raw.Vendor is null || !_byVendor.TryGetValue(raw.Vendor, out var codec))
            return [];

        return codec.Decode(raw);
    }
}
