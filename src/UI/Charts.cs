using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VolumeGuard.Core;

namespace VolumeGuard.UI
{
    /// <summary>Base dos gráficos desenhados à mão: texto, dica flutuante e área de hover.</summary>
    public abstract class ChartBase : FrameworkElement
    {
        protected static readonly Typeface Regular = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        protected static readonly Typeface Bold = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        protected static readonly Pen GridPen = Frozen(new Pen(Palette.Hairline, 1));
        protected static readonly Pen AxisPen = Frozen(new Pen(Palette.Axis, 1));
        protected Point? Hover;

        protected static Pen Frozen(Pen p) { p.Freeze(); return p; }

        protected static Pen Dashed(Brush b, double thickness)
        {
            var p = new Pen(b, thickness) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
            p.Freeze();
            return p;
        }

        protected FormattedText Text(string s, double size, Brush brush, bool bold = false)
        {
            return new FormattedText(s, Fmt.Br, FlowDirection.LeftToRight, bold ? Bold : Regular, size, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        protected override void OnMouseMove(MouseEventArgs e) { Hover = e.GetPosition(this); InvalidateVisual(); }
        protected override void OnMouseLeave(MouseEventArgs e) { Hover = null; InvalidateVisual(); }

        protected static double Snap(double v) { return Math.Round(v) + 0.5; }

        protected void HLine(DrawingContext dc, Pen pen, double x0, double x1, double y)
        {
            dc.DrawLine(pen, new Point(x0, Snap(y)), new Point(x1, Snap(y)));
        }

        protected void DrawTooltip(DrawingContext dc, Point anchor, IList<string> lines)
        {
            var texts = new List<FormattedText>();
            double w = 0, h = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = Text(lines[i], i == 0 ? 12.5 : 12, i == 0 ? (Brush)Palette.Ink : Palette.InkSecondary, i == 0);
                texts.Add(t);
                w = Math.Max(w, t.Width);
                h += t.Height + (i > 0 ? 1 : 0);
            }
            double pad = 8, bw = w + pad * 2, bh = h + pad * 2;
            double x = anchor.X + 12, y = anchor.Y - bh - 8;
            if (x + bw > ActualWidth) x = anchor.X - bw - 12;
            if (x < 0) x = 0;
            if (y < 0) y = Math.Min(anchor.Y + 12, Math.Max(0, ActualHeight - bh));
            dc.DrawRoundedRectangle(Palette.Raised, AxisPen, new Rect(x, y, bw, bh), 7, 7);
            double cy = y + pad;
            foreach (var t in texts) { dc.DrawText(t, new Point(x + pad, cy)); cy += t.Height + 1; }
        }

        protected static Geometry TopRounded(Rect r, double radius)
        {
            double rad = Math.Max(0, Math.Min(radius, Math.Min(r.Width / 2, r.Height)));
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(r.Left, r.Bottom), true, true);
                c.LineTo(new Point(r.Left, r.Top + rad), false, false);
                if (rad > 0) c.ArcTo(new Point(r.Left + rad, r.Top), new Size(rad, rad), 0, false, SweepDirection.Clockwise, false, false);
                c.LineTo(new Point(r.Right - rad, r.Top), false, false);
                if (rad > 0) c.ArcTo(new Point(r.Right, r.Top + rad), new Size(rad, rad), 0, false, SweepDirection.Clockwise, false, false);
                c.LineTo(new Point(r.Right, r.Bottom), false, false);
            }
            g.Freeze();
            return g;
        }
    }

    /// <summary>Barra de progresso fina (medidores de app e de dose).</summary>
    public class MeterBar : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register("Value", typeof(double), typeof(MeterBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register("Maximum", typeof(double), typeof(MeterBar),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty MarkerAtProperty = DependencyProperty.Register("MarkerAt", typeof(double), typeof(MeterBar),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty FillProperty = DependencyProperty.Register("Fill", typeof(Brush), typeof(MeterBar),
            new FrameworkPropertyMetadata(Palette.Accent, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Value { get { return (double)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
        public double Maximum { get { return (double)GetValue(MaximumProperty); } set { SetValue(MaximumProperty, value); } }
        public double MarkerAt { get { return (double)GetValue(MarkerAtProperty); } set { SetValue(MarkerAtProperty, value); } }
        public Brush Fill { get { return (Brush)GetValue(FillProperty); } set { SetValue(FillProperty, value); } }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight, r = h / 2;
            if (w <= 0 || h <= 0) return;
            dc.DrawRoundedRectangle(Palette.Track, null, new Rect(0, 0, w, h), r, r);
            double max = Maximum > 0 ? Maximum : 1;
            double f = Math.Max(0, Math.Min(1, Value / max));
            if (double.IsNaN(f)) f = 0;
            if (f > 0) dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(h, w * f), h), r, r);
            if (!double.IsNaN(MarkerAt) && MarkerAt > 0 && MarkerAt < max)
            {
                double x = Math.Round(w * MarkerAt / max);
                dc.DrawRectangle(Palette.Page, null, new Rect(x - 1, -2, 3, h + 4));
                dc.DrawRectangle(Palette.InkSecondary, null, new Rect(x, -2, 1, h + 4));
            }
        }
    }

    /// <summary>Nível dos últimos 60 s (10 amostras por segundo).</summary>
    public class LiveChart : ChartBase
    {
        double[] data = new double[0];
        double limitLine = double.NaN, alertLine = double.NaN;
        static readonly Pen LinePen = MakeLinePen();
        static readonly Pen AlertPen = Dashed(Palette.Critical, 1);
        static readonly Pen LimitPen = Dashed(Palette.Serious, 1);

        static Pen MakeLinePen()
        {
            var p = new Pen(Palette.Accent, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            p.Freeze();
            return p;
        }

        public void SetData(double[] values, double limit, double alert)
        {
            data = values ?? new double[0];
            limitLine = limit; alertLine = alert;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            double left = 30, right = 8, top = 6, bottom = 20;
            double pw = W - left - right, ph = H - top - bottom;
            if (pw <= 10 || ph <= 10) return;

            double maxData = 0;
            foreach (var v in data) if (!double.IsNaN(v) && !double.IsInfinity(v)) maxData = Math.Max(maxData, v);
            double yMin = 30, yMax = Math.Max(100, Math.Ceiling((maxData + 5) / 10) * 10);
            Func<double, double> Y = v => top + ph * (1 - (Math.Max(yMin, Math.Min(yMax, v)) - yMin) / (yMax - yMin));

            for (double g = 40; g <= yMax; g += 20)
            {
                HLine(dc, GridPen, left, W - right, Y(g));
                var t = Text(g.ToString("0"), 11, Palette.Muted);
                dc.DrawText(t, new Point(left - 6 - t.Width, Y(g) - t.Height / 2));
            }
            HLine(dc, AxisPen, left, W - right, Y(yMin));

            int n = data.Length;
            Func<int, double> X = i => left + (n <= 1 ? 0 : pw * i / (n - 1.0));
            if (n > 1)
            {
                var fill = new StreamGeometry();
                var line = new StreamGeometry();
                using (var fc = fill.Open())
                using (var lc = line.Open())
                {
                    int i = 0;
                    while (i < n)
                    {
                        while (i < n && !Valid(data[i])) i++;
                        if (i >= n) break;
                        int start = i;
                        lc.BeginFigure(new Point(X(i), Y(data[i])), false, false);
                        fc.BeginFigure(new Point(X(i), Y(yMin)), true, true);
                        fc.LineTo(new Point(X(i), Y(data[i])), false, false);
                        i++;
                        while (i < n && Valid(data[i]))
                        {
                            var p = new Point(X(i), Y(data[i]));
                            lc.LineTo(p, true, true);
                            fc.LineTo(p, false, false);
                            i++;
                        }
                        fc.LineTo(new Point(X(i - 1), Y(yMin)), false, false);
                        if (i - start == 1) lc.LineTo(new Point(X(start) + 1, Y(data[start])), true, false);
                    }
                }
                fill.Freeze(); line.Freeze();
                dc.DrawGeometry(Palette.AccentFill, null, fill);
                dc.DrawGeometry(null, LinePen, line);
            }

            bool same = !double.IsNaN(limitLine) && !double.IsNaN(alertLine) && Math.Abs(limitLine - alertLine) < 0.5;
            if (!double.IsNaN(alertLine)) DrawThreshold(dc, AlertPen, Palette.Critical, alertLine, same ? "limite e alerta " : "alerta ", Y, left, W - right);
            if (!double.IsNaN(limitLine) && !same) DrawThreshold(dc, LimitPen, Palette.Serious, limitLine, "limite ", Y, left, W - right);

            var l0 = Text("−60 s", 11, Palette.Muted);
            dc.DrawText(l0, new Point(left, H - l0.Height));
            var l1 = Text("−30 s", 11, Palette.Muted);
            dc.DrawText(l1, new Point(left + pw / 2 - l1.Width / 2, H - l1.Height));
            var l2 = Text("agora", 11, Palette.Muted);
            dc.DrawText(l2, new Point(W - right - l2.Width, H - l2.Height));

            if (Hover.HasValue && n > 1)
            {
                double hx = Math.Max(left, Math.Min(W - right, Hover.Value.X));
                int idx = (int)Math.Round((hx - left) / pw * (n - 1));
                idx = Math.Max(0, Math.Min(n - 1, idx));
                double x = X(idx);
                dc.DrawLine(AxisPen, new Point(Snap(x), top), new Point(Snap(x), Y(yMin)));
                double secondsAgo = (n - 1 - idx) / 10.0;
                string when = secondsAgo < 0.5 ? "agora" : "há " + Math.Round(secondsAgo).ToString("0") + " s";
                if (Valid(data[idx]))
                {
                    var p = new Point(x, Y(data[idx]));
                    dc.DrawEllipse(Palette.Surface, new Pen(Palette.Accent, 2), p, 4.5, 4.5);
                    DrawTooltip(dc, p, new[] { Fmt.Spl(data[idx]), when });
                }
                else DrawTooltip(dc, new Point(x, Y(yMin) - 10), new[] { "Silêncio", when });
            }
        }

        void DrawThreshold(DrawingContext dc, Pen pen, Brush brush, double v, string label, Func<double, double> Y, double x0, double x1)
        {
            double y = Y(v);
            HLine(dc, pen, x0, x1, y);
            var t = Text(label + Fmt.Spl(v), 10.5, brush);
            dc.DrawRectangle(Palette.Surface, null, new Rect(x1 - t.Width - 6, y - t.Height - 1, t.Width + 6, t.Height));
            dc.DrawText(t, new Point(x1 - t.Width - 2, y - t.Height - 1));
        }

        static bool Valid(double v) { return !double.IsNaN(v) && !double.IsInfinity(v) && v > 0; }
    }

    /// <summary>Nível ao longo de um dia, em blocos de 10 minutos.</summary>
    public class DayChart : ChartBase
    {
        const int Buckets = 144;
        readonly double[] leq = new double[Buckets];
        readonly int[] active = new int[Buckets];
        readonly string[] topApp = new string[Buckets];
        bool any;
        static readonly Pen RefPen = Dashed(Palette.Critical, 1);

        public void SetDay(DayData day)
        {
            var energy = new double[Buckets];
            var apps = new Dictionary<string, double>[Buckets];
            Array.Clear(active, 0, Buckets);
            any = false;
            foreach (var m in day.Minutes)
            {
                int b = Math.Max(0, Math.Min(Buckets - 1, m.Minute / 10));
                double e = m.Energy;
                energy[b] += e;
                active[b] += m.Active;
                if (apps[b] == null) apps[b] = new Dictionary<string, double>();
                foreach (var kv in m.Apps)
                {
                    double cur; apps[b].TryGetValue(kv.Key, out cur);
                    apps[b][kv.Key] = cur + e * kv.Value;
                }
            }
            for (int i = 0; i < Buckets; i++)
            {
                leq[i] = Hearing.LeqFromEnergy(energy[i], active[i]);
                topApp[i] = apps[i] != null && apps[i].Count > 0 ? apps[i].OrderByDescending(k => k.Value).First().Key : null;
                if (active[i] > 0) any = true;
            }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            double left = 30, right = 8, top = 8, bottom = 22;
            double pw = W - left - right, ph = H - top - bottom;
            if (pw <= 10 || ph <= 10) return;
            double yMin = 40, yMax = 100;
            Func<double, double> Y = v => top + ph * (1 - (Math.Max(yMin, Math.Min(yMax, v)) - yMin) / (yMax - yMin));

            for (double g = 40; g <= 100; g += 20)
            {
                if (g > yMin) HLine(dc, GridPen, left, W - right, Y(g));
                var t = Text(g.ToString("0"), 11, Palette.Muted);
                dc.DrawText(t, new Point(left - 6 - t.Width, Y(g) - t.Height / 2));
            }
            double slot = pw / Buckets;
            double barW = Math.Max(1, slot - 1.5);
            int hoverIdx = -1;
            if (Hover.HasValue && Hover.Value.X >= left && Hover.Value.X <= W - right)
                hoverIdx = Math.Max(0, Math.Min(Buckets - 1, (int)((Hover.Value.X - left) / slot)));

            for (int i = 0; i < Buckets; i++)
            {
                if (active[i] == 0 || double.IsInfinity(leq[i])) continue;
                double x = left + i * slot + (slot - barW) / 2;
                double yTop = Y(leq[i]);
                var r = new Rect(x, yTop, barW, Math.Max(1, Y(yMin) - yTop));
                dc.DrawGeometry(Palette.Zone(Hearing.Zone(leq[i])), null, TopRounded(r, 2));
                if (i == hoverIdx) dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null, TopRounded(r, 2));
            }
            HLine(dc, AxisPen, left, W - right, Y(yMin));
            HLine(dc, RefPen, left, W - right, Y(85));
            var refText = Text("85 dB", 10.5, Palette.Critical);
            dc.DrawRectangle(Palette.Surface, null, new Rect(W - right - refText.Width - 6, Y(85) - refText.Height - 1, refText.Width + 6, refText.Height));
            dc.DrawText(refText, new Point(W - right - refText.Width - 2, Y(85) - refText.Height - 1));

            for (int h = 0; h <= 24; h += 3)
            {
                var t = Text(h + "h", 11, Palette.Muted);
                double x = left + pw * h / 24.0 - t.Width / 2;
                x = Math.Max(left - 4, Math.Min(W - right - t.Width, x));
                dc.DrawText(t, new Point(x, H - t.Height));
            }

            if (!any)
            {
                var t = Text("Sem som registrado neste dia", 13, Palette.Muted);
                dc.DrawText(t, new Point(left + pw / 2 - t.Width / 2, top + ph / 2 - t.Height / 2));
            }
            if (hoverIdx >= 0 && active[hoverIdx] > 0)
            {
                int m0 = hoverIdx * 10, m1 = m0 + 10;
                var lines = new List<string>
                {
                    string.Format("{0:00}:{1:00}–{2:00}:{3:00}", m0 / 60, m0 % 60, (m1 / 60) % 24, m1 % 60),
                    Fmt.Spl(leq[hoverIdx]) + " · " + Hearing.ZoneName(Hearing.Zone(leq[hoverIdx])) + " · " + Hearing.FormatDuration(active[hoverIdx]) + " com som",
                };
                if (topApp[hoverIdx] != null) lines.Add("Principal: " + topApp[hoverIdx]);
                double x = left + hoverIdx * slot + slot / 2;
                DrawTooltip(dc, new Point(x, Y(leq[hoverIdx])), lines);
            }
        }
    }

    /// <summary>Dose diária dos últimos 14 dias.</summary>
    public class WeekChart : ChartBase
    {
        public class Day
        {
            public DateTime Date;
            public double Dose, Leq;
            public int Active;
        }

        List<Day> days = new List<Day>();
        DateTime selected;
        static readonly Pen RefPen = Dashed(Palette.InkSecondary, 1);
        static readonly Pen SelPen = Frozen(new Pen(Palette.Ink, 1.5));
        public event Action<DateTime> DayClicked;

        static readonly string[] Wd = { "dom", "seg", "ter", "qua", "qui", "sex", "sáb" };

        public void SetData(List<Day> d, DateTime sel)
        {
            days = d ?? new List<Day>();
            selected = sel.Date;
            InvalidateVisual();
        }

        double left = 36, right = 8, top = 10, bottom = 34;

        int IndexAt(Point p)
        {
            if (days.Count == 0) return -1;
            double pw = ActualWidth - left - right;
            double slot = pw / days.Count;
            if (p.X < left || p.X > ActualWidth - right) return -1;
            return Math.Max(0, Math.Min(days.Count - 1, (int)((p.X - left) / slot)));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            int i = IndexAt(e.GetPosition(this));
            if (i >= 0 && DayClicked != null) DayClicked(days[i].Date);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            double pw = W - left - right, ph = H - top - bottom;
            if (pw <= 10 || ph <= 10 || days.Count == 0) return;
            double maxDose = days.Max(d => d.Dose);
            double yMax = Math.Max(100, Math.Ceiling(maxDose * 1.15 / 50) * 50);
            Func<double, double> Y = v => top + ph * (1 - Math.Min(yMax, Math.Max(0, v)) / yMax);

            for (double g = 0; g <= yMax; g += yMax > 200 ? 100 : 50)
            {
                if (g > 0 && Math.Abs(g - 100) > 0.1) HLine(dc, GridPen, left, W - right, Y(g));
                var t = Text(g.ToString("0") + "%", 11, Palette.Muted);
                dc.DrawText(t, new Point(left - 6 - t.Width, Y(g) - t.Height / 2));
            }
            double slot = pw / days.Count;
            double barW = Math.Min(26, slot * 0.62);
            int hoverIdx = Hover.HasValue ? IndexAt(Hover.Value) : -1;
            for (int i = 0; i < days.Count; i++)
            {
                var d = days[i];
                double cx = left + slot * i + slot / 2;
                if (d.Dose > 0)
                {
                    double yTop = Math.Min(Y(d.Dose), Y(0) - 2);
                    var r = new Rect(cx - barW / 2, yTop, barW, Y(0) - yTop);
                    dc.DrawGeometry(Palette.DoseBrush(d.Dose), null, TopRounded(r, 4));
                    if (i == hoverIdx) dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null, TopRounded(r, 4));
                }
                bool isSel = d.Date.Date == selected;
                bool isToday = d.Date.Date == DateTime.Today;
                var wd = Text(isToday ? "hoje" : Wd[(int)d.Date.DayOfWeek], 10.5, isSel ? (Brush)Palette.Ink : Palette.Muted, isSel);
                var dn = Text(d.Date.Day.ToString(), 11, isSel ? (Brush)Palette.Ink : Palette.InkSecondary, isSel);
                dc.DrawText(wd, new Point(cx - wd.Width / 2, H - bottom + 5));
                dc.DrawText(dn, new Point(cx - dn.Width / 2, H - bottom + 5 + wd.Height - 1));
                if (isSel) dc.DrawRoundedRectangle(null, SelPen, new Rect(cx - slot / 2 + 2, H - bottom + 3, slot - 4, bottom - 4), 6, 6);
            }
            HLine(dc, AxisPen, left, W - right, Y(0));
            HLine(dc, RefPen, left, W - right, Y(100));

            if (hoverIdx >= 0)
            {
                var d = days[hoverIdx];
                var lines = new List<string>
                {
                    d.Date.ToString("dddd, dd/MM", Fmt.Br),
                    "Dose " + Fmt.Percent(d.Dose),
                    d.Active > 0 ? Hearing.FormatDuration(d.Active) + " ouvindo · média " + Fmt.Spl(d.Leq) : "Sem som registrado",
                };
                double cx = left + slot * hoverIdx + slot / 2;
                DrawTooltip(dc, new Point(cx, Y(Math.Max(d.Dose, 5))), lines);
            }
        }
    }
}
