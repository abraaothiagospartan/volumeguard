using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VolumeGuard.Audio;
using VolumeGuard.Core;

namespace VolumeGuard.UI
{
    /// <summary>Aviso flutuante no canto da tela (não rouba o foco).</summary>
    public sealed class AlertWindow : Window
    {
        readonly AudioEngine engine;
        readonly Border card, iconBg;
        readonly TextBlock iconText, titleText, bodyText;
        readonly Button primary, secondary;
        readonly DispatcherTimer hideTimer = new DispatcherTimer();
        bool showingLoud;

        public event Action OpenHistory;

        public AlertWindow(AudioEngine e)
        {
            engine = e;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            Foreground = Palette.Ink;
            Title = "VolumeGuard — aviso";

            var root = (FrameworkElement)Xaml.Load("Alert.xaml");
            Content = root;
            card = Xaml.Find<Border>(root, "Card");
            iconBg = Xaml.Find<Border>(root, "IconBg");
            iconText = Xaml.Find<TextBlock>(root, "IconText");
            titleText = Xaml.Find<TextBlock>(root, "TitleText");
            bodyText = Xaml.Find<TextBlock>(root, "BodyText");
            primary = Xaml.Find<Button>(root, "PrimaryButton");
            secondary = Xaml.Find<Button>(root, "SecondaryButton");
            Xaml.Find<Button>(root, "CloseButton").Click += (s, a) => HideAlert();
            primary.Click += (s, a) =>
            {
                if (showingLoud) engine.ReduceNow();
                else if (OpenHistory != null) OpenHistory();
                HideAlert();
            };
            secondary.Click += (s, a) =>
            {
                if (showingLoud) engine.SnoozeAlerts(15);
                HideAlert();
            };
            hideTimer.Tick += (s, a) => HideAlert();
            SizeChanged += (s, a) => PlaceInCorner();
        }

        public void Handle(AlertInfo a)
        {
            switch (a.Kind)
            {
                case AlertKind.Loud: ShowLoud(a); break;
                case AlertKind.LoudUpdate: if (IsVisible && showingLoud) FillLoud(a); break;
                case AlertKind.LoudEnded:
                    if (IsVisible && showingLoud) { hideTimer.Interval = TimeSpan.FromSeconds(2); hideTimer.Start(); }
                    break;
                case AlertKind.Dose: if (!(IsVisible && showingLoud)) ShowDose(a); break;
            }
        }

        void ShowLoud(AlertInfo a)
        {
            showingLoud = true;
            hideTimer.Stop();
            card.BorderBrush = Palette.Critical;
            iconBg.Background = Palette.ZoneTint(3);
            iconText.Foreground = Palette.Critical;
            iconText.Text = "!";
            primary.Style = (Style)FindResource("DangerButton");
            primary.Content = "Baixar agora";
            secondary.Content = "Ignorar 15 min";
            secondary.Visibility = Visibility.Visible;
            FillLoud(a);
            Present();
        }

        void FillLoud(AlertInfo a)
        {
            var s = SettingsStore.Current;
            titleText.Text = "Volume alto: " + Fmt.Spl(a.Level);
            string who = string.IsNullOrEmpty(a.AppName) ? "O som" : a.AppName;
            double safe = Hearing.SafeSecondsPerDay(a.Level);
            bodyText.Text = who + " está acima do seu limite de alerta (" + Fmt.Spl(s.AlertSpl) + "). " +
                (safe < 16 * 3600 ? "Nesse nível, o tempo seguro é de só " + Hearing.FormatDuration(safe) + " por dia. " : "") +
                "Dose de hoje: " + Fmt.Percent(a.Dose) + ".";
        }

        void ShowDose(AlertInfo a)
        {
            showingLoud = false;
            card.BorderBrush = a.Milestone >= 100 ? Palette.Critical : Palette.Warning;
            iconBg.Background = Palette.ZoneTint(a.Milestone >= 100 ? 3 : 1);
            iconText.Foreground = a.Milestone >= 100 ? Palette.Critical : Palette.Warning;
            iconText.Text = "%";
            primary.Style = (Style)FindResource("AccentButton");
            primary.Content = "Ver histórico";
            secondary.Content = "OK";
            titleText.Text = "Você já usou " + a.Milestone + "% da dose de som de hoje";
            if (a.Milestone >= 100)
                bodyText.Text = "Passou do limite diário recomendado (85 dB por 8 h). Daqui pra frente o risco para a audição aumenta: vale fazer uma pausa ou baixar bem o volume.";
            else
            {
                double remaining = Hearing.NioshSeconds * (1 - a.Dose / 100) * Math.Pow(10, -((double.IsInfinity(a.Level) ? 85 : a.Level) - 85) / 10);
                bodyText.Text = "No nível de agora" + (double.IsInfinity(a.Level) ? "" : " (" + Fmt.Spl(a.Level) + ")") +
                    ", sobra cerca de " + Hearing.FormatDuration(Math.Max(0, remaining)) + " até 100%. Baixar 3 dB dobra esse tempo.";
            }
            Present();
            hideTimer.Interval = TimeSpan.FromSeconds(25);
            hideTimer.Start();
        }

        void Present()
        {
            if (!IsVisible)
            {
                Opacity = 0;
                Show();
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            }
            PlaceInCorner();
        }

        void PlaceInCorner()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 4;
            Top = wa.Bottom - ActualHeight - 4;
        }

        void HideAlert()
        {
            hideTimer.Stop();
            showingLoud = false;
            Hide();
        }
    }
}
