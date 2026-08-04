using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Ui.Common.Rendering;

/// <summary>
/// 커스텀 렌더 패널의 공용 그리기 도구.
///
/// 🔑 <b>왜 컨트롤 트리 대신 직접 그리는가</b>:
///    상황판은 패널 10장이 33ms 마다 갱신된다. 이걸 <c>TextBlock</c>·<c>Rectangle</c> 트리로 만들면
///    <b>비주얼 오브젝트가 수천 개</b>가 되고, WPF 는 매 프레임 그 트리 전체의
///    측정·배치(measure/arrange)를 돈다. 직접 그리면 오브젝트는 <b>패널당 하나</b>다.
///
/// 🔒 <b>브러시·펜은 전부 얼려서(frozen) 재사용한다</b> — <see cref="HudPalette"/>.
///    프레임당 할당 0 이 목표다. 여기서 새로 만들면 그 취지가 무너진다.
///
/// 🔴 <b>텍스트는 반드시 <see cref="FormattedTextCache"/> 를 거친다.</b>
///    매 프레임 <c>FormattedText</c> 를 새로 만들면 UI 스레드가 포화되고
///    데이터 틱이 순번을 못 받아 <b>화면이 굳는다</b>(그 클래스 주석의 사고).
/// </summary>
public static class HudDraw
{
    // ── 서체 ─────────────────────────────────────────────────────────────
    //
    // 🔑 시스템 폰트를 쓴다(임베디드 아님).
    //    임베디드 폰트는 FormattedText 를 만들 때마다 pack:// 스트림을 다시 열어
    //    고빈도 렌더에서 특히 비싸다. 캐시가 있어도 굳이 그 위험을 살 이유가 없다.

    private static readonly FontFamily Family = new("Segoe UI");

    private static readonly Typeface Regular = new(Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface Semi = new(Family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Heavy = new(Family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>글자 굵기.</summary>
    public enum Weight
    {
        Normal,
        Semi,
        Heavy
    }

    /// <summary>가로 정렬 기준.</summary>
    public enum Align
    {
        Left,
        Center,
        Right
    }

    private static Typeface FaceOf(Weight w) => w switch
    {
        Weight.Heavy => Heavy,
        Weight.Semi => Semi,
        _ => Regular
    };

    /// <summary>
    /// 텍스트 한 줄. <paramref name="y"/> 는 <b>글자 상단</b> 기준이다(베이스라인 아님).
    /// </summary>
    /// <param name="ppd">
    /// pixels per dip — 화면 배율. 배율이 다르면 렌더 결과가 달라지므로 캐시 키에 들어간다.
    /// 호출부는 <see cref="VisualTreeHelper.GetDpi(Visual)"/> 의 <c>PixelsPerDip</c> 을 넘긴다.
    /// </param>
    /// <param name="maxWidth">넘으면 말줄임. 0 이하면 제한 없음.</param>
    /// <returns>그린 텍스트. 폭·높이를 읽어 다음 요소를 배치할 때 쓴다.</returns>
    public static FormattedText Text(
        DrawingContext dc, string s, double x, double y, double size, Brush brush, double ppd,
        Weight w = Weight.Normal, Align align = Align.Left, double maxWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(dc);

        // 호출부가 「칸 너비 − 여백」으로 maxWidth 를 계산하면 좁은 창에서 음수가 나온다.
        // 음수를 그대로 넘기면 WPF 가 던지므로 여기서 흡수한다(그리기는 계속돼야 한다).
        var ft = FormattedTextCache.Get(s, FaceOf(w), size, brush, ppd, maxWidth > 0 ? Math.Max(1, maxWidth) : 0);

        double dx = align switch
        {
            Align.Right => -ft.WidthIncludingTrailingWhitespace,
            Align.Center => -ft.WidthIncludingTrailingWhitespace / 2,
            _ => 0
        };

        dc.DrawText(ft, new Point(x + dx, y));
        return ft;
    }

    /// <summary>
    /// 폭에 맞을 때까지 <b>글자를 줄여서</b> 그린다 — 말줄임 대신.
    ///
    /// 🔴 <b>숫자는 잘리면 뜻이 달라진다.</b> <c>55,643</c> 과 <c>55,6…</c> 는
    ///    다른 값이 아니라 <b>읽을 수 없는 값</b>이다. 관제 화면에서 그건 없느니만 못하다.
    ///
    /// 📌 하한에서도 안 맞으면 하한 크기로 그린다 — 무한정 줄여 못 읽게 만드는 것보다
    ///    살짝 넘치더라도 <b>읽히는 편</b>이 낫다.
    /// </summary>
    public static void TextFit(
        DrawingContext dc, string s, double x, double y, double size, double minSize, double maxWidth,
        Brush brush, double ppd, Weight w = Weight.Normal, Align align = Align.Left)
    {
        ArgumentNullException.ThrowIfNull(dc);

        var face = FaceOf(w);
        double f = size;

        while (f > minSize)
        {
            var probe = FormattedTextCache.Get(s, face, f, brush, ppd, 0);
            if (probe.WidthIncludingTrailingWhitespace <= maxWidth) break;
            f -= 0.5;
        }

        // maxWidth 를 넘기지 않는다 = 말줄임이 걸리지 않는다.
        Text(dc, s, x, y, Math.Max(minSize, f), brush, ppd, w, align);
    }

    /// <summary>
    /// 패널 틀(배경 + 테두리 + 제목)을 그리고 <b>내용 영역</b>을 돌려준다.
    ///
    /// 🔑 제목 왼쪽의 액센트 막대는 장식이 아니라 <b>패널의 시작을 눈에 박는 표시</b>다.
    ///    패널이 10장 붙어 있으면 경계선만으로는 어디서 끊기는지 눈이 못 따라간다.
    /// </summary>
    /// <param name="chrome">
    /// <c>false</c> 면 배경·테두리를 그리지 않고 제목만 — 다른 카드 <b>안에</b> 구획으로 넣을 때.
    /// 카드 안에 카드를 겹쳐 그리면 테두리가 두 겹이 되어 지저분해진다.
    /// </param>
    public static Rect Frame(
        DrawingContext dc, double w, double h, string title, double ppd, bool chrome = true, Brush? accent = null)
    {
        ArgumentNullException.ThrowIfNull(dc);

        if (chrome)
        {
            dc.DrawRoundedRectangle(HudPalette.Panel, HudPalette.Base, new Rect(0, 0, w, h), 8, 8);

            // 상단 유리 하이라이트 — 위 2% 만. 이게 없으면 패널이 「종이」처럼 납작해 보인다.
            dc.DrawRectangle(HudPalette.Sheen, null, new Rect(1, 1, Math.Max(0, w - 2), Math.Min(2, h)));
        }

        double titleX = 13;

        if (accent is not null)
        {
            dc.DrawRectangle(accent, null, new Rect(12, 11, 3, 12));
            titleX = 22;
        }

        Text(dc, title, titleX, 8, 12.5, HudPalette.Foreground, ppd, Weight.Semi,
            maxWidth: Math.Max(0, w - titleX - 12));

        return new Rect(12, 34, Math.Max(0, w - 24), Math.Max(0, h - 44));
    }

    /// <summary>
    /// 가로 막대 — 값에 비례한 길이. <paramref name="ratio"/> 는 0~1 로 클램프한다.
    ///
    /// 🔑 <b>클램프가 안전장치다.</b> 분모가 0 이거나 순간 최댓값을 넘는 값이 들어오면
    ///    막대가 패널 밖으로 튀어 다른 패널을 침범한다(커스텀 렌더는 클리핑이 자동이 아니다).
    /// </summary>
    public static void Bar(DrawingContext dc, Rect track, double ratio, Brush fill, double radius = 3)
    {
        ArgumentNullException.ThrowIfNull(dc);

        double r = double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 1) : 0;
        double width = track.Width * r;

        if (width <= 0) return;

        dc.DrawRoundedRectangle(fill, null, new Rect(track.X, track.Y, width, track.Height), radius, radius);
    }
}
