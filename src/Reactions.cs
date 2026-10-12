using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TSDraw
{
    // Frames of one reaction image. The streamer's TeamSpeak page decodes the original
    // (GIF, animated WebP, AVIF...) with Chromium and sends them as one PNG sprite sheet, so
    // TSDraw.exe never parses images that came from other people.
    class ReactionAsset
    {
        public readonly List<Bitmap> Frames = new List<Bitmap>();
        public int[] Delays;
        public int TotalMs;

        public int FrameAt(long ms)
        {
            if (Frames.Count == 1 || TotalMs <= 0) return 0;
            long t = ms % TotalMs;
            for (int i = 0; i < Delays.Length; i++)
            {
                if (t < Delays[i]) return i;
                t -= Delays[i];
            }
            return Frames.Count - 1;
        }

        public void Dispose() { foreach (var f in Frames) f.Dispose(); }
    }

    class Reaction
    {
        public string Key;
        public float X, Y, S;
        public long Created, Start = -1;   // Start: when its frames were available and it began to show
    }

    static class ReactionAssets
    {
        public const int ReactMs = 4000;
        static readonly Regex KeyRe = new Regex("^(7tv:[0-9A-Za-z]{20,32}|img:[0-9a-f]{64})$");
        static readonly Dictionary<string, ReactionAsset> Assets = new Dictionary<string, ReactionAsset>();
        static readonly LinkedList<string> AssetOrder = new LinkedList<string>();
        static readonly Dictionary<string, List<Bitmap>> Scaled = new Dictionary<string, List<Bitmap>>();
        static readonly LinkedList<string> ScaledOrder = new LinkedList<string>();

        public static string KeyOf(DrawMsg m) { return (m.K == "7tv" ? "7tv:" : "img:") + m.Ref; }

        public static ReactionAsset Get(string key)
        {
            ReactionAsset a;
            return Assets.TryGetValue(key, out a) ? a : null;
        }

        // {t:"asset", key, w, h, n, cols, delays:[ms], png:<base64 sprite sheet>} from the page.
        public static void Add(Dictionary<string, object> d)
        {
            string key = Json.S1(d, "key"), png = Json.S1(d, "png");
            int w = (int)Json.D(d, "w", 0), h = (int)Json.D(d, "h", 0), n = (int)Json.D(d, "n", 0), cols = (int)Json.D(d, "cols", 0);
            var delays = Json.Arr(d, "delays");
            if (key == null || !KeyRe.IsMatch(key) || png == null || png.Length > 16 * 1024 * 1024) return;
            if (w < 1 || w > 512 || h < 1 || h > 512 || n < 1 || n > 64 || cols < 1 || cols > n || delays.Length != n) return;
            var a = new ReactionAsset { Delays = new int[n] };
            try
            {
                using (var ms = new MemoryStream(Convert.FromBase64String(png)))
                using (var sheet = new Bitmap(ms))
                {
                    if (sheet.Width < cols * w || sheet.Height < (n + cols - 1) / cols * h) return;
                    for (int i = 0; i < n; i++)
                    {
                        a.Frames.Add(sheet.Clone(new Rectangle(i % cols * w, i / cols * h, w, h), PixelFormat.Format32bppArgb));
                        a.Delays[i] = Math.Max(0, Math.Min(10000, (int)Convert.ToDouble(delays[i])));
                    }
                }
            }
            catch (Exception e)
            {
                Log.W("reaction image {0} unusable: {1}", key, e.Message);
                a.Dispose();
                return;
            }
            a.TotalMs = a.Delays.Sum();
            Remove(key);
            Assets[key] = a;
            AssetOrder.AddLast(key);
            while (AssetOrder.Count > 40) Remove(AssetOrder.First.Value);
        }

        static void Remove(string key)
        {
            ReactionAsset old;
            if (!Assets.TryGetValue(key, out old)) return;
            Assets.Remove(key);
            AssetOrder.Remove(key);
            foreach (var k in Scaled.Keys.Where(k => k.StartsWith(key + "@")).ToList()) DropScaled(k);
            old.Dispose();
        }

        // Frames resized to the on-screen height once, with hard-edged alpha: the overlay window
        // uses colour-key transparency, where half-transparent pixels would turn into dark fringes.
        public static List<Bitmap> ScaledFrames(string key, ReactionAsset a, int px)
        {
            string k = key + "@" + px;
            List<Bitmap> list;
            if (Scaled.TryGetValue(k, out list)) { ScaledOrder.Remove(k); ScaledOrder.AddLast(k); return list; }
            list = a.Frames.Select(f => ScaleHardAlpha(f, px)).ToList();
            Scaled[k] = list;
            ScaledOrder.AddLast(k);
            while (ScaledOrder.Count > 16) DropScaled(ScaledOrder.First.Value);
            return list;
        }

        static void DropScaled(string k)
        {
            List<Bitmap> list;
            if (!Scaled.TryGetValue(k, out list)) return;
            Scaled.Remove(k);
            ScaledOrder.Remove(k);
            foreach (var b in list) b.Dispose();
        }

        static Bitmap ScaleHardAlpha(Bitmap src, int h)
        {
            int w = Math.Max(1, (int)Math.Round(src.Width * (h / (double)src.Height)));
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (var ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);   // no dark halo at the borders
                    g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }
            var data = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var buf = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int i = 3; i < buf.Length; i += 4)
            {
                if (buf[i] < 110) { buf[i] = 0; buf[i - 1] = 0; buf[i - 2] = 0; buf[i - 3] = 0; }
                else buf[i] = 255;
            }
            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            dst.UnlockBits(data);
            return dst;
        }

        // Pop in, float up a little, then shrink away (the colour-keyed overlay can't fade).
        public static void Draw(Graphics g, List<Reaction> reacts, int w, int h, long now)
        {
            var oldInterp = g.InterpolationMode;
            var oldOffset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;   // keeps the hard alpha edges while popping
            g.PixelOffsetMode = PixelOffsetMode.Half;
            foreach (var r in reacts)
            {
                if (r.Start < 0) continue;
                var a = Get(r.Key);
                if (a == null) continue;
                int px = Math.Max(8, Math.Min(1024, (int)(r.S * h)));
                var frames = ScaledFrames(r.Key, a, px);
                long age = now - r.Start;
                float p = Math.Min(1f, age / (float)ReactMs);
                float scale = p < 0.07f ? 0.3f + 0.85f * (p / 0.07f)
                            : p < 0.13f ? 1.15f - 0.15f * ((p - 0.07f) / 0.06f)
                            : p > 0.82f ? 1f - 0.45f * ((p - 0.82f) / 0.18f) : 1f;
                float rise = p < 0.82f ? 0.12f * (p / 0.82f) : 0.12f + 0.18f * ((p - 0.82f) / 0.18f);
                var f = frames[a.FrameAt(age)];
                float dw = f.Width * scale, dh = f.Height * scale;
                float cx = r.X * w, cy = r.Y * h - rise * px;
                g.DrawImage(f, new RectangleF(cx - dw / 2, cy - dh / 2, dw, dh));
            }
            g.InterpolationMode = oldInterp;
            g.PixelOffsetMode = oldOffset;
        }
    }
}
