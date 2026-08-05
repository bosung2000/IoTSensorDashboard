using System.Text.RegularExpressions;
using Xunit;

namespace IoTSensorDashboard.Tests.Guards;

/// <summary>
/// 「전체 스캔을 화면 경로에서 부르지 않는다」를 <b>소스에서</b> 검사한다.
///
/// 🔴 <b>왜 이런 형태의 테스트가 필요한가</b>: 이 결함은 실행해도 잘 안 드러난다.
///    DB 가 작으면 전체 스캔이 빨라 아무 일도 안 일어나고,
///    <b>데이터가 쌓이고 · 창이 앞에 있고 · 부하가 클 때</b>만 터진다.
///    즉 「빌드 그린 · 짧은 테스트 그린」인데 데모에서 죽는 부류다.
///
/// 📌 실측(5,000/s · 900만 행): 관제실 창을 앞에 두는 것만으로
///    백로그가 <b>1,036 → 7,148</b> 로 뛰었다. <see cref="System.Object"/> 가 아니라
///    저장 워커와 같은 락을 쥔 채 500만 행을 세고 있었기 때문이다.
///
/// 🔑 규약은 원래 코드에 적혀 있었다 — 「기동 시 1회 읽고 이후는 메모리 카운터로 증분하는 것이
///    <b>호출 측 책임</b>」. 사람이 지키기로 한 규약은 언젠가 깨지므로 여기서 기계가 지킨다.
///
/// ⚠️ 테스트 프로젝트는 net8.0 이라 net8.0-windows 인 WPF 프로젝트를 참조할 수 없다.
///    그래서 타입이 아니라 <b>소스 텍스트</b>를 본다. 거친 방법이지만,
///    이 규약은 「어디서 부르는가」의 문제라 텍스트로도 충분히 지킬 수 있다.
/// </summary>
public sealed class FullScanGuardTests
{
    /// <summary>
    /// <c>_store.Count</c>(전체 스캔) 호출은 <b>기동 경로 한 곳</b>뿐이어야 한다.
    /// </summary>
    [Fact]
    public void 저장소_전체스캔은_기동시_한_번만_부른다()
    {
        var source = ReadSource("IoTSensorDashboard.ControlRoom", "ServerHost.cs");

        // 주석 안의 언급(설명·경고)은 제외하고 실제 호출만 센다.
        var code = StripComments(source);
        int calls = Regex.Matches(code, @"_store\s*\.\s*Count\b").Count;

        Assert.True(calls == 1,
            $"_store.Count 호출이 {calls}곳이다. 전체 스캔이므로 기동 시 1회여야 한다. " +
            "화면이 쓰는 값은 TotalStored(기준점 + 메모리 카운터)로 답해야 한다.");
    }

    /// <summary>
    /// 기동 시의 그 1회도 <b>UI 스레드를 막지 않아야</b> 한다.
    /// 900MB DB 에서는 수백 ms 이상 걸려 창이 그동안 안 뜬다.
    /// </summary>
    [Fact]
    public void 기동시_전체스캔은_백그라운드로_뺀다()
    {
        var code = StripComments(ReadSource("IoTSensorDashboard.ControlRoom", "ServerHost.cs"));

        Assert.Matches(@"Task\.Run\s*\(\s*\(\s*\)\s*=>\s*_store\s*\.\s*Count", code);
    }

    /// <summary>주석·문서 주석을 지운다 — 설명문 속 단어를 호출로 오해하지 않게.</summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*///.*$", "", RegexOptions.Multiline);
        source = Regex.Replace(source, @"//.*$", "", RegexOptions.Multiline);

        return source;
    }

    /// <summary>
    /// 소스 파일을 찾는다. 테스트 실행 위치(bin/...)에서 위로 올라가며
    /// 솔루션 파일이 있는 곳을 저장소 루트로 본다.
    /// </summary>
    private static string ReadSource(string projectName, string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IoTSensorDashboard.sln")))
            dir = dir.Parent;

        Assert.True(dir is not null, "저장소 루트를 찾지 못했다 (IoTSensorDashboard.sln 기준).");

        var path = Path.Combine(dir!.FullName, "src", projectName, fileName);
        Assert.True(File.Exists(path), $"소스를 찾지 못했다: {path}");

        return File.ReadAllText(path);
    }
}
