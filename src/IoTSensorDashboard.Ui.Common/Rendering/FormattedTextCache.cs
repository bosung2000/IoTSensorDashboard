using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace IoTSensorDashboard.Ui.Common.Rendering;

/// <summary>
/// 텍스트 모양(shaping) 결과를 재사용한다.
///
/// ⭐ <b>이 클래스가 없으면 화면이 굳는다.</b> 진단이 가장 어려웠던 사고의 봉합이다.
///
/// 📌 근거 — 실측 회귀:
///
///    앱 폰트가 프로젝트에 <b>임베드</b>되어 있어서,
///    <c>FormattedText</c> 를 새로 만들 때마다 WPF 가 <b>폰트 스트림을 다시 연다.</b>
///
///    패널들이 33ms 마다 수백 개를 새로 만들면 <b>UI 스레드가 포화</b>되고,
///    애니메이션 타이머가 디스패처를 독점해 데이터 틱이 <b>영원히 순번을 못 받는다.</b>
///
/// 🔴 증상이 고약한 이유:
///    백엔드(MQTT·SQLite)는 <b>다른 스레드라 멀쩡히 계속 돈다.</b>
///    그래서 <b>화면만 굳고 DB 는 계속 커진다.</b>
///    겉보기엔 「연결이 끊겼다」로 보여 <b>엉뚱한 곳(재연결)을 파게 된다.</b>
///
///    실제 진단 근거: 스택 샘플 <b>5/5 가 폰트 스트림을 여는 함수 안</b>이었다.
///
/// 🔒 <b>UI 스레드 전용</b>이다. 다른 스레드에서 부르지 말 것.
///    WPF 렌더 객체는 만든 스레드에 묶인다.
/// </summary>
public static class FormattedTextCache
{
    /// <summary>
    /// 캐시 상한. 넘으면 통째로 비운다.
    ///
    /// 🔑 정확성에는 무해하다 — 다시 만들면 그만이다.
    ///    무한히 쌓이게 두면 그게 곧 메모리 누수다.
    /// </summary>
    private const int MaxEntries = 4096;

    private static readonly Dictionary<Key, FormattedText> Cache = [];

    /// <summary>지금 캐시에 든 항목 수. 진단·검증용.</summary>
    public static int Count => Cache.Count;

    /// <summary>
    /// 같은 문자열·서체·크기·색이면 <b>같은 인스턴스</b>를 돌려준다.
    /// </summary>
    /// <param name="ppd">
    /// pixels per dip — 화면 배율. 이게 다르면 모양이 달라지므로 키에 포함한다.
    /// </param>
    /// <param name="maxWidth">0 이하면 폭 제한 없음.</param>
    /// <remarks>
    /// 🔑 <b>인스턴스 재사용이 안전한 이유</b>:
    ///    그리는 <b>위치는 DrawText 의 인자</b>다.
    ///    반환된 객체를 보관하거나 변형하지 않는 한(폭·높이 읽기는 무해)
    ///    같은 인스턴스를 여러 번 그려도 된다.
    /// </remarks>
    public static FormattedText Get(
        string text, Typeface face, double size, Brush brush, double ppd, double maxWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(brush);

        var key = new Key(text, face, size, brush, ppd, maxWidth);

        if (Cache.TryGetValue(key, out var cached)) return cached;

        if (Cache.Count >= MaxEntries) Cache.Clear();

        var created = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            face,
            size,
            brush,
            ppd);

        if (maxWidth > 0)
        {
            created.MaxTextWidth = maxWidth;

            // 🔴 <b>이 두 줄이 없으면 글자가 겹친다.</b>
            //
            // 📌 실측: 감지 피드의 메시지가 길어지자 WPF 가 폭을 넘긴 부분을
            //    **말줄임이 아니라 다음 줄로 넘겼고**, 그 두 번째 줄이 아래 줄 위에
            //    그려져 두 줄이 뭉개졌다. MaxTextWidth 는 「넘치면 접어라」는 뜻이지
            //    「넘치면 잘라라」가 아니다.
            //
            // 🔒 직접 그리는 패널은 줄 높이를 **호출부가 계산해서** 배치한다.
            //    한 줄일 것이라 가정하고 y 를 더해 가므로, 여기서 한 줄을 보장해야 한다.
            created.MaxLineCount = 1;
            created.Trimming = TextTrimming.CharacterEllipsis;
        }

        Cache[key] = created;
        return created;
    }

    /// <summary>캐시를 비운다. 테스트·진단용.</summary>
    public static void Clear() => Cache.Clear();

    /// <summary>
    /// 캐시 키.
    ///
    /// Brush 를 키에 넣는 이유: 색이 다르면 다른 결과물이다.
    /// 얼린(frozen) 브러시는 값이 안 변하므로 키로 안전하다.
    /// </summary>
    private readonly record struct Key(
        string Text, Typeface Face, double Size, Brush Brush, double Ppd, double MaxWidth);
}
