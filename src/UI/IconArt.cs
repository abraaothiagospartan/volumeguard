using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace VolumeGuard.UI
{
    /// <summary>Desenho do ícone (medidor de 4 barras num quadrado arredondado). Usado no .exe e na bandeja.</summary>
    public static class IconArt
    {
        public static readonly Color Good = Color.FromArgb(0x0C, 0xA3, 0x0C);
        public static readonly Color Warning = Color.FromArgb(0xFA, 0xB2, 0x19);
        public static readonly Color Serious = Color.FromArgb(0xEC, 0x83, 0x5A);
        public static readonly Color Critical = Color.FromArgb(0xD0, 0x3B, 0x3B);
        public static readonly Color Dim = Color.FromArgb(0x4A, 0x4A, 0x46);

        public static Color[] AppColors { get { return new[] { Good, Good, Warning, Critical }; } }

        public static Bitmap Render(int size, Color[] bars)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size;
                float inset = size <= 20 ? 0 : s * 0.04f;
                var bg = new RectangleF(inset, inset, s - inset * 2, s - inset * 2);
                using (var path = Rounded(bg, s * 0.24f))
                using (var fill = new LinearGradientBrush(bg, Color.FromArgb(0x2A, 0x2A, 0x27), Color.FromArgb(0x16, 0x16, 0x15), 90f))
                {
                    g.FillPath(fill, path);
                    if (size >= 32)
                        using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255), Math.Max(1f, s / 64f)))
                            g.DrawPath(pen, path);
                }
                float padX = s * (size <= 20 ? 0.17f : 0.2f), padY = s * (size <= 20 ? 0.16f : 0.21f);
                float areaW = s - padX * 2, areaH = s - padY * 2;
                int n = bars.Length;
                float gap = areaW * (size <= 20 ? 0.14f : 0.12f);
                float bw = (areaW - gap * (n - 1)) / n;
                float[] heights = { 0.34f, 0.56f, 0.78f, 1.0f };
                for (int i = 0; i < n; i++)
                {
                    float h = areaH * heights[Math.Min(i, heights.Length - 1)];
                    var r = new RectangleF(padX + i * (bw + gap), padY + areaH - h, bw, h);
                    if (size <= 20) { r.X = (float)Math.Round(r.X); r.Width = Math.Max(2, (float)Math.Round(r.Width)); }
                    using (var b = new SolidBrush(bars[i]))
                    using (var p = Rounded(r, Math.Min(bw / 2, s * 0.06f)))
                        g.FillPath(b, p);
                }
            }
            return bmp;
        }

        /// <summary>Cores da bandeja: acende barras conforme a zona de nível.</summary>
        public static Color[] TrayColors(int zone, bool active, bool paused)
        {
            var c = new Color[4];
            Color lit = paused ? Color.FromArgb(0x89, 0x87, 0x81) : zone == 0 ? Good : zone == 1 ? Warning : zone == 2 ? Serious : Critical;
            int count = paused || !active ? 0 : zone + 1;
            for (int i = 0; i < 4; i++) c[i] = i < count ? lit : (paused ? Color.FromArgb(0x5A, 0x5A, 0x55) : Dim);
            if (!paused && !active) { c[0] = Good; }
            return c;
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Grava um .ico com várias resoluções (entradas PNG).</summary>
        public static void WriteIco(string path, int[] sizes, Color[] bars)
        {
            var pngs = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (var bmp = Render(sizes[i], bars))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    pngs[i] = ms.ToArray();
                }
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length);
                    w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }
    }
}
