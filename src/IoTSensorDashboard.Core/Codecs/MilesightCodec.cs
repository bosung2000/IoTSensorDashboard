using System.Text.Json;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Ingestion;

namespace IoTSensorDashboard.Core.Codecs;

/// <summary>
/// Milesight — 주기별 in/out 을 별도 필드로 보낸다.
///
/// <code>
/// { "deviceId": "milesight-0002",
///   "time": "2026-07-09T09:00:00.0000000+09:00",
///   "periodIn": 5, "periodOut": 4 }
/// </code>
///
/// 🔑 FLIR 과 필드 이름·구조가 완전히 다른데도 같은 표준 CountEvent 로 접힌다.
///    이것이 "이기종 흡수"이고, 새 기종을 코덱 하나로 붙일 수 있는 이유다.
/// </summary>
public sealed class MilesightCodec : ISensorCodec
{
    public string Vendor => "milesight";

    public IReadOnlyList<CountEvent> Decode(RawPayload raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        try
        {
            using var doc = JsonDocument.Parse(raw.Body);
            var root = doc.RootElement;

            var sensorId = root.GetProperty("deviceId").GetString();
            if (string.IsNullOrWhiteSpace(sensorId)) return [];

            var ts = CodecTime.ParseIso(root.GetProperty("time").GetString());

            // 방향별로 따로 담는다 — FLIR 의 "형제 보존"과 같은 이유로,
            // 한쪽 필드가 깨져도 나머지 한쪽은 살린다.
            var events = new List<CountEvent>(2);
            AddIfPresent(events, root, "periodIn", "in", sensorId, ts);
            AddIfPresent(events, root, "periodOut", "out", sensorId, ts);
            return events;
        }
        catch (Exception)
        {
            // 🔒 코덱은 절대 throw 하지 않는다(ISensorCodec 계약).
            return [];
        }
    }

    private static void AddIfPresent(
        List<CountEvent> events, JsonElement root, string property, string direction,
        string sensorId, DateTimeOffset ts)
    {
        try
        {
            if (!root.TryGetProperty(property, out var value)) return;

            events.Add(new CountEvent
            {
                SensorId = sensorId,
                OccurredAt = ts,
                Count = value.GetInt32(),
                Direction = direction
            });
        }
        catch (Exception)
        {
            // 이 방향만 건너뛴다.
        }
    }
}
