using System.Reflection;
using IoTSensorDashboard.Core.Domain;
using IoTSensorDashboard.Core.Ingestion;
using IoTSensorDashboard.Core.Storage;
using Xunit;

namespace IoTSensorDashboard.Tests.InvariantGates;

/// <summary>
/// G-I2 · append-only (계약 수준)
///
/// Given  이벤트가 저장된 상태
/// When   다른 payload(다른 Count)로 같은 정체성을 재전송
/// Then   최초본이 그대로 남는다
///
/// 추가: IEventStore 계약에 mutation 표면이 존재하지 않는다(리플렉션 검사).
///
/// 보존창·롤업은 영속 저장소(2층)에서 완성되지만, 계약이 수정을 허용하지 않는다는 사실은
/// 지금 못박아야 한다 — 나중에 "편의상" 메서드가 하나 생기면 그때는 이미 늦다.
/// </summary>
public sealed class G_I2_AppendOnlyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 9, 0, 0, TimeSpan.Zero);

    /// <summary>계약에 있으면 안 되는 이름들. 부분 일치로 본다.</summary>
    private static readonly string[] ForbiddenSurfaces =
    [
        "Update", "Delete", "Remove", "Clear", "Replace", "Truncate", "Set"
    ];

    [Fact]
    public void 저장_계약에_수정_삭제_표면이_없다()
    {
        // 🔒 이건 "하지 말자"는 약속이 아니라 구조적으로 못 하게 만든 것이다.
        //    표면이 없으면 소비 코드가 append-only 를 어길 방법 자체가 없다.
        var methodNames = typeof(IEventStore)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();

        var violations = methodNames
            .Where(name => ForbiddenSurfaces.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(violations.Count == 0,
            $"IEventStore 에 수정·삭제 표면이 생겼다: {string.Join(", ", violations)}");
    }

    [Fact]
    public void 다른_값으로_재전송해도_최초본이_남는다()
    {
        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store);

        var first = new CountEvent { SensorId = "flir-0001", OccurredAt = T0, Count = 3, Direction = "in" };
        var resent = first with { Count = 99 };

        pipeline.Ingest(first);
        pipeline.Ingest(resent);

        var stored = Assert.Single(store.Snapshot());
        Assert.Equal(3, stored.Count);
    }

    [Fact]
    public void 거부된_이벤트는_저장되지_않는다()
    {
        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store);

        pipeline.Ingest(null);
        pipeline.Ingest(new CountEvent { SensorId = "  ", OccurredAt = T0, Count = 1 });
        pipeline.Ingest(new CountEvent { SensorId = "flir-0001", OccurredAt = T0, Count = -5 });
        pipeline.Ingest(new CountEvent { SensorId = "flir-0001", OccurredAt = T0, Count = 500 });

        Assert.Equal(0L, store.Count);
    }

    [Fact]
    public void 스냅샷은_삽입_순서를_보존한다()
    {
        // 순서가 흔들리면 "최초본이 권위"라는 말을 눈으로 확인할 수 없다.
        var store = new InMemoryEventStore();
        var pipeline = new IngestionPipeline(store);

        for (int i = 0; i < 20; i++)
            pipeline.Ingest(new CountEvent
            {
                SensorId = "flir-0001",
                OccurredAt = T0.AddSeconds(i),
                Count = i,
                Direction = "in"
            });

        var counts = store.Snapshot().Select(e => e.Count).ToArray();
        Assert.Equal(Enumerable.Range(0, 20).ToArray(), counts);
    }

    [Fact]
    public void 도메인_타입은_불변이다()
    {
        // 📌 이벤트가 나중에 수정될 수 있으면 "어느 스레드가 언제 바꿨는지"에 따라 저장 결과가 달라지고,
        //    그러면 I1 을 증명할 방법이 없다.
        var settable = typeof(CountEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { } s && s.IsPublic && !IsInitOnly(s))
            .Select(p => p.Name)
            .ToList();

        Assert.True(settable.Count == 0,
            $"CountEvent 에 변경 가능한 속성이 있다: {string.Join(", ", settable)}");
    }

    /// <summary>init 접근자는 반환 타입에 IsExternalInit 수정자가 붙는다.</summary>
    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");
}
