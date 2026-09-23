using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VolumeGuard.Audio;

namespace VolumeGuard.UI
{
    public static class Xaml
    {
        public static object Load(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("VolumeGuard.Xaml." + name))
            {
                if (s == null) throw new InvalidOperationException("Recurso XAML ausente: " + name);
                return XamlReader.Load(s);
            }
        }

        public static T Find<T>(FrameworkElement root, string name) where T : class
        {
            var o = root.FindName(name) as T;
            if (o == null) throw new InvalidOperationException("Elemento não encontrado: " + name);
            return o;
        }

        public static Stream Resource(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream("VolumeGuard." + name);
        }
    }

    public static class Fmt
    {
        public static readonly CultureInfo Br = new CultureInfo("pt-BR");
        const string Minus = "−";

        static string Signed(double v, string format)
        {
            string s = Math.Abs(v).ToString(format, Br);
            if (v < 0 && s.Trim('0', ',') != "") return Minus + s;
            return s;
        }

        /// <summary>Volume relativo em dB ("−20 dB", "−7,5 dB").</summary>
        public static string Db(double db) { return Signed(db, "0.#") + " dB"; }

        public static string Pct(double scalar)
        {
            double p = scalar * 100;
            if (p <= 0.0001) return "0%";
            if (p < 1) return p.ToString("0.0#", Br) + "%";
            if (p < 10) return p.ToString("0.#", Br) + "%";
            return p.ToString("0", Br) + "%";
        }

        public static string VolumeMain(double db, bool percent)
        {
            if (db <= AudioEngine.MinDb + 0.01) return percent ? "0%" : "mínimo";
            return percent ? Pct(AudioEngine.DbToScalar(db)) : Db(db);
        }

        public static string VolumeAlt(double db, bool percent)
        {
            if (db <= AudioEngine.MinDb + 0.01) return percent ? "−60 dB" : "0%";
            return percent ? Db(db) : Pct(AudioEngine.DbToScalar(db));
        }

        /// <summary>Nível absoluto estimado ("63 dB").</summary>
        public static string Spl(double spl)
        {
            if (double.IsInfinity(spl) || double.IsNaN(spl)) return "—";
            return Math.Round(spl).ToString("0", Br) + " dB";
        }

        public static string SplNumber(double spl)
        {
            if (double.IsInfinity(spl) || double.IsNaN(spl) || spl < 0) return "—";
            return Math.Round(spl).ToString("0", Br);
        }

        public static string Percent(double v)
        {
            if (v < 1 && v > 0) return "<1%";
            return Math.Round(v).ToString("0", Br) + "%";
        }

        public static double ParseNumber(string s, double fallback)
        {
            double v;
            s = (s ?? "").Trim().Replace(" ", "");
            if (double.TryParse(s, NumberStyles.Float, Br, out v)) return v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }
    }

    public static class Palette
    {
        static SolidColorBrush B(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public static readonly SolidColorBrush Page = B("#0F0F0E");
        public static readonly SolidColorBrush Surface = B("#1A1A19");
        public static readonly SolidColorBrush Raised = B("#262624");
        public static readonly SolidColorBrush Hairline = B("#2C2C2A");
        public static readonly SolidColorBrush Axis = B("#383835");
        public static readonly SolidColorBrush Track = B("#2E2E2B");
        public static readonly SolidColorBrush Ink = B("#FFFFFF");
        public static readonly SolidColorBrush InkSecondary = B("#C3C2B7");
        public static readonly SolidColorBrush Muted = B("#898781");
        public static readonly SolidColorBrush Accent = B("#3987E5");
        public static readonly SolidColorBrush AccentFill = B("#2E3987E5");
        public static readonly SolidColorBrush Good = B("#0CA30C");
        public static readonly SolidColorBrush Warning = B("#FAB219");
        public static readonly SolidColorBrush Serious = B("#EC835A");
        public static readonly SolidColorBrush Critical = B("#D03B3B");

        public static SolidColorBrush Zone(int zone)
        {
            switch (zone)
            {
                case 0: return Good;
                case 1: return Warning;
                case 2: return Serious;
                default: return Critical;
            }
        }

        /// <summary>Fundo discreto para chips de status (cor da zona sobre a superfície).</summary>
        public static SolidColorBrush ZoneTint(int zone)
        {
            switch (zone)
            {
                case 0: return B("#15260F");
                case 1: return B("#2B2410");
                case 2: return B("#2E1D15");
                default: return B("#331616");
            }
        }

        public static SolidColorBrush DoseBrush(double dose)
        {
            if (dose >= 100) return Critical;
            if (dose >= 50) return Warning;
            return Good;
        }
    }

    public static class Native
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetDefaultDllDirectories(int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr AddDllDirectory(string dir);
        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        /// <summary>
        /// DLLs carregadas depois disso só vêm do System32 e da pasta do .NET — nunca da pasta do .exe
        /// (protege contra uma DLL maliciosa deixada, por exemplo, na pasta Downloads).
        /// </summary>
        public static void HardenDllSearch()
        {
            try
            {
                string rt = RuntimeEnvironment.GetRuntimeDirectory();
                AddDllDirectory(rt);
                AddDllDirectory(Path.Combine(rt, "WPF"));
                SetDefaultDllDirectories(0x800 | 0x400); // SEARCH_SYSTEM32 | SEARCH_USER_DIRS
            }
            catch { }
        }

        /// <summary>Devolve ao Windows as páginas de memória que não estão em uso.</summary>
        public static void TrimWorkingSet()
        {
            try { SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1)); } catch { }
        }

        /// <summary>Barra de título escura (Windows 10 20H1+ / 11) e na cor da página no Windows 11.</summary>
        public static void DarkTitleBar(Window w)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                int caption = 0x000E0F0F; // COLORREF 0x00BBGGRR de #0F0F0E
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
            }
            catch { }
        }
    }

    public static class StartupManager
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "VolumeGuard";

        static string Command { get { return "\"" + Assembly.GetExecutingAssembly().Location + "\" --minimized"; } }

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                        return k != null && k.GetValue(ValueName) != null;
                }
                catch { return false; }
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) k.SetValue(ValueName, Command);
                else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName);
            }
        }

        /// <summary>
        /// Se o exe registrado não existe mais (mudou de pasta), aponta para este. Não mexe se o registrado
        /// ainda existe — senão abrir a versão portátil "roubaria" a inicialização da versão instalada.
        /// </summary>
        public static void RefreshPath()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    var v = k.GetValue(ValueName) as string;
                    if (v == null || v == Command) return;
                    string registered = v.StartsWith("\"") && v.IndexOf('"', 1) > 1 ? v.Substring(1, v.IndexOf('"', 1) - 1) : v;
                    if (!File.Exists(registered)) k.SetValue(ValueName, Command);
                }
            }
            catch { }
        }
    }

    public static class IconCache
    {
        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static ImageSource fallback;

        public static ImageSource Get(string path)
        {
            ImageSource img;
            if (path != null && cache.TryGetValue(path, out img)) return img;
            img = null;
            try
            {
                if (path != null && File.Exists(path))
                {
                    using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(path))
                    {
                        if (ico != null)
                        {
                            var bs = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            bs.Freeze();
                            img = bs;
                        }
                    }
                }
            }
            catch { }
            if (img == null) img = Fallback;
            if (path != null) cache[path] = img;
            return img;
        }

        static ImageSource Fallback
        {
            get
            {
                if (fallback == null)
                {
                    var g = new GeometryGroup();
                    g.Children.Add(Geometry.Parse("M3,9 H7 L12,4 V20 L7,15 H3 Z"));
                    g.Children.Add(Geometry.Parse("M15,8.5 A4.5,4.5 0 0 1 15,15.5"));
                    var d = new DrawingImage(new GeometryDrawing(Palette.InkSecondary, new Pen(Palette.InkSecondary, 1.6), g));
                    d.Freeze();
                    fallback = d;
                }
                return fallback;
            }
        }
    }
}
