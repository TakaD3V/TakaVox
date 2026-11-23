using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TakaVox.Dsp;

namespace TakaVox.UI
{
    static class Draw
    {
        static readonly Typeface Face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static readonly Typeface FaceBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        public static FormattedText Text(Visual v, string s, double size, Brush b, bool bold = false)
        {
            double dpi = 1.0;
            try { dpi = VisualTreeHelper.GetDpi(v).PixelsPerDip; } catch { }
            return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, b, dpi);
        }

        public static Pen Pen(Brush b, double t) { var p = new Pen(b, t); p.Freeze(); return p; }
    }

    /// <summary>
    /// Pinceis dos controles desenhados a mao, feitos a partir das cores do tema atual (congelados = rapidos) e
    /// refeitos quando o tema muda. Nunca congele um pincel do Theme diretamente (ele e "vivo").
    /// </summary>
    static class Paint
    {
        static int ver = -1;
        public static Brush Well, Graph, Track, OutFill, EqFill, Tip, WaveA, WaveB, Accent, AccentHi, WellText, WellSub, WellDim;
        public static Pen WellPen, TrackPen, ValuePen, Grid, Zero, InPen, OutPen, EqPen, Mid;

        static Pen Round(Brush b, double t) { var p = new Pen(b, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; p.Freeze(); return p; }

        public static void Ensure()
        {
            if (ver == Theme.Version) return;
            ver = Theme.Version;
            var t = Theme.Current;
            Well = Theme.Alpha(t.Well, 255);
            Graph = Theme.Alpha(t.Graph, 255);
            Track = Theme.Alpha(t.Track, 255);
            Accent = Theme.Alpha(t.Accent, 255);
            AccentHi = Theme.Alpha(t.AccentHi, 255);
            WellText = Theme.Alpha(t.WellText, 255);
            WellSub = Theme.Alpha(t.WellSub, 255);
            WellDim = Theme.Alpha(t.WellDim, 255);
            WellPen = Draw.Pen(Well, 1);
            TrackPen = Round(Track, 7);
            ValuePen = Round(Accent, 7);
            Grid = Draw.Pen(Theme.Alpha(t.WellLine, 160), 1);
            Zero = Draw.Pen(Theme.Alpha(t.WellSub, 90), 1);
            InPen = Draw.Pen(Theme.Alpha(t.WellSub, 110), 1.2);
            OutPen = Draw.Pen(Theme.Alpha(t.Good, 230), 1.4);
            OutFill = Theme.Alpha(t.Good, 38);
            EqPen = Draw.Pen(AccentHi, 2.2);
            EqFill = Theme.Alpha(t.Accent, 34);
            Tip = Theme.Alpha(ThemeDef.Lerp(t.Graph, t.WellText, 0.1), 235);
            WaveA = Theme.Alpha(t.WellSub, 200);
            WaveB = Theme.Alpha(t.Good, 220);
            Mid = Draw.Pen(Theme.Alpha(t.WellLine, 255), 1);
        }
    }

    /// <summary>Medidor de nivel estilo LED (verde/amarelo/vermelho) com marca de pico.</summary>
    public sealed class LevelMeter : FrameworkElement
    {
        public float Db = -120f, PeakDb = -120f;
        public float MinDb = -60f, MaxDb = 3f;
        public bool ShowScale = true;
        Brush fill;
        int fillVer = -1;
        static Brush BgB => Paint.Well;
        static Pen SegPen => Paint.WellPen;

        double Frac(float db) => Math.Max(0, Math.Min(1, (db - MinDb) / (MaxDb - MinDb)));

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 10) return;
            Paint.Ensure();
            double scaleW = ShowScale ? 26 : 0;
            var bar = new Rect(scaleW, 0, w - scaleW, h);
            dc.DrawRoundedRectangle(BgB, null, bar, 5, 5);
            if (fill == null || fillVer != Theme.Version)
            {
                fillVer = Theme.Version;
                var g = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
                g.GradientStops.Add(new GradientStop(Theme.GoodC, 0));
                g.GradientStops.Add(new GradientStop(Theme.GoodC, Frac(-18)));
                g.GradientStops.Add(new GradientStop(Theme.WarnC, Frac(-9)));
                g.GradientStops.Add(new GradientStop(Theme.BadC, Frac(-2)));
                g.GradientStops.Add(new GradientStop(Theme.BadC, 1));
                g.Freeze();
                fill = g;
            }
            var inner = new Rect(bar.X + 3, 3, bar.Width - 6, h - 6);
            double f = Frac(Db);
            if (f > 0)
            {
                double top = inner.Bottom - inner.Height * f;
                dc.PushClip(new RectangleGeometry(new Rect(inner.X, top, inner.Width, inner.Bottom - top)));
                dc.DrawRoundedRectangle(fill, null, inner, 3, 3);
                dc.Pop();
            }
            // segmentos (visual de LED)
            for (double y = inner.Bottom - 4; y > inner.Top; y -= 4)
                dc.DrawLine(SegPen, new Point(inner.X, y), new Point(inner.Right, y));
            // pico
            double pf = Frac(PeakDb);
            if (pf > 0.01)
            {
                double py = inner.Bottom - inner.Height * pf;
                var pb = PeakDb > -2 ? Theme.Bad : PeakDb > -9 ? Theme.Warn : Paint.WellText;
                dc.DrawRectangle(pb, null, new Rect(inner.X, py - 1, inner.Width, 2));
            }
            if (ShowScale)
            {
                foreach (var t in new[] { 0, -6, -12, -24, -36, -48 })
                {
                    if (t < MinDb) continue;
                    double y = inner.Bottom - inner.Height * Frac(t);
                    var ft = Draw.Text(this, t.ToString(), 10, Theme.Dim);
                    dc.DrawText(ft, new Point(scaleW - 4 - ft.Width, y - ft.Height / 2));
                }
            }
        }
    }

    /// <summary>Barra horizontal com rotulo e valor (reducao de ganho, nivel etc.).</summary>
    public sealed class BarMeter : FrameworkElement
    {
        public string Label = "";
        public string ValueText = "";
        public double Fraction;
        public Brush BarBrush = Theme.Accent;
        public string Tip { set { ToolTip = value; } }
        static Brush Track => Paint.Track;

        public BarMeter() { Height = 34; }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            if (w < 20) return;
            Paint.Ensure();
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
            var lt = Draw.Text(this, Label, 11.5, Theme.Sub);
            dc.DrawText(lt, new Point(0, 0));
            var vt = Draw.Text(this, ValueText, 11.5, Theme.Text, true);
            dc.DrawText(vt, new Point(w - vt.Width, 0));
            var r = new Rect(0, 21, w, 6);
            dc.DrawRoundedRectangle(Track, null, r, 3, 3);
            double f = Math.Max(0, Math.Min(1, Fraction));
            if (f > 0.003) dc.DrawRoundedRectangle(BarBrush, null, new Rect(0, 21, Math.Max(6, w * f), 6), 3, 3);
        }
    }

    /// <summary>Botao giratorio (arraste para cima/baixo, roda do mouse, duplo clique = padrao).</summary>
    public sealed class Knob : FrameworkElement
    {
        public double Min, Max = 1, Value, Default;
        public string Label = "";
        public Func<double, string> Format = v => v.ToString("0");
        public event Action<double> ValueChanged;
        Point start; double startValue; bool dragging;
        static Pen TrackPen => Paint.TrackPen;
        static Pen ValuePen => Paint.ValuePen;

        public Knob() { Width = 112; Height = 122; Cursor = Cursors.SizeNS; Focusable = false; }

        double Frac => Max > Min ? (Value - Min) / (Max - Min) : 0;

        static Point Polar(Point c, double r, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        static Geometry Arc(Point c, double r, double fromDeg, double toDeg)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(Polar(c, r, fromDeg), false, false);
                ctx.ArcTo(Polar(c, r, toDeg), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            Paint.Ensure();
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
            double r = Math.Min(w, 88) / 2 - 6;
            var c = new Point(w / 2, r + 7);
            const double a0 = 135, sweep = 270;
            dc.DrawGeometry(null, TrackPen, Arc(c, r, a0, a0 + sweep - 0.01));
            double f = Math.Max(0, Math.Min(1, Frac));
            if (f > 0.004) dc.DrawGeometry(null, ValuePen, Arc(c, r, a0, a0 + sweep * f));
            var dot = Polar(c, r, a0 + sweep * f);
            dc.DrawEllipse(Brushes.White, null, dot, 5, 5);
            var vt = Draw.Text(this, Format(Value), 16, Theme.Text, true);
            dc.DrawText(vt, new Point(c.X - vt.Width / 2, c.Y - vt.Height / 2));
            var lt = Draw.Text(this, Label, 12, Theme.Sub);
            dc.DrawText(lt, new Point(c.X - lt.Width / 2, c.Y + r + 10));
        }

        void SetValue(double v, bool raise)
        {
            v = Math.Max(Min, Math.Min(Max, v));
            if (Math.Abs(v - Value) < 1e-9) return;
            Value = v;
            InvalidateVisual();
            if (raise) ValueChanged?.Invoke(v);
        }

        public void SetSilently(double v) { Value = Math.Max(Min, Math.Min(Max, v)); InvalidateVisual(); }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { SetValue(Default, true); e.Handled = true; return; }
            start = e.GetPosition(this); startValue = Value; dragging = true;
            CaptureMouse(); e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            var p = e.GetPosition(this);
            double sens = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 800 : 200;
            SetValue(startValue + (start.Y - p.Y) / sens * (Max - Min), true);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            dragging = false; ReleaseMouseCapture();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            SetValue(Value + Math.Sign(e.Delta) * (Max - Min) / 50.0, true);
            e.Handled = true;
        }
    }

    /// <summary>Analisador de espectro ao vivo + curva do equalizador com pontos arrastaveis.</summary>
    public sealed class SpectrumEq : FrameworkElement
    {
        public ChainParams Params;
        public event Action EqChanged;
        public float[] OutDb, InDb;    // dB por bin (FFT de 4096 a 48 kHz)
        public bool ShowInput = true;
        const double FMin = 30, FMax = 20000, SpecMin = -110, SpecMax = -10, EqRange = 18;
        int drag = -1, hover = -1;
        readonly Biquad scratch = new Biquad();
        static Pen Grid => Paint.Grid;
        static Pen Zero => Paint.Zero;
        static Pen InPen => Paint.InPen;
        static Pen OutPen => Paint.OutPen;
        static Brush OutFill => Paint.OutFill;
        static Pen EqPen => Paint.EqPen;
        static Brush EqFill => Paint.EqFill;
        static Brush Bg => Paint.Graph;

        public SpectrumEq() { ClipToBounds = true; Cursor = Cursors.Arrow; }

        double X(double f, double w) => w * Math.Log(f / FMin) / Math.Log(FMax / FMin);
        double F(double x, double w) => FMin * Math.Pow(FMax / FMin, Math.Max(0, Math.Min(1, x / w)));
        double SY(double db, double h) => h * (SpecMax - db) / (SpecMax - SpecMin);
        double EY(double db, double h) => h / 2 - db / EqRange * (h / 2 - 14);
        double EqDb(double y, double h) => (h / 2 - y) / (h / 2 - 14) * EqRange;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 50 || h < 50) return;
            Paint.Ensure();
            dc.DrawRoundedRectangle(Bg, null, new Rect(0, 0, w, h), 10, 10);
            // grade
            foreach (var f in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
            {
                double x = X(f, w);
                dc.DrawLine(Grid, new Point(x, 0), new Point(x, h));
                var t = Draw.Text(this, f >= 1000 ? (f / 1000) + "k" : f.ToString(), 10, Paint.WellDim);
                dc.DrawText(t, new Point(x + 3, h - t.Height - 3));
            }
            foreach (var db in new double[] { -12, -6, 6, 12 })
            {
                double y = EY(db, h);
                dc.DrawLine(Grid, new Point(0, y), new Point(w, y));
                var t = Draw.Text(this, (db > 0 ? "+" : "") + db + " dB", 9.5, Paint.WellDim);
                dc.DrawText(t, new Point(4, y - t.Height - 1));
            }
            dc.DrawLine(Zero, new Point(0, EY(0, h)), new Point(w, EY(0, h)));

            // espectros
            if (ShowInput && InDb != null) dc.DrawGeometry(null, InPen, SpecGeom(InDb, w, h, false));
            if (OutDb != null)
            {
                dc.DrawGeometry(OutFill, null, SpecGeom(OutDb, w, h, true));
                dc.DrawGeometry(null, OutPen, SpecGeom(OutDb, w, h, false));
            }

            // curva do EQ
            if (Params != null)
            {
                var g = new StreamGeometry();
                var gf = new StreamGeometry();
                using (var c = g.Open())
                using (var cf = gf.Open())
                {
                    cf.BeginFigure(new Point(0, EY(0, h)), true, true);
                    bool first = true;
                    for (double x = 0; x <= w; x += 3)
                    {
                        double db = Params.EqResponseDb(F(x, w), scratch);
                        var p = new Point(x, EY(Math.Max(-EqRange - 2, Math.Min(EqRange + 2, db)), h));
                        if (first) { c.BeginFigure(p, false, false); first = false; } else c.LineTo(p, true, true);
                        cf.LineTo(p, false, false);
                    }
                    cf.LineTo(new Point(w, EY(0, h)), false, false);
                }
                g.Freeze(); gf.Freeze();
                if (Params.EqOn) dc.DrawGeometry(EqFill, null, gf);
                dc.DrawGeometry(null, Params.EqOn ? EqPen : InPen, g);

                for (int i = 0; i < Params.Bands.Length; i++)
                {
                    var b = Params.Bands[i];
                    var p = new Point(X(b.Freq, w), EY(b.GainDb, h));
                    bool hot = i == hover || i == drag;
                    var fillB = !b.On || !Params.EqOn ? Paint.WellDim : hot ? Brushes.White : Paint.Accent;
                    dc.DrawEllipse(fillB, Draw.Pen(Brushes.White, hot ? 2 : 1.2), p, hot ? 9 : 7.5, hot ? 9 : 7.5);
                    var t = Draw.Text(this, (i + 1).ToString(), 10, hot ? Paint.Accent : Brushes.White, true);
                    dc.DrawText(t, new Point(p.X - t.Width / 2, p.Y - t.Height / 2));
                    if (hot)
                    {
                        string info = $"{b.Name}: {FormatHz(b.Freq)}  {(b.GainDb >= 0 ? "+" : "")}{b.GainDb:0.0} dB  Q {b.Q:0.00}";
                        var it = Draw.Text(this, info, 11.5, Paint.WellText, true);
                        double tx = Math.Min(Math.Max(6, p.X - it.Width / 2), w - it.Width - 6);
                        double ty = p.Y > 40 ? p.Y - 34 : p.Y + 16;
                        dc.DrawRoundedRectangle(Paint.Tip, null, new Rect(tx - 6, ty - 3, it.Width + 12, it.Height + 6), 5, 5);
                        dc.DrawText(it, new Point(tx, ty));
                    }
                }
            }
        }

        public static string FormatHz(double f) => f >= 1000 ? (f / 1000).ToString("0.0#") + " kHz" : f.ToString("0") + " Hz";

        Geometry SpecGeom(float[] db, double w, double h, bool closed)
        {
            int bins = db.Length;
            double binHz = 24000.0 / (bins - 1);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                bool first = true;
                double lastX = 0;
                for (double x = 0; x <= w; x += 2)
                {
                    double f0 = F(x, w), f1 = F(x + 2, w);
                    int k0 = (int)(f0 / binHz), k1 = Math.Max(k0, (int)(f1 / binHz));
                    float v = -200;
                    for (int k = k0; k <= k1 && k < bins; k++) if (db[k] > v) v = db[k];
                    double y = SY(Math.Max(SpecMin, Math.Min(SpecMax, v)), h);
                    var p = new Point(x, y);
                    if (first)
                    {
                        if (closed) { c.BeginFigure(new Point(0, h), true, true); c.LineTo(p, false, false); }
                        else c.BeginFigure(p, false, false);
                        first = false;
                    }
                    else c.LineTo(p, true, true);
                    lastX = x;
                }
                if (closed) c.LineTo(new Point(lastX, h), false, false);
            }
            g.Freeze();
            return g;
        }

        int HitTest(Point p)
        {
            if (Params == null) return -1;
            double w = ActualWidth, h = ActualHeight;
            int best = -1; double bestD = 14;
            for (int i = 0; i < Params.Bands.Length; i++)
            {
                var b = Params.Bands[i];
                double d = (new Point(X(b.Freq, w), EY(b.GainDb, h)) - p).Length;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            int i = HitTest(e.GetPosition(this));
            if (i < 0) return;
            if (e.ClickCount == 2)
            {
                Params.Bands[i].GainDb = 0; Params.Touch(); EqChanged?.Invoke(); InvalidateVisual();
                return;
            }
            drag = i; CaptureMouse(); e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (drag >= 0 && Params != null)
            {
                var b = Params.Bands[drag];
                double w = ActualWidth, h = ActualHeight;
                b.Freq = (float)Math.Round(Math.Max(30, Math.Min(18000, F(p.X, w))));
                b.GainDb = (float)Math.Round(Math.Max(-EqRange, Math.Min(EqRange, EqDb(p.Y, h))) * 2) / 2f;
                if (!b.On) b.On = true;
                Params.Touch();
                EqChanged?.Invoke();
                InvalidateVisual();
                return;
            }
            int hv = HitTest(p);
            if (hv != hover) { hover = hv; Cursor = hv >= 0 ? Cursors.Hand : Cursors.Arrow; InvalidateVisual(); }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (drag >= 0) { drag = -1; ReleaseMouseCapture(); InvalidateVisual(); }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (hover >= 0 && drag < 0) { hover = -1; InvalidateVisual(); }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            int i = drag >= 0 ? drag : HitTest(e.GetPosition(this));
            if (i < 0 || Params == null) return;
            var b = Params.Bands[i];
            b.Q = (float)Math.Max(0.2, Math.Min(8, b.Q * (e.Delta > 0 ? 1.12 : 1 / 1.12)));
            Params.Touch(); EqChanged?.Invoke(); InvalidateVisual();
            e.Handled = true;
        }
    }

    /// <summary>Forma de onda (original x tratado) da gravacao de teste.</summary>
    public sealed class WaveformView : FrameworkElement
    {
        public float[] A, B;
        public int Length;
        public double Progress = -1;
        public string LabelA = "Original", LabelB = "Com TakaVox";
        static Brush ABrush => Paint.WaveA;
        static Brush BBrush => Paint.WaveB;
        static Brush Bg => Paint.Graph;
        static Pen Mid => Paint.Mid;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 20 || h < 20) return;
            Paint.Ensure();
            dc.DrawRoundedRectangle(Bg, null, new Rect(0, 0, w, h), 10, 10);
            double laneH = h / 2;
            DrawLane(dc, A, 0, laneH, w, ABrush, LabelA);
            DrawLane(dc, B, laneH, laneH, w, BBrush, LabelB);
            if (Progress >= 0 && Progress <= 1)
            {
                double x = w * Progress;
                dc.DrawRectangle(Brushes.White, null, new Rect(x - 1, 0, 2, h));
            }
            if (A == null)
            {
                var t = Draw.Text(this, "Grave um teste para ver a diferença aqui", 13, Paint.WellDim);
                dc.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
            }
        }

        void DrawLane(DrawingContext dc, float[] d, double top, double hh, double w, Brush br, string label)
        {
            double mid = top + hh / 2;
            dc.DrawLine(Mid, new Point(0, mid), new Point(w, mid));
            if (d != null && Length > 0)
            {
                int len = Math.Min(Length, d.Length);
                int cols = (int)w;
                double per = (double)len / cols;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    for (int x = 0; x < cols; x++)
                    {
                        int a = (int)(x * per), b = Math.Min(len, (int)((x + 1) * per) + 1);
                        float mx = 0;
                        for (int i = a; i < b; i++) { float v = Math.Abs(d[i]); if (v > mx) mx = v; }
                        double amp = Math.Min(1, mx) * (hh / 2 - 6);
                        if (amp < 0.5) amp = 0.5;
                        c.BeginFigure(new Point(x + 0.5, mid - amp), false, false);
                        c.LineTo(new Point(x + 0.5, mid + amp), true, false);
                    }
                }
                g.Freeze();
                dc.DrawGeometry(null, Draw.Pen(br, 1), g);
            }
            var t = Draw.Text(this, label, 11, Paint.WellSub, true);
            dc.DrawText(t, new Point(10, top + 6));
        }
    }
}
