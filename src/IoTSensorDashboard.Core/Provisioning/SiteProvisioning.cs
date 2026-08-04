using System.Globalization;
using IoTSensorDashboard.Core.Domain;

namespace IoTSensorDashboard.Core.Provisioning;

/// <summary>
/// 조직도와 센서 명부의 <b>단일 진실원</b>.
///
/// 🔑 이것이 I5 의 「분모」다.
///    가동률·온라인율의 분모는 여기서 나온 <b>「있어야 할 센서 수」</b>이지
///    「지금까지 본 센서 수」가 아니다.
///
/// 📌 근거 — 분모를 관측 기반으로 뒀다가 난 사고:
///    1,000대 중 50대가 무응답인데 화면은 950/950 = 가동률 100% 를 띄웠다.
///    처음부터 죽어 있던 센서는 분모에서 통째로 빠졌기 때문이다.
///    이 부류가 가장 찾기 어렵다 — <b>처음부터 없는 것은 영원히 안 보인다.</b>
/// </summary>
public sealed class SiteProvisioning
{
    public const int DefaultSensorCount = 1_000;

    public const string HeadquartersId = "hq";
    public const string HeadquartersName = "본사";

    /// <summary>본부 2개 — 수도권 · 영남.</summary>
    private static readonly (string Id, string Name)[] GroupDefs =
    [
        ("g1", "수도권본부"),
        ("g2", "영남본부"),
    ];

    /// <summary>
    /// 매장 12개. 순서가 곧 라운드로빈 순서이므로 <b>바꾸면 센서 배정이 통째로 달라진다.</b>
    /// </summary>
    private static readonly (string GroupId, string Suffix, string Name)[] StoreDefs =
    [
        ("g1", "s0", "강남점"),
        ("g1", "s1", "잠실점"),
        ("g1", "s2", "여의도점"),
        ("g1", "s3", "판교점"),
        ("g1", "s4", "수원점"),
        ("g1", "s5", "홍대점"),
        ("g2", "s0", "해운대점"),
        ("g2", "s1", "서면점"),
        ("g2", "s2", "울산점"),
        ("g2", "s3", "포항점"),
        ("g2", "s4", "대구점"),
        ("g2", "s5", "창원점"),
    ];

    private readonly Dictionary<string, string> _siteOfSensor = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nameOfSite = new(StringComparer.Ordinal);

    public SiteProvisioning(int sensorCount = DefaultSensorCount)
    {
        if (sensorCount < 0) throw new ArgumentOutOfRangeException(nameof(sensorCount));

        var sites = new List<Site> { new() { Id = HeadquartersId, ParentId = null, Name = HeadquartersName } };
        _nameOfSite[HeadquartersId] = HeadquartersName;

        foreach (var (id, name) in GroupDefs)
        {
            sites.Add(new Site { Id = id, ParentId = HeadquartersId, Name = name });
            _nameOfSite[id] = name;
        }

        var storeIds = new List<string>(StoreDefs.Length);
        foreach (var (groupId, suffix, name) in StoreDefs)
        {
            var id = $"{groupId}-{suffix}";
            sites.Add(new Site { Id = id, ParentId = groupId, Name = name });
            _nameOfSite[id] = name;
            storeIds.Add(id);
        }

        Sites = sites;
        StoreIds = storeIds;
        StoreNames = StoreDefs.Select(s => s.Name).ToArray();

        var sensors = new List<Sensor>(sensorCount);
        for (int i = 0; i < sensorCount; i++)
        {
            var id = SensorIdFor(i);
            var siteId = storeIds[i % storeIds.Count];   // 라운드로빈

            sensors.Add(new Sensor { Id = id, SiteId = siteId, Vendor = VendorFor(i) });
            _siteOfSensor[id] = siteId;
        }

        Sensors = sensors;
        SensorIds = sensors.Select(s => s.Id).ToArray();
    }

    /// <summary>본사 1 + 본부 2 + 매장 12 = 15개 노드.</summary>
    public IReadOnlyList<Site> Sites { get; }

    /// <summary>있어야 할 센서 전부. <b>이것이 분모다.</b></summary>
    public IReadOnlyList<Sensor> Sensors { get; }

    public IReadOnlyList<string> SensorIds { get; }

    public IReadOnlyList<string> StoreIds { get; }

    public IReadOnlyList<string> StoreNames { get; }

    /// <summary>
    /// 센서 ID 규약 — <c>{vendor}-{i:D4}</c>.
    ///
    /// 🔑 <b>결정적(deterministic)</b>이어야 한다. 같은 입력이면 항상 같은 ID 가 나와야
    ///    재시작 후에도 같은 센서로 인식된다.
    ///    난수나 시각이 섞이면 재시작할 때마다 "새 센서 1,000대"가 나타나고
    ///    옛 센서 1,000대는 영원히 오프라인으로 남는다.
    /// </summary>
    public static string SensorIdFor(int index) =>
        $"{VendorFor(index)}-{index.ToString("D4", CultureInfo.InvariantCulture)}";

    /// <summary>짝/홀 교대 — 짝수는 flir, 홀수는 milesight.</summary>
    public static string VendorFor(int index) => index % 2 == 0 ? "flir" : "milesight";

    /// <summary>
    /// 이 센서가 어느 지점 소속인가. 모르면 null.
    ///
    /// 🔑 대시보드가 스트림에서 사이트를 못 받았을 때 <b>폴백</b>으로 쓴다.
    ///    없는 정보를 지어내는 게 아니라 이미 우리가 아는 사실을 쓰는 것이므로 「발명」이 아니다.
    ///
    /// 📌 이 폴백이 없어서 난 사고: 사이트를 못 받은 센서를 건너뛰었더니
    ///    센서가 전부 죽은 매장이 목록에서 <b>통째로 사라졌다.</b>
    ///    0 으로 표시되는 것보다 나쁘다 — 0 은 "손님이 없었구나"지만
    ///    사라지면 "그런 매장이 없구나"가 된다.
    /// </summary>
    public string? SiteOf(string sensorId) =>
        sensorId is not null && _siteOfSensor.TryGetValue(sensorId, out var siteId) ? siteId : null;

    /// <summary>지점 ID → 표시 이름. 모르는 지점이면 ID 를 그대로 돌려준다.</summary>
    public string SiteName(string siteId) =>
        siteId is not null && _nameOfSite.TryGetValue(siteId, out var name) ? name : siteId ?? "";

    /// <summary>이 매장에 배정된 센서 수.</summary>
    public int SensorCountOf(string siteId) =>
        _siteOfSensor.Count(kv => kv.Value == siteId);
}
