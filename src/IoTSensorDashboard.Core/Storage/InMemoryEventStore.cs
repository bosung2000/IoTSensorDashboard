using IoTSensorDashboard.Core.Domain;

namespace IoTSensorDashboard.Core.Storage;

/// <summary>
/// 인메모리 저장소 — 헤드리스 검증과 대시보드의 읽기 모델용.
///
/// 왜 삽입 순서를 따로 보존하나: Snapshot() 이 "들어온 순서"를 돌려줘야
/// 최초본 권위(I2)와 집계 재현성을 눈으로 확인할 수 있다.
/// Dictionary 의 열거 순서는 보장되지 않으므로 List 를 함께 둔다.
///
/// 스레드 안전: 수집 워커가 동시에 쓴다. 모든 공개 표면이 같은 락 안에서 돈다.
/// </summary>
public sealed class InMemoryEventStore : IEventStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CountEvent> _byKey = new(StringComparer.Ordinal);
    private readonly List<CountEvent> _ordered = [];

    public bool TryAppend(CountEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);

        lock (_gate)
        {
            // 🔑 "먼저 조회하고 없으면 넣기"를 락 밖에서 하면 동시성에서 깨진다.
            //    영속 구현이 INSERT OR IGNORE 로 DB 에 원자성을 위임하는 것과 같은 이유다.
            if (!_byKey.TryAdd(e.DedupKey, e)) return false;

            _ordered.Add(e);
            return true;
        }
    }

    public IReadOnlyList<bool> TryAppendBatch(IReadOnlyList<CountEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return [];

        var results = new bool[events.Count];
        lock (_gate)
        {
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (_byKey.TryAdd(e.DedupKey, e))
                {
                    _ordered.Add(e);
                    results[i] = true;
                }
            }
        }
        return results;
    }

    public IReadOnlyList<CountEvent> Snapshot()
    {
        lock (_gate) return _ordered.ToArray();
    }

    public long Count
    {
        get { lock (_gate) return _ordered.Count; }
    }

    public bool Contains(string dedupKey)
    {
        ArgumentNullException.ThrowIfNull(dedupKey);
        lock (_gate) return _byKey.ContainsKey(dedupKey);
    }
}
