using System.Text.Json;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Ingestion;

namespace IoTSensorDashboard.Core.Codecs;

/// <summary>
/// FLIR — 라인별 카운트를 배열로 보낸다.
///
/// <code>
/// { "sensorId": "flir-0001",
///   "timestamp": "2026-07-09T09:00:00.0000000+09:00",
///   "lines": [ { "direction": "in", "count": 3 }, { "direction": "out", "count": 2 } ] }
/// </code>
///
/// → CountEvent 는 라인 수만큼 나온다.
/// </summary>
public sealed class FlirCodec : ISensorCodec
{
    public string Vendor => "flir";

    public IReadOnlyList<CountEvent> Decode(RawPayload raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        try
        {
            using var doc = JsonDocument.Parse(raw.Body);
            var root = doc.RootElement;

            // 센서를 모르는 이벤트는 저장할 곳이 없다 — 라인이 아무리 멀쩡해도 버린다.
            var sensorId = root.GetProperty("sensorId").GetString();
            if (string.IsNullOrWhiteSpace(sensorId)) return [];

            var ts = CodecTime.ParseIso(root.GetProperty("timestamp").GetString());

            var events = new List<CountEvent>();
            foreach (var line in root.GetProperty("lines").EnumerateArray())
            {
                try
                {
                    events.Add(new CountEvent
                    {
                        SensorId = sensorId,
                        OccurredAt = ts,
                        Count = line.GetProperty("count").GetInt32(),
                        Direction = line.TryGetProperty("direction", out var d) ? d.GetString() : null
                    });
                }
                catch (Exception)
                {
                    // 이 라인만 건너뛴다 — 정상 형제 라인은 보존한다.
                    // in/out 중 out 만 깨졌다고 in 까지 버리면, 데이터가 있는데 안 세게 된다.
                }
            }

            return events;
        }
        catch (Exception)
        {
            // 🔒 코덱은 절대 throw 하지 않는다(ISensorCodec 계약).
            //    외부 데이터 불신: 형식 오류·누락 필드 → 빈 리스트.
            //    이건 관대함이 아니라, 한 건의 망가진 payload 가 수집 루프 전체를 멈추지 못하게 하는 장치다.
            return [];
        }
    }
}
