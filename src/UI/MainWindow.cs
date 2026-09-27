using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VolumeGuard.Audio;
using VolumeGuard.Core;

namespace VolumeGuard.UI
{
    public sealed partial class MainWindow : Window
    {
        readonly AudioEngine engine;
        readonly Grid host;
        readonly RadioButton navNow, navApps, navHistory, navSettings;
        readonly TextBlock headerStatus;
        readonly Ellipse statusDot;
        FrameworkElement pageNow, pageApps, pageHistory, pageSettings;
        string currentPage = "now";
        EngineSnapshot snap;

        public MainWindow(AudioEngine e)
        {
            engine = e;
            Title = "VolumeGuard";
            Width = 1060; Height = 780; MinWidth = 920; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Palette.Page;
            Foreground = Palette.Ink;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            try { Icon = BitmapFrame.Create(Xaml.Resource("app.ico")); } catch { }

            var shell = (FrameworkElement)Xaml.Load("Shell.xaml");
            Content = shell;
            host = Xaml.Find<Grid>(shell, "PageHost");
            navNow = Xaml.Find<RadioButton>(shell, "NavNow");
            navApps = Xaml.Find<RadioButton>(shell, "NavApps");
            navHistory = Xaml.Find<RadioButton>(shell, "NavHistory");
            navSettings = Xaml.Find<RadioButton>(shell, "NavSettings");
            headerStatus = Xaml.Find<TextBlock>(shell, "HeaderStatus");
            statusDot = Xaml.Find<Ellipse>(shell, "StatusDot");
            try { Xaml.Find<Image>(shell, "Logo").Source = LargestIconFrame(); } catch { }

            pageNow = AddPage("PageNow.xaml");
            pageApps = AddPage("PageApps.xaml");
            pageHistory = AddPage("PageHistory.xaml");
            pageSettings = AddPage("PageSettings.xaml");
            InitNow();
            InitApps();
            InitHistory();
            InitSettings();

            navNow.Checked += (s, a) => ShowPage("now");
            navApps.Checked += (s, a) => ShowPage("apps");
            navHistory.Checked += (s, a) => ShowPage("history");
            navSettings.Checked += (s, a) => ShowPage("settings");
            ShowPage(SettingsStore.Current.StartPage ?? "now");

            SourceInitialized += (s, a) => Native.DarkTitleBar(this);
        }

        static BitmapSource LargestIconFrame()
        {
            var dec = BitmapDecoder.Create(Xaml.Resource("app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return dec.Frames.OrderByDescending(f => f.PixelWidth).First();
        }

        FrameworkElement AddPage(string name)
        {
            var p = (FrameworkElement)Xaml.Load(name);
            p.Visibility = Visibility.Collapsed;
            host.Children.Add(p);
            return p;
        }

        public void ShowPage(string name)
        {
            currentPage = name;
            pageNow.Visibility = name == "now" ? Visibility.Visible : Visibility.Collapsed;
            pageApps.Visibility = name == "apps" ? Visibility.Visible : Visibility.Collapsed;
            pageHistory.Visibility = name == "history" ? Visibility.Visible : Visibility.Collapsed;
            pageSettings.Visibility = name == "settings" ? Visibility.Visible : Visibility.Collapsed;
            var nav = name == "apps" ? navApps : name == "history" ? navHistory : name == "settings" ? navSettings : navNow;
            if (nav.IsChecked != true) nav.IsChecked = true;
            if (name == "history") RefreshHistory();
            if (name == "apps") RefreshSaved();
            if (name == "settings") LoadSettingsToUi();
            if (snap != null) Deliver(snap);
        }

        public void ShowAndActivate(string page)
        {
            if (page != null) ShowPage(page);
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        public void OnSnapshot(EngineSnapshot s)
        {
            snap = s;
            UpdateHeader(s);
            if (!IsVisible || WindowState == WindowState.Minimized) return;
            Deliver(s);
        }

        void Deliver(EngineSnapshot s)
        {
            switch (currentPage)
            {
                case "now": UpdateNow(s); break;
                case "apps": UpdateApps(s); break;
                case "history": TickHistory(); break;
                case "settings": UpdateSettingsLive(s); break;
            }
        }

        void UpdateHeader(EngineSnapshot s)
        {
            string text;
            Brush dot;
            if (!s.HasDevice) { text = s.Error ?? "Procurando saída de áudio…"; dot = Palette.Critical; }
            else if (s.Paused) { text = "Proteção pausada até " + s.PausedUntil.ToString("HH:mm") + " · " + s.DeviceName; dot = Palette.Muted; }
            else if (s.Error != null) { text = s.Error; dot = Palette.Warning; }
            else { text = "Protegendo · " + s.DeviceName; dot = Palette.Good; }
            headerStatus.Text = text;
            statusDot.Fill = dot;
        }

        /// <summary>Troca o modo global (usado pela tela Agora, pelos Ajustes e pela bandeja).</summary>
        public void SetMode(VolumeMode m)
        {
            ApplyMode(engine, m);
            LoadModeToNow();
            LoadSettingsToUi();
        }

        public static void ApplyMode(AudioEngine engine, VolumeMode m)
        {
            var s = SettingsStore.Current;
            int prev = s.Mode;
            if (prev == (int)m) return;
            s.Mode = (int)m;
            SettingsStore.MarkDirty();
            engine.SettingsChanged(prev);
        }

        static string AppNameFor(EngineSnapshot s)
        {
            if (s == null || s.Apps.Count == 0) return null;
            var top = s.Apps.Where(a => a.Sounding && !double.IsInfinity(a.OutSpl)).OrderByDescending(a => a.OutSpl).FirstOrDefault();
            return top != null ? top.Name : null;
        }
    }
}
