using System.Globalization;
using System.Windows;
using System.Windows.Media;
using IoTSensorDashboard.ControlRoom.Model;
using IoTSensorDashboard.Core.Formatting;
using IoTSensorDashboard.Ui.Common.Rendering;
using IoTSensorDashboard.Ui.Common.Theme;

namespace IoTSensorDashboard.ControlRoom.Views;

/// <summary>
/// 파이프라인을 <b>그림 한 장</b>으로 — 센서에서 대시보드까지.
///
/// 🔑 <b>왜 숫자 카드가 아니라 그림인가</b>: 「수신 3,900/s · 저장 3,880/s · 백로그 12」를
///    숫자로 늘어놓으면 <b>어느 단계가 막혔는지</b>를 사람이 머릿속에서 이어 붙여야 한다.
///    단계를 순서대로 그려 놓으면 <b>막힌 자리에서 눈이 멈춘다</b> — 그게 관제 화면의 일이다.
///
/// 📌 각 노드는 자기 단계의 <b>실측값</b>을 들고 있다. 장식용 상자가 아니다.
/// </summary>
public sealed class PipelineView : RenderPanel
{
    private PipelineSnapshot _snapshot = PipelineSnapshot.Empty();

    public void Update(PipelineSnapshot snapshot)
    {
        _snapshot = snapshot ?? PipelineSnapshot.Empty();
        Redraw();
    }

    protected override void RenderContent(DrawingContext dc, Rect area)
    {
        var s = _snapshot;

        double headerHeight = 62;
        double stripHeight = 34;

        DrawUptime(dc, new Rect(area.X, area.Y, area.Width, headerHeight), s);

        var flow = new Rect(
            area.X, area.Y + headerHeight,
            area.Width, Math.Max(0, area.Height - headerHeight - stripHeight));

        if (flow.Height > 60) DrawFlow(dc, flow, s);

        DrawActivity(dc, new Rect(area.X, area.Bottom - stripHeight + 6, area.Width, stripHeight - 6), s);
    }

    /// <summary>
    /// 센서 응답률 — 화면에서 가장 큰 글자.
    ///
    /// 🔴 분모가 0 이면 <b>「측정 불가」</b>다. 100% 가 아니다.
    ///    관측하지 못한 것을 만점으로 그리는 것이 이 프로젝트에서 가장 비싼 거짓말이었다.
    ///
    /// 🔴 <b>「가동률」이라 부르지 않는다 — 실측으로 드러난 오해.</b>
    ///    이 값이 재는 것은 <b>「응답하는가」</b>이지 <b>「일하고 있는가」</b>가 아니다.
    ///    관제실은 조용한 센서에게 직접 물어보고(핑) 답이 오면 온라인으로 센다.
    ///    그래서 <b>발신이 완전히 멈춰 데이터가 0 건이어도 이 값은 100%</b> 가 나온다.
    ///    실제로 그 상태를 재현했을 때 화면 전체가 「완벽」으로 보였다 —
    ///    「가동률」이라는 말이 그 오해의 절반을 만들고 있었다.
    /// </summary>
    private void DrawUptime(DrawingContext dc, Rect area, PipelineSnapshot s)
    {
        double cx = area.X + area.Width / 2;

        var color = s.Uptime switch
        {
            null => HudPalette.Unknown,
            >= 0.999 => HudPalette.In,
            >= 0.95 => HudPalette.Warn,
            _ => HudPalette.Down
        };

        HudDraw.Text(dc, "센서 응답률", cx - 96, area.Y + 14, 12.5, HudPalette.TextMuted, Ppd,
            HudDraw.Weight.Semi, HudDraw.Align.Right);

        if (s.Uptime is double u)
        {
            HudDraw.TextFit(dc, (u * 100).ToString("F2", CultureInfo.CurrentCulture) + "%",
                cx - 86, area.Y, 34, 16, area.Width * 0.42, color, Ppd, HudDraw.Weight.Heavy);
        }
        else
        {
            HudDraw.TextFit(dc, "측정 불가", cx - 86, area.Y + 6, 24, 12, area.Width * 0.42,
                HudPalette.Unknown, Ppd, HudDraw.Weight.Heavy);
        }

        // 🔑 N/M 을 같이 — 분모를 모르는 비율은 믿을 근거가 없다.
        HudDraw.Text(dc, $"{s.SensorsOnline:N0} / {s.SensorsTotal:N0} 응답",
            cx - 86, area.Y + 40, 11, HudPalette.TextDim, Ppd);

        // 🔴 응답률만으로는 「데이터가 들어오는가」를 알 수 없다.
        //    센서가 전부 응답해도 발신이 멈춰 있으면 이 화면은 온통 초록이 된다.
        //    그 상태를 여기서 깨뜨린다 — 초록불만 있는 화면이 최약점을 감춘다.
        if (!s.DataFlowing)
        {
            // 🔒 오른쪽 끝에 붙인다. 가운데 큰 숫자는 폭이 값에 따라 변하므로
            //    그 옆에 상대 좌표로 두면 **글자가 겹친다**(실제로 겹쳤다).
            //    경계가 고정된 쪽에 정렬하는 것이 안전하다.
            double limit = area.Width * 0.42;

            HudDraw.Text(dc, "⚠ 응답은 오지만 데이터 수신 없음", area.Right, area.Y + 14, 12,
                HudPalette.Warn, Ppd, HudDraw.Weight.Semi, HudDraw.Align.Right, limit);

            HudDraw.Text(dc, "센서는 살아 있고 발신이 멈춘 상태", area.Right, area.Y + 32, 10,
                HudPalette.TextDim, Ppd, align: HudDraw.Align.Right, maxWidth: limit);
        }
    }

    /// <summary>센서 다발 → 노드 4개.</summary>
    private void DrawFlow(DrawingContext dc, Rect area, PipelineSnapshot s)
    {
        double sensorWidth = Math.Min(120, area.Width * 0.14);
        var funnel = new Point(area.X + sensorWidth, area.Y + area.Height / 2);

        DrawSensorBundle(dc, new Rect(area.X, area.Y, sensorWidth, area.Height), funnel, s);

        // 🔴 이 네 값은 **전부 이번 세션 기준**이다.
        //    전체 기간 누적(저장소 행 수)을 여기 섞으면 「저장이 수신의 1,000배」처럼 보인다 —
        //    계산은 맞는데 기간이 달라서 생기는 거짓말이고, 실제로 한 번 냈던 결함이다.
        //    전체 기간 값은 하단 카드에 「(전체 기간)」 라벨과 함께 따로 있다.
        (string Title, string Sub, string Value, Brush Color)[] nodes =
        [
            ("수집 큐", "바운드 큐 · 최신우선", s.Backlog.ToString("N0", CultureInfo.CurrentCulture),
                s.Backlog > 0 ? HudPalette.Warn : HudPalette.In),

            ("저장", "append-only · 이벤트", CompactNumber.Format(s.SessionStored), HudPalette.Accent),

            // 🔴 집계 노드에는 값을 넣지 않는다.
            //    넣을 만한 카운터가 **메시지 단위**뿐이라, 이벤트 단위인 저장 옆에 놓으면
            //    「저장 3,722 → 집계 1,862」가 되어 뒤 단계에서 절반이 사라진 것처럼 보인다.
            //    한 메시지에 in·out 두 이벤트가 들어 있어 생기는 차이일 뿐인데도 그렇게 읽힌다.
            //    단위가 다른 수를 흐름에 나란히 놓느니 **비워 두는 편**이 정직하다.
            ("집계", "시간 버킷 (I3)", "", HudPalette.Accent),

            // 단위를 부제에 적는다 — 위 카드의 「수신」은 메시지, 여기는 이벤트다.
            ("대시보드", "실시간 · 이벤트/s", RateText.Format(s.StoreRate), HudPalette.In),
        ];

        double left = funnel.X + 26;
        double span = area.Right - left;
        double gap = 22;
        double nodeWidth = Math.Max(60, (span - gap * (nodes.Length - 1)) / nodes.Length);
        double nodeHeight = Math.Min(72, area.Height * 0.5);
        double top = area.Y + (area.Height - nodeHeight) / 2;

        var previousEdge = funnel;

        for (int i = 0; i < nodes.Length; i++)
        {
            var rect = new Rect(left + i * (nodeWidth + gap), top, nodeWidth, nodeHeight);

            // 연결선 — 노드보다 먼저 그려서 노드 뒤로 지나가게.
            DrawLink(dc, previousEdge, new Point(rect.X, rect.Y + rect.Height / 2),
                i == 0 ? $"워커 ×{s.Workers}" : null, i == 0 ? "병렬 처리 · 적응" : null);

            DrawNode(dc, rect, nodes[i].Title, nodes[i].Sub, nodes[i].Value, nodes[i].Color);

            previousEdge = new Point(rect.Right, rect.Y + rect.Height / 2);
        }
    }

    private void DrawSensorBundle(DrawingContext dc, Rect area, Point funnel, PipelineSnapshot s)
    {
        HudDraw.Text(dc, "센서", area.X, area.Y, 11, HudPalette.Foreground, Ppd, HudDraw.Weight.Semi);
        HudDraw.Text(dc, "FLIR · Milesight", area.X, area.Y + 13, 9.5, HudPalette.TextDim, Ppd);

        // 점 개수는 고정이다 — 1,000개를 다 찍을 수도 없고, 찍어도 못 읽는다.
        // 여기서 점은 「센서가 여럿이다」를 뜻하는 기호이지 개수를 세는 눈금이 아니다.
        const int Dots = 16;

        double top = area.Y + 30;
        double height = Math.Max(0, area.Bottom - top - 6);
        if (height < 20) return;

        // 살아 있는 비율만큼 위에서부터 초록으로 — 아래쪽 회색이 죽은 몫이다.
        int alive = s.Uptime is double u ? (int)Math.Round(Dots * Math.Clamp(u, 0, 1)) : 0;

        var thin = new Pen(HudPalette.FrozenBrush(0x30, 0x5E, 0xEA, 0xD4), 1)
        {
            DashStyle = new DashStyle([2, 3], 0)
        };
        thin.Freeze();

        for (int i = 0; i < Dots; i++)
        {
            double y = top + height * i / (Dots - 1.0);
            var at = new Point(area.X + 8, y);

            dc.DrawLine(thin, at, funnel);
            dc.DrawEllipse(i < alive ? HudPalette.In : HudPalette.Unknown, null, at, 2.6, 2.6);
        }

        dc.DrawEllipse(HudPalette.In, null, funnel, 3.4, 3.4);
    }

    private void DrawLink(DrawingContext dc, Point from, Point to, string? label, string? sub)
    {
        var pen = new Pen(HudPalette.In, 1.3);
        pen.Freeze();

        dc.DrawLine(pen, from, to);

        // 진행 방향 표시 — 선만 있으면 어느 쪽으로 흐르는지 알 수 없다.
        double mid = (from.X + to.X) / 2;
        var head = new StreamGeometry();

        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(mid + 4, to.Y), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(mid - 3, to.Y - 4), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new Point(mid - 3, to.Y + 4), isStroked: false, isSmoothJoin: false);
        }

        head.Freeze();
        dc.DrawGeometry(HudPalette.In, null, head);

        if (label is null) return;

        HudDraw.Text(dc, label, mid, to.Y - 26, 11, HudPalette.Foreground, Ppd,
            HudDraw.Weight.Semi, HudDraw.Align.Center);

        if (sub is not null)
            HudDraw.Text(dc, sub, mid, to.Y + 8, 9, HudPalette.TextDim, Ppd, align: HudDraw.Align.Center);
    }

    private void DrawNode(DrawingContext dc, Rect rect, string title, string sub, string value, Brush color)
    {
        var pen = new Pen(color, 1.3);
        pen.Freeze();

        dc.DrawRoundedRectangle(HudPalette.FrozenBrush(0xFF, 0x10, 0x14, 0x1B), pen, rect, 8, 8);

        double inner = rect.Width - 18;
        if (inner < 12) return;

        HudDraw.TextFit(dc, title, rect.X + 9, rect.Y + 8, 12.5, 8.5, inner,
            HudPalette.Foreground, Ppd, HudDraw.Weight.Semi);

        HudDraw.TextFit(dc, sub, rect.X + 9, rect.Y + 24, 9.5, 7, inner, HudPalette.TextDim, Ppd);

        // 값이 없는 단계는 값 자리를 비운다 — "0" 이나 "—" 를 넣으면 그게 측정값처럼 읽힌다.
        if (value.Length == 0) return;

        HudDraw.TextFit(dc, value, rect.X + 9, rect.Bottom - 26, 17, 10, inner,
            color, Ppd, HudDraw.Weight.Heavy);
    }

    /// <summary>
    /// 처리 활동 — 최근 처리량을 막대로.
    ///
    /// 🔑 <b>「돌고 있다」를 눈에 보이게</b> 하는 장치다. 숫자만 있으면 값이 굳었는지
    ///    원래 그 값인지 구별할 수 없다. 막대가 흐르면 살아 있는 것이다.
    /// </summary>
    private void DrawActivity(DrawingContext dc, Rect area, PipelineSnapshot s)
    {
        HudDraw.Text(dc, "처리 활동", area.X, area.Y - 2, 9.5, HudPalette.TextDim, Ppd);

        var plot = new Rect(area.X + 62, area.Y, Math.Max(0, area.Width - 62), area.Height - 4);
        if (plot.Width < 20 || s.Activity.Count == 0) return;

        double peak = 1;
        foreach (var v in s.Activity) peak = Math.Max(peak, v);

        double slot = plot.Width / s.Activity.Count;
        double barWidth = Math.Max(1.5, slot - 2);

        for (int i = 0; i < s.Activity.Count; i++)
        {
            double h = plot.Height * Math.Clamp(s.Activity[i] / peak, 0.06, 1);

            dc.DrawRoundedRectangle(
                s.Activity[i] > 0 ? HudPalette.In : HudPalette.FrozenBrush(0x24, 0xFF, 0xFF, 0xFF),
                null,
                new Rect(plot.X + slot * i, plot.Bottom - h, barWidth, h), 1.5, 1.5);
        }
    }
}
