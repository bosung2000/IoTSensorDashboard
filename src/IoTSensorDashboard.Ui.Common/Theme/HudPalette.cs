using System.Windows.Media;

namespace IoTSensorDashboard.Ui.Common.Theme;

/// <summary>
/// HUD 의 색과 선.
///
/// 🔑 <b>선 위계가 4단계인 것이 핵심이다.</b>
///
/// 📌 화면이 밋밋해 보이는 원인은 대개 색이 아니라 <b>「선이 한 단계뿐」</b>이라는 데 있다.
///    패널 경계·구분선·눈금이 전부 같은 밝기면, 눈이
///    <b>「무엇이 큰 덩어리이고 무엇이 그 안의 칸인지」</b>를 형태로 알 수 없어
///    화면 전체가 <b>한 겹</b>으로 읽힌다.
///
///    정밀해 보이는 화면은 예외 없이 선을 3~4단계로 나눠 쓴다.
///
/// 🔒 모든 브러시·펜은 <see cref="Freezable.Freeze"/> 한다 —
///    고빈도 렌더 경로에서 매 프레임 새로 만들면 할당이 쌓인다.
/// </summary>
public static class HudPalette
{
    // ── 표면 ─────────────────────────────────────────────────────────────

    /// <summary>가장 아래 바닥.</summary>
    public static readonly Brush Void = FrozenBrush(0xFF, 0x08, 0x0A, 0x0D);

    /// <summary>
    /// 패널 몸통 — 바닥보다 <b>약간만</b> 밝게.
    ///
    /// 🔑 크게 벌리면 카드가 「떠 보이는」 게 아니라 <b>「붕 뜬다」</b>.
    /// </summary>
    public static readonly Brush Panel = FrozenBrush(0xFF, 0x12, 0x15, 0x1B);

    /// <summary>패널 상단 유리 하이라이트 (위 2%만).</summary>
    public static readonly Brush Sheen = FrozenBrush(0x08, 0xFF, 0xFF, 0xFF);

    // ── 글자 ─────────────────────────────────────────────────────────────

    public static readonly Brush Foreground = FrozenBrush(0xFF, 0xDC, 0xDF, 0xE6);
    public static readonly Brush TextMuted = FrozenBrush(0xFF, 0x8A, 0x93, 0xA2);
    public static readonly Brush TextDim = FrozenBrush(0xFF, 0x5A, 0x63, 0x72);

    /// <summary>🔑 <b>드물게</b> 써야 강조가 된다.</summary>
    public static readonly Brush Accent = FrozenBrush(0xFF, 0x35, 0xC7, 0xFF);

    // ── 의미색 ───────────────────────────────────────────────────────────

    /// <summary>정상·증가.</summary>
    public static readonly Brush Up = FrozenBrush(0xFF, 0x08, 0x99, 0x81);

    /// <summary>오류·감소.</summary>
    public static readonly Brush Down = FrozenBrush(0xFF, 0xF2, 0x36, 0x45);

    /// <summary>경고.</summary>
    public static readonly Brush Warn = FrozenBrush(0xFF, 0xFF, 0xC1, 0x07);

    /// <summary>유입 (청록).</summary>
    public static readonly Brush In = FrozenBrush(0xFF, 0x5E, 0xEA, 0xD4);

    /// <summary>유출 (라벤더).</summary>
    public static readonly Brush Out = FrozenBrush(0xFF, 0xC9, 0xB3, 0xFF);

    /// <summary>미확인·오프라인 — <b>0 이 아니라 「모름」의 색</b>.</summary>
    public static readonly Brush Unknown = FrozenBrush(0xFF, 0x9A, 0xA2, 0xAE);

    // ── 🔑 선 위계 4단계 ─────────────────────────────────────────────────

    /// <summary>배경 격자 — <b>있는 줄 모르지만 없으면 바닥이 빈다.</b></summary>
    public static readonly Pen Faint = FrozenPen(0x0A, 0xFF, 0xFF, 0xFF, 1);

    /// <summary>패널 <b>안쪽 칸</b> 나눔.</summary>
    public static readonly Pen Soft = FrozenPen(0xFF, 0x2A, 0x30, 0x3A, 1);

    /// <summary><b>패널 경계</b>.</summary>
    public static readonly Pen Base = FrozenPen(0xFF, 0x33, 0x3B, 0x47, 1);

    /// <summary>코너 브래킷·활성 강조 — <b>시선을 붙잡는 유일한 단계</b>. 두께가 1.2 다.</summary>
    public static readonly Pen Strong = FrozenPen(0xFF, 0x55, 0x62, 0x74, 1.2);

    // 🔒 이 4단계는 **용도**로 나눈 것이지 취향이 아니다. 임의로 합치지 말 것.

    /// <summary>얼린 단색 브러시.</summary>
    public static Brush FrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>얼린 펜.</summary>
    public static Pen FrozenPen(byte a, byte r, byte g, byte b, double thickness)
    {
        var pen = new Pen(FrozenBrush(a, r, g, b), thickness);
        pen.Freeze();
        return pen;
    }
}
