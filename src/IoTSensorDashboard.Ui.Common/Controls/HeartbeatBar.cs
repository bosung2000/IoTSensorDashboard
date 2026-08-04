using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using IoTSensorDashboard.Ui.Common.Rendering;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.Ui.Common.Controls;

/// <summary>
/// 활력 표시등 — 「화면엔 안 보여도 <b>반드시 돌아야 하는 것</b>」이 살아 있는지.
///
/// 🔒 <b>장식이 아니다.</b>
///
/// 📌 근거 — 실제 사건: 유지보수 루프가 <b>예외를 통째로 삼켜</b>,
///    계속 실패해도 <b>아무도 몰랐다.</b> DB 는 조용히 커지고 있었다.
///
/// 등록할 것: 수집 루프 · 유지보수 루프 · 헬스 핑처럼
/// <b>멈춰도 화면에 아무 변화가 없는</b> 백그라운드 작업들.
///
/// > 🧭 <b>`catch` 로 삼킨 주기 작업에는 반드시 활력 점을</b> —
/// > 안 붙이면 영원히 실패해도 아무도 모른다.
/// </summary>
public sealed class HeartbeatBar : FrameworkElement
{
    private const double DotRadius = 3.5;
    private const double ItemSpacing = 18;
    private const double LabelGap = 6;
    private const double FontSize = 10.5;

    private static readonly Typeface Face = new("Segoe UI");

    private readonly List<Vital> _vitals = [];
    private readonly DispatcherTimer _timer;

    public HeartbeatBar()
    {
        ClipToBounds = true;
        Height = 18;

        // ⚠️ 우선순위를 <b>명시</b>한다. 기본값에 맡기지 않는다.
        //
        // 📌 애니메이션 타이머가 Render 우선순위로 디스패처를 독점하면,
        //    기본 우선순위인 데이터·활력 틱이 **영원히 순번을 못 받는다.**
        //    그러면 이 표시등 자체가 굳어서 "다 살아 있다"고 거짓말한다.
        _timer = new DispatcherTimer(DispatcherPriority.DataBind)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => InvalidateVisual();

        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// 감시 대상을 등록한다.
    /// </summary>
    /// <param name="key">Beat 할 때 쓸 식별자.</param>
    /// <param name="label">화면에 보일 짧은 이름.</param>
    /// <param name="tooltip">마우스를 올렸을 때 설명 — <b>무엇이 죽으면 어떻게 되는지</b>를 적는다.</param>
    /// <param name="timeoutMs">이 시간 안에 Beat 가 없으면 색이 바뀐다.</param>
    public void AddVital(string key, string label, string tooltip, int timeoutMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (timeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs),
                "제한 시간이 0 이하면 항상 죽은 것으로 보여 경고가 의미를 잃는다.");

        _vitals.Add(new Vital(key, label, tooltip, TimeSpan.FromMilliseconds(timeoutMs)));
        InvalidateVisual();
    }

    /// <summary>루프가 한 바퀴 돌 때마다 부른다.</summary>
    public void Beat(string key)
    {
        foreach (var vital in _vitals)
        {
            if (!string.Equals(vital.Key, key, StringComparison.Ordinal)) continue;

            vital.LastBeat = DateTimeOffset.UtcNow;
            return;
        }
    }

    /// <summary>지금 살아 있지 않은 항목들. 진단·테스트용.</summary>
    public IReadOnlyList<string> StaleKeys(DateTimeOffset now) =>
        _vitals.Where(v => v.IsStale(now)).Select(v => v.Key).ToList();

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        if (_vitals.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double x = 0;
        double centerY = ActualHeight / 2;

        foreach (var vital in _vitals)
        {
            // 한 번도 뛴 적 없음 → 「모름」(회색). 멈춤 → 경고색. 정상 → 초록.
            var brush = vital.LastBeat is null
                ? HudPalette.Unknown
                : vital.IsStale(now) ? HudPalette.Down : HudPalette.Up;

            dc.DrawEllipse(brush, null, new Point(x + DotRadius, centerY), DotRadius, DotRadius);
            x += DotRadius * 2 + LabelGap;

            var text = FormattedTextCache.Get(vital.Label, Face, FontSize, HudPalette.TextMuted, ppd);
            dc.DrawText(text, new Point(x, centerY - text.Height / 2));

            x += text.Width + ItemSpacing;
        }
    }

    protected override void OnToolTipOpening(System.Windows.Controls.ToolTipEventArgs e)
    {
        // 마우스가 올라간 항목의 설명을 보여준다.
        var position = Mouse.GetPositionRelativeTo(this);
        ToolTip = HitTest(position) ?? "감시 중인 백그라운드 작업";

        base.OnToolTipOpening(e);
    }

    private string? HitTest(Point point)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double x = 0;

        foreach (var vital in _vitals)
        {
            double start = x;
            x += DotRadius * 2 + LabelGap;

            var text = FormattedTextCache.Get(vital.Label, Face, FontSize, HudPalette.TextMuted, ppd);
            x += text.Width + ItemSpacing;

            if (point.X >= start && point.X < x) return vital.Tooltip;
        }

        return null;
    }

    /// <summary>감시 대상 하나.</summary>
    private sealed class Vital(string key, string label, string tooltip, TimeSpan timeout)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public string Tooltip { get; } = tooltip;
        public TimeSpan Timeout { get; } = timeout;
        public DateTimeOffset? LastBeat { get; set; }

        /// <summary>제한 시간 안에 뛰지 않았는가. 한 번도 안 뛰었으면 「모름」이지 「멈춤」이 아니다.</summary>
        public bool IsStale(DateTimeOffset now) =>
            LastBeat is { } last && now - last > Timeout;
    }

    /// <summary>마우스 위치를 얻는 최소 헬퍼(테스트에서 WPF 입력 스택을 타지 않게 분리).</summary>
    private static class Mouse
    {
        public static Point GetPositionRelativeTo(IInputElement element) =>
            System.Windows.Input.Mouse.GetPosition(element);
    }
}
