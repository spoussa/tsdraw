using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace TSDraw
{
    static class Clock
    {
        static readonly Stopwatch Sw = Stopwatch.StartNew();
        public static long Ms { get { return Sw.ElapsedMilliseconds; } }
    }

    // A validated drawing message. Everything from the relay or the page goes through
    // FromDict, and only these fields are ever forwarded.
    class DrawMsg
    {
        public string T, Sid, From, Name, Id, C;
        public double W, Ar;
        public double[] P;
        public bool End, All;

        static readonly Regex ColorRe = new Regex("^#[0-9a-fA-F]{6}$");
        static readonly Regex IdRe = new Regex("^[A-Za-z0-9_-]{1,32}$");

        public static DrawMsg FromDict(Dictionary<string, object> d)
        {
            var m = new DrawMsg { T = Json.S1(d, "t"), Sid = Json.S1(d, "sid") };
            if ((m.T != "stroke" && m.T != "clear" && m.T != "here") || string.IsNullOrEmpty(m.Sid) || m.Sid.Length > 200) return null;
            m.From = Clip(Json.S1(d, "from"), 32);
            m.Name = Clip(Json.S1(d, "name"), 32);
            m.All = Json.B(d, "all");
            if (m.T == "clear" || m.T == "here") return m;
            m.Id = Json.S1(d, "id");
            if (m.Id == null || !IdRe.IsMatch(m.Id)) return null;
            m.C = Json.S1(d, "c");
            if (m.C == null || !ColorRe.IsMatch(m.C)) m.C = "#ff3b30";
            m.W = Math.Max(0.001, Math.Min(0.05, Json.D(d, "w", 0.005)));
            m.Ar = Math.Max(0, Math.Min(10, Json.D(d, "ar", 0)));
            m.End = Json.B(d, "end");
            var raw = Json.Arr(d, "p");
            int n = Math.Min(raw.Length / 2 * 2, 4000);
            m.P = new double[n];
            for (int i = 0; i < n; i++)
            {
                double v;
                try { v = Convert.ToDouble(raw[i], CultureInfo.InvariantCulture); } catch { return null; }
                if (double.IsNaN(v)) return null;
                m.P[i] = Math.Max(0, Math.Min(1, v));
            }
            return m;
        }

        static string Clip(string s, int max)
        {
            if (s == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s.Trim()) if (!char.IsControl(ch)) sb.Append(ch);
            s = sb.ToString();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object> { { "t", T }, { "sid", Sid }, { "from", From ?? "" }, { "name", Name ?? "" } };
            if (T == "here") return d;
            if (T == "clear") { d["all"] = All; return d; }
            d["id"] = Id; d["c"] = C; d["w"] = Math.Round(W, 5); d["end"] = End; d["ar"] = Math.Round(Ar, 4);
            d["p"] = P.Select(v => Math.Round(v, 4)).ToArray();
            return d;
        }
    }

    class Stroke
    {
        public string Key, From, Name, Id;
        public Color Color;
        public float Width;
        public readonly List<PointF> Pts = new List<PointF>();
        public long Last;
        public bool Done, Own;
    }

    class StrokeSet
    {
        readonly Dictionary<string, Stroke> map = new Dictionary<string, Stroke>();
        public int HoldMs = 3000, FadeMs = 700;

        public IEnumerable<Stroke> All { get { return map.Values; } }

        public void Add(Stroke s) { map[s.Key] = s; }

        public void Apply(DrawMsg m, string ownId)
        {
            if (m.T == "clear")
            {
                foreach (var k in map.Where(kv => m.All || kv.Value.From == m.From).Select(kv => kv.Key).ToList()) map.Remove(k);
                return;
            }
            string key = m.From + ":" + m.Id;
            Stroke s;
            if (!map.TryGetValue(key, out s))
            {
                s = new Stroke { Key = key, From = m.From, Name = m.Name, Id = m.Id, Color = ColorTranslator.FromHtml(m.C), Width = (float)m.W, Own = m.From == ownId };
                map[key] = s;
            }
            for (int i = 0; i + 1 < m.P.Length; i += 2) s.Pts.Add(new PointF((float)m.P[i], (float)m.P[i + 1]));
            s.Last = Clock.Ms;
            if (m.End) s.Done = true;
        }

        public void ClearOwn() { foreach (var k in map.Where(kv => kv.Value.Own).Select(kv => kv.Key).ToList()) map.Remove(k); }

        public float Alpha(Stroke s, long now)
        {
            if (!s.Done) return 1;
            long age = now - s.Last;
            if (age <= HoldMs) return 1;
            return Math.Max(0, 1 - (age - HoldMs) / (float)FadeMs);
        }

        public bool Prune(long now)
        {
            foreach (var k in map.Where(kv => Alpha(kv.Value, now) <= 0).Select(kv => kv.Key).ToList()) map.Remove(k);
            return map.Count > 0;
        }
    }

    // The overlay is a colour-keyed window (see OverlayWindow), so everything is drawn
    // opaque: strokes get a dark outline for contrast and fade out by thinning.
    static class Renderer
    {
        static readonly Color Outline = Color.FromArgb(24, 24, 28);

        public static void Draw(Graphics g, StrokeSet set, int w, int h, long now, bool drawMode, Color modeColor, string hotkey)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var latest = new Dictionary<string, Stroke>();
            foreach (var s in set.All)
            {
                float a = set.Alpha(s, now);
                if (a <= 0 || s.Pts.Count == 0) continue;
                float lw = Math.Max(2f, s.Width * h) * (0.15f + 0.85f * a);
                float ow = lw + Math.Max(2f, lw * 0.4f);
                var pts = s.Pts.Select(p => new PointF(p.X * w, p.Y * h)).ToArray();
                if (pts.Length == 1)
                {
                    using (var ob = new SolidBrush(Outline)) g.FillEllipse(ob, pts[0].X - ow / 2, pts[0].Y - ow / 2, ow, ow);
                    using (var b = new SolidBrush(s.Color)) g.FillEllipse(b, pts[0].X - lw / 2, pts[0].Y - lw / 2, lw, lw);
                }
                else
                {
                    using (var outline = RoundPen(Outline, ow)) g.DrawLines(outline, pts);
                    using (var pen = RoundPen(s.Color, lw)) g.DrawLines(pen, pts);
                }
                if (!s.Own && s.Name.Length > 0 && a > 0.4f) latest[s.From] = s;
            }
            foreach (var s in latest.Values) DrawLabel(g, s, w, h);
            if (drawMode) DrawModeFrame(g, w, h, modeColor, hotkey);
        }

        static Pen RoundPen(Color c, float width)
        {
            return new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        }

        static void DrawLabel(Graphics g, Stroke s, int w, int h)
        {
            var last = s.Pts[s.Pts.Count - 1];
            float x = last.X * w, y = last.Y * h;
            float px = Math.Max(12f, h / 85f);
            using (var font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var size = g.MeasureString(s.Name, font);
                float pad = px * 0.45f, bw = size.Width + pad * 2, bh = size.Height + pad * 0.6f;
                float lx = x + px * 0.8f, ly = y + px * 0.6f;
                if (lx + bw > w) lx = x - bw - px * 0.8f;
                if (ly + bh > h) ly = y - bh - px * 0.6f;
                using (var path = RoundRect(lx, ly, bw, bh, px * 0.35f))
                using (var bg = new SolidBrush(s.Color))
                using (var fg = new SolidBrush(IsLight(s.Color) ? Color.FromArgb(17, 17, 17) : Color.White))
                {
                    g.FillPath(bg, path);
                    g.DrawString(s.Name, font, fg, lx + pad, ly + pad * 0.3f);
                }
            }
        }

        static void DrawModeFrame(Graphics g, int w, int h, Color c, string hotkey)
        {
            using (var pen = new Pen(c, 4)) g.DrawRectangle(pen, 2, 2, w - 4, h - 4);
            string text = "Drawing on your screen - " + hotkey + " or Esc to stop, right-click to clear";
            float px = Math.Max(13f, h / 75f);
            using (var font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var size = g.MeasureString(text, font);
                float bw = size.Width + px * 1.2f, bh = size.Height + px * 0.5f, x = (w - bw) / 2, y = px;
                using (var path = RoundRect(x, y, bw, bh, bh / 2))
                using (var bg = new SolidBrush(Color.FromArgb(20, 24, 33)))
                using (var fg = new SolidBrush(Color.White))
                {
                    g.FillPath(bg, path);
                    g.DrawString(text, font, fg, x + px * 0.6f, y + px * 0.25f);
                }
            }
        }

        static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = Math.Min(2 * r, Math.Min(w, h));
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static bool IsLight(Color c) { return (c.R * 299 + c.G * 587 + c.B * 114) / 1000 > 150; }
    }

    // Borderless, topmost, click-through window over the captured area, excluded from
    // screen capture so the drawings don't end up in the stream (viewers draw them
    // locally). It uses colour-key transparency rather than per-pixel alpha because
    // Windows only honours capture exclusion for SetLayeredWindowAttributes-style
    // layered windows: UpdateLayeredWindow windows either fail (error 8) or turn the
    // whole captured area black.
    class OverlayWindow : NativeWindow, IDisposable
    {
        public static readonly Color Key = Color.FromArgb(1, 0, 1);
        IntPtr screenDC, memDC, hBmp, oldBmp;
        Bitmap bmp;
        Rectangle bounds;
        bool shown;

        public OverlayWindow(bool excludeFromCapture)
        {
            CreateHandle(new CreateParams
            {
                Caption = "TSDraw overlay", X = 0, Y = 0, Width = 1, Height = 1, Style = Native.WS_POPUP,
                ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                          Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
            });
            screenDC = Native.GetDC(IntPtr.Zero);
            Native.SetLayeredWindowAttributes(Handle, (uint)(Key.R | (Key.G << 8) | (Key.B << 16)), 0, 1 /* LWA_COLORKEY */);
            if (excludeFromCapture) Native.ExcludeFromCapture(Handle, "overlay");
        }

        public void SetBounds(Rectangle r)
        {
            if (r == bounds) return;
            if (r.Size != bounds.Size) CreateSurface(r.Width, r.Height);
            bounds = r;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, Native.SWP_NOACTIVATE);
        }

        void CreateSurface(int w, int h)
        {
            FreeSurface();
            var bi = new Native.BITMAPINFOHEADER
            {
                biSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
                biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
            };
            IntPtr bits;
            hBmp = Native.CreateDIBSection(screenDC, ref bi, 0, out bits, IntPtr.Zero, 0);
            memDC = Native.CreateCompatibleDC(screenDC);
            oldBmp = Native.SelectObject(memDC, hBmp);
            bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppRgb, bits);
        }

        void FreeSurface()
        {
            if (bmp != null) { bmp.Dispose(); bmp = null; }
            if (memDC != IntPtr.Zero) { Native.SelectObject(memDC, oldBmp); Native.DeleteDC(memDC); memDC = IntPtr.Zero; }
            if (hBmp != IntPtr.Zero) { Native.DeleteObject(hBmp); hBmp = IntPtr.Zero; }
        }

        public void Render(Action<Graphics> draw)
        {
            if (bmp == null) return;
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Key);
                draw(g);
            }
            Blit();
            if (!shown)
            {
                Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                shown = true;
                Blit();
            }
        }

        void Blit()
        {
            IntPtr dc = Native.GetDC(Handle);
            Native.BitBlt(dc, 0, 0, bounds.Width, bounds.Height, memDC, 0, 0, 0x00CC0020 /* SRCCOPY */);
            Native.ReleaseDC(Handle, dc);
        }

        public void Hide()
        {
            if (!shown) return;
            Native.ShowWindow(Handle, Native.SW_HIDE);
            shown = false;
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_ERASEBKGND: m.Result = (IntPtr)1; return;
                case Native.WM_PAINT:
                    Native.PAINTSTRUCT ps;
                    IntPtr dc = Native.BeginPaint(Handle, out ps);
                    if (memDC != IntPtr.Zero) Native.BitBlt(dc, 0, 0, bounds.Width, bounds.Height, memDC, 0, 0, 0x00CC0020);
                    Native.EndPaint(Handle, ref ps);
                    return;
                case Native.WM_MOUSEACTIVATE: m.Result = (IntPtr)Native.MA_NOACTIVATE; return;
                case Native.WM_DPICHANGED: m.Result = IntPtr.Zero; return;   // keep our physical-pixel bounds
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            FreeSurface();
            if (screenDC != IntPtr.Zero) { Native.ReleaseDC(IntPtr.Zero, screenDC); screenDC = IntPtr.Zero; }
            DestroyHandle();
        }
    }

    // Nearly invisible (alpha 1/255) window that catches the mouse while the streamer
    // draws on their own screen; the overlay itself is click-through.
    class InputWindow : NativeWindow, IDisposable
    {
        public event Action<int, int> MouseDown, MouseMove, MouseUp;
        public event Action RightClick;
        bool shown;

        public InputWindow(bool excludeFromCapture)
        {
            CreateHandle(new CreateParams
            {
                Caption = "TSDraw input", X = 0, Y = 0, Width = 1, Height = 1, Style = Native.WS_POPUP,
                ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
            });
            Native.SetLayeredWindowAttributes(Handle, 0, 1, 2 /* LWA_ALPHA */);
            if (excludeFromCapture) Native.ExcludeFromCapture(Handle, "input layer");
        }

        public void Show(Rectangle r)
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            shown = true;
        }

        public void Hide()
        {
            if (!shown) return;
            Native.ReleaseCapture();
            Native.ShowWindow(Handle, Native.SW_HIDE);
            shown = false;
        }

        protected override void WndProc(ref Message m)
        {
            int x = (short)((long)m.LParam & 0xffff), y = (short)(((long)m.LParam >> 16) & 0xffff);
            switch (m.Msg)
            {
                case Native.WM_MOUSEACTIVATE: m.Result = (IntPtr)Native.MA_NOACTIVATE; return;
                case Native.WM_DPICHANGED: m.Result = IntPtr.Zero; return;
                case Native.WM_SETCURSOR:
                    Native.SetCursor(Native.LoadCursor(IntPtr.Zero, 32515 /* IDC_CROSS */));
                    m.Result = (IntPtr)1;
                    return;
                case Native.WM_LBUTTONDOWN:
                    Native.SetCapture(Handle);
                    if (MouseDown != null) MouseDown(x, y);
                    return;
                case Native.WM_MOUSEMOVE:
                    if (MouseMove != null) MouseMove(x, y);
                    return;
                case Native.WM_LBUTTONUP:
                    Native.ReleaseCapture();
                    if (MouseUp != null) MouseUp(x, y);
                    return;
                case Native.WM_RBUTTONUP:
                    if (RightClick != null) RightClick();
                    return;
            }
            base.WndProc(ref m);
        }

        public void Dispose() { DestroyHandle(); }
    }

    // Where on this PC the stream's pixels come from.
    static class Targets
    {
        public const int SCREEN = 2, WINDOW = 3;

        public static Rectangle Resolve(int type, string srcId, string title, double aspect)
        {
            if (type == WINDOW)
            {
                IntPtr h = IntPtr.Zero;
                long v;
                if (long.TryParse(srcId, out v) && v != 0 && Native.IsWindow(new IntPtr(v))) h = new IntPtr(v);
                if (h == IntPtr.Zero && !string.IsNullOrEmpty(title)) h = Native.FindTopLevelWindow(title);
                if (h == IntPtr.Zero || Native.IsIconic(h)) return Rectangle.Empty;
                var r = Native.WindowFrame(h);
                return r.HasValue ? r.Value : Rectangle.Empty;
            }

            // WebRTC numbers screens by their EnumDisplayDevices index.
            var screens = Screen.AllScreens;
            Screen pick = null;
            int idx;
            if (int.TryParse(srcId, out idx))
            {
                string dev;
                if (Native.DisplayDevicesByIndex().TryGetValue(idx, out dev))
                    pick = screens.FirstOrDefault(s => string.Equals(s.DeviceName, dev, StringComparison.OrdinalIgnoreCase));
            }
            if (aspect > 0 && (pick == null || !SameAspect(pick.Bounds, aspect)))
            {
                var m = screens.Where(s => SameAspect(s.Bounds, aspect)).ToList();
                if (m.Count == 1) pick = m[0];
            }
            if (pick == null) pick = screens.Length == 1 ? screens[0] : Screen.PrimaryScreen;
            return pick.Bounds;
        }

        static bool SameAspect(Rectangle r, double aspect)
        {
            return r.Height > 0 && Math.Abs(r.Width / (double)r.Height - aspect) / aspect < 0.03;
        }
    }

    // One per stream this PC is sending: tracks the captured area and shows what viewers draw.
    class OverlayController : IDisposable
    {
        public readonly string Sid;
        int srcType;
        string srcId, title;
        readonly App app;
        readonly StrokeSet strokes = new StrokeSet();
        readonly OverlayWindow win;
        readonly InputWindow input;
        readonly Timer timer = new Timer { Interval = 16 };
        Rectangle target;
        long lastResolve = -100000, lastFlush;
        double streamAspect;
        bool drawMode;
        Stroke cur;
        readonly List<double> pending = new List<double>();

        public OverlayController(App app, string sid)
        {
            this.app = app;
            Sid = sid;
            strokes.HoldMs = (int)(app.Cfg.FadeSeconds * 1000);
            win = new OverlayWindow(app.Cfg.ExcludeOverlayFromCapture);
            input = new InputWindow(app.Cfg.ExcludeOverlayFromCapture);
            input.MouseDown += OnDown;
            input.MouseMove += OnMove;
            input.MouseUp += (x, y) => EndStroke();
            input.RightClick += ClearOwn;
            timer.Tick += (s, e) => Tick();
        }

        public bool DrawMode { get { return drawMode; } }
        public Rectangle Target { get { return target; } }
        public string Describe()
        {
            return srcType == Targets.WINDOW ? "window \"" + title + "\"" : "screen " + srcId;
        }

        public void UpdateSource(int type, string id, string ttl)
        {
            if (type == srcType && id == srcId && ttl == title) return;
            srcType = type; srcId = id; title = ttl;
            lastResolve = -100000;
            Log.W("stream {0}: capturing {1}", Short(Sid), Describe());
        }

        public void Apply(DrawMsg m)
        {
            if (m.Ar > 0 && Math.Abs(m.Ar - streamAspect) > 0.001) { streamAspect = m.Ar; lastResolve = -100000; }
            strokes.Apply(m, app.InstanceId);
            Wake();
        }

        void Wake()
        {
            if (!timer.Enabled) { timer.Start(); Tick(); }
        }

        void Tick()
        {
            long now = Clock.Ms;
            if (now - lastResolve > 500)
            {
                lastResolve = now;
                var t = Targets.Resolve(srcType, srcId, title, streamAspect);
                if (t != target) { Log.W("overlay for {0} -> {1}", Short(Sid), t); target = t; }
            }
            if (drawMode) Flush(false);
            bool any = strokes.Prune(now);
            if (target.Width <= 0 || target.Height <= 0 || (!any && !drawMode))
            {
                win.Hide();
                input.Hide();
                if (!any && !drawMode) timer.Stop();
                return;
            }
            win.SetBounds(target);
            if (drawMode) input.Show(target); else input.Hide();
            win.Render(g => Renderer.Draw(g, strokes, target.Width, target.Height, now, drawMode, app.DrawColor, app.Cfg.DrawHotkey));
        }

        public void SetDrawMode(bool on)
        {
            if (on == drawMode) return;
            drawMode = on;
            if (!on && cur != null) EndStroke();
            if (!on) input.Hide();
            lastResolve = -100000;
            if (timer.Enabled) Tick(); else Wake();
        }

        void OnDown(int x, int y)
        {
            if (!drawMode) return;
            cur = new Stroke
            {
                Id = RoomKeys.RandomId(6), From = app.InstanceId, Name = app.Cfg.Name, Own = true,
                Color = app.DrawColor, Width = 0.006f, Last = Clock.Ms,
            };
            cur.Key = "self:" + cur.Id;
            strokes.Add(cur);
            pending.Clear();
            AddPoint(x, y);
            Tick();
        }

        void OnMove(int x, int y)
        {
            if (cur == null) return;
            AddPoint(x, y);
        }

        void AddPoint(int x, int y)
        {
            if (target.Width <= 0 || target.Height <= 0) return;
            float nx = Math.Max(0, Math.Min(1, x / (float)target.Width)), ny = Math.Max(0, Math.Min(1, y / (float)target.Height));
            var p = new PointF((float)Math.Round(nx, 4), (float)Math.Round(ny, 4));
            if (cur.Pts.Count > 0 && cur.Pts[cur.Pts.Count - 1] == p) return;
            cur.Pts.Add(p);
            cur.Last = Clock.Ms;
            pending.Add(p.X); pending.Add(p.Y);
        }

        void EndStroke()
        {
            if (cur == null) return;
            cur.Done = true;
            cur.Last = Clock.Ms;
            Flush(true);
            cur = null;
        }

        void Flush(bool end)
        {
            if (cur == null || (pending.Count == 0 && !end)) return;
            if (!end && Clock.Ms - lastFlush < 40) return;
            lastFlush = Clock.Ms;
            app.PublishOwn(new DrawMsg
            {
                T = "stroke", Sid = Sid, Id = cur.Id, C = string.Format("#{0:x2}{1:x2}{2:x2}", cur.Color.R, cur.Color.G, cur.Color.B),
                W = cur.Width, P = pending.ToArray(), End = end,
                Ar = target.Height > 0 ? target.Width / (double)target.Height : 0,
            });
            pending.Clear();
        }

        public void ClearOwn()
        {
            strokes.ClearOwn();
            app.PublishOwn(new DrawMsg { T = "clear", Sid = Sid });
            Wake();
        }

        public static string Short(string sid) { return sid.Length > 8 ? sid.Substring(0, 8) : sid; }

        public void Dispose()
        {
            timer.Stop();
            timer.Dispose();
            win.Dispose();
            input.Dispose();
        }
    }
}
