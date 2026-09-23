using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using VolumeGuard.Audio;
using VolumeGuard.Core;

namespace VolumeGuard.UI
{
    public sealed partial class MainWindow
    {
        Border banner, zoneChip;
        TextBlock bannerTitle, bannerText, heroValue, heroSub, zoneText;
        TextBlock dailyDoseValue, weeklyDoseValue, listenValue, leqValue, maxValue;
        TextBlock quickLabel, quickValue, quickCaption, limiterText, deviceText, masterValue, masterCaption;
        Ellipse zoneDot, limiterDot;
        LiveChart live;
        MeterBar dailyBar, weeklyBar;
        RadioButton modeFixed, modeTarget;
        Slider quickSlider, masterSlider;
        Button bannerButton;
        ToggleButton muteButton;
        bool nowUpdating;
        DateTime masterTouched = DateTime.MinValue, heroUpdated = DateTime.MinValue;
        double heroLevel = double.NegativeInfinity;
        bool liveHadSound, liveDrawn;
        double liveLimit = double.NaN, liveAlert = double.NaN;

        void InitNow()
        {
            var p = pageNow;
            banner = Xaml.Find<Border>(p, "Banner");
            bannerTitle = Xaml.Find<TextBlock>(p, "BannerTitle");
            bannerText = Xaml.Find<TextBlock>(p, "BannerText");
            bannerButton = Xaml.Find<Button>(p, "BannerButton");
            zoneChip = Xaml.Find<Border>(p, "ZoneChip");
            zoneDot = Xaml.Find<Ellipse>(p, "ZoneDot");
            zoneText = Xaml.Find<TextBlock>(p, "ZoneText");
            heroValue = Xaml.Find<TextBlock>(p, "HeroValue");
            heroSub = Xaml.Find<TextBlock>(p, "HeroSub");
            live = Xaml.Find<LiveChart>(p, "Live");
            dailyDoseValue = Xaml.Find<TextBlock>(p, "DailyDoseValue");
            weeklyDoseValue = Xaml.Find<TextBlock>(p, "WeeklyDoseValue");
            dailyBar = Xaml.Find<MeterBar>(p, "DailyDoseBar");
            weeklyBar = Xaml.Find<MeterBar>(p, "WeeklyDoseBar");
            listenValue = Xaml.Find<TextBlock>(p, "ListenValue");
            leqValue = Xaml.Find<TextBlock>(p, "LeqValue");
            maxValue = Xaml.Find<TextBlock>(p, "MaxValue");
            modeFixed = Xaml.Find<RadioButton>(p, "ModeFixed");
            modeTarget = Xaml.Find<RadioButton>(p, "ModeTarget");
            quickLabel = Xaml.Find<TextBlock>(p, "QuickLabel");
            quickValue = Xaml.Find<TextBlock>(p, "QuickValue");
            quickSlider = Xaml.Find<Slider>(p, "QuickSlider");
            quickCaption = Xaml.Find<TextBlock>(p, "QuickCaption");
            limiterDot = Xaml.Find<Ellipse>(p, "LimiterDot");
            limiterText = Xaml.Find<TextBlock>(p, "LimiterText");
            muteButton = Xaml.Find<ToggleButton>(p, "MuteButton");
            deviceText = Xaml.Find<TextBlock>(p, "DeviceText");
            masterValue = Xaml.Find<TextBlock>(p, "MasterValue");
            masterSlider = Xaml.Find<Slider>(p, "MasterSlider");
            masterCaption = Xaml.Find<TextBlock>(p, "MasterCaption");

            dailyBar.Maximum = 150; dailyBar.MarkerAt = 100;
            weeklyBar.Maximum = 150; weeklyBar.MarkerAt = 100;

            modeFixed.Checked += (s, a) => { if (!nowUpdating) SetMode(VolumeMode.Fixed); };
            modeTarget.Checked += (s, a) => { if (!nowUpdating) SetMode(VolumeMode.Target); };
            quickSlider.ValueChanged += (s, a) =>
            {
                if (nowUpdating) return;
                var st = SettingsStore.Current;
                double v = Math.Round(a.NewValue * 2) / 2;
                if (st.Mode == (int)VolumeMode.Fixed) st.InitialVolumeDb = v; else st.TargetSpl = v;
                SettingsStore.MarkDirty();
                engine.SettingsChanged(st.Mode);
                UpdateQuickTexts();
            };
            masterSlider.ValueChanged += (s, a) =>
            {
                if (nowUpdating) return;
                masterTouched = DateTime.Now;
                engine.SetMasterDb(a.NewValue);
                masterValue.Text = Fmt.Db(a.NewValue);
            };
            Xaml.Find<Button>(p, "MasterDown").Click += (s, a) => NudgeMaster(-1);
            Xaml.Find<Button>(p, "MasterUp").Click += (s, a) => NudgeMaster(+1);
            muteButton.Click += (s, a) => engine.SetMasterMute(muteButton.IsChecked == true);
            bannerButton.Click += (s, a) => ShowPage("settings");
            LoadModeToNow();
        }

        void NudgeMaster(double delta)
        {
            double v = Math.Max(masterSlider.Minimum, Math.Min(masterSlider.Maximum, masterSlider.Value + delta));
            masterTouched = DateTime.Now;
            nowUpdating = true;
            masterSlider.Value = v;
            nowUpdating = false;
            masterValue.Text = Fmt.Db(v);
            engine.SetMasterDb(v);
        }

        void LoadModeToNow()
        {
            if (modeFixed == null) return;
            var s = SettingsStore.Current;
            nowUpdating = true;
            try
            {
                bool fixedMode = s.Mode == (int)VolumeMode.Fixed;
                modeFixed.IsChecked = fixedMode;
                modeTarget.IsChecked = !fixedMode;
                if (fixedMode)
                {
                    quickLabel.Text = "Volume com que cada app abre";
                    quickSlider.Minimum = AudioEngine.MinDb; quickSlider.Maximum = 0;
                    quickSlider.SmallChange = 0.5; quickSlider.LargeChange = 3;
                    quickSlider.Value = s.InitialVolumeDb;
                }
                else
                {
                    quickLabel.Text = "Nível alvo no seu ouvido";
                    quickSlider.Minimum = 40; quickSlider.Maximum = 85;
                    quickSlider.SmallChange = 0.5; quickSlider.LargeChange = 2;
                    quickSlider.Value = s.TargetSpl;
                }
            }
            finally { nowUpdating = false; }
            UpdateQuickTexts();
        }

        void UpdateQuickTexts()
        {
            var s = SettingsStore.Current;
            if (s.Mode == (int)VolumeMode.Fixed)
            {
                quickValue.Text = Fmt.VolumeMain(s.InitialVolumeDb, s.ShowPercent) + " · " + Fmt.VolumeAlt(s.InitialVolumeDb, s.ShowPercent);
                quickCaption.Text = "Todo app ou site que começar a tocar som já abre neste volume. Se você ajustar um app, ele passa a abrir do jeito que você deixou.";
            }
            else
            {
                quickValue.Text = Fmt.Spl(s.TargetSpl);
                quickCaption.Text = "Cada app é ajustado sozinho para soar perto deste nível: propaganda alta é abaixada, vídeo baixo sobe (até o máximo do app). Mexer no volume de um app vira uma preferência dele.";
            }
        }

        void UpdateNow(EngineSnapshot s)
        {
            var st = SettingsStore.Current;

            // Aviso de calibração / erro
            if (!s.HasDevice)
            {
                banner.Visibility = Visibility.Visible;
                bannerTitle.Text = "Nenhuma saída de áudio disponível";
                bannerText.Text = s.Error ?? "Conecte um fone ou escolha uma saída de áudio no Windows.";
                bannerButton.Visibility = Visibility.Collapsed;
            }
            else if (!s.DeviceConfigured)
            {
                banner.Visibility = Visibility.Visible;
                bannerTitle.Text = "Diga qual fone você usa para os dB ficarem certos";
                var profile = CurrentProfile();
                var preset = profile != null ? HeadphonePresets.Find(profile.Preset) : null;
                bannerText.Text = "Por enquanto estou supondo \"" + (preset != null ? preset.Title : "fone comum") + "\" (" + Fmt.Spl(s.CalSpl) + " no máximo) em " + s.DeviceName + ".";
                bannerButton.Visibility = Visibility.Visible;
            }
            else if (s.Error != null)
            {
                banner.Visibility = Visibility.Visible;
                bannerTitle.Text = "Atenção";
                bannerText.Text = s.Error;
                bannerButton.Visibility = Visibility.Collapsed;
            }
            else banner.Visibility = Visibility.Collapsed;

            // Número principal (4x por segundo para não tremer)
            double level = s.LevelFast;
            bool sounding = !double.IsInfinity(level) && level > 20;
            if ((DateTime.Now - heroUpdated).TotalMilliseconds >= 250)
            {
                heroUpdated = DateTime.Now;
                heroLevel = sounding ? level : double.NegativeInfinity;
                heroValue.Text = sounding ? Fmt.SplNumber(level) : "—";
                heroValue.Foreground = sounding ? Palette.Ink : Palette.Muted;
                if (!sounding)
                {
                    zoneText.Text = "Silêncio";
                    zoneDot.Fill = Palette.Muted;
                    zoneChip.Background = Palette.Raised;
                }
                else
                {
                    int z = Hearing.Zone(level);
                    zoneText.Text = Hearing.ZoneName(z);
                    zoneDot.Fill = Palette.Zone(z);
                    zoneChip.Background = Palette.ZoneTint(z);
                }
                string app = AppNameFor(s);
                if (!s.CountExposure && s.HasDevice)
                    heroSub.Text = "Este dispositivo está marcado como caixa de som: os dB são só referência e não entram na exposição.";
                else if (!sounding)
                    heroSub.Text = "Nada tocando agora.";
                else
                    heroSub.Text = "Nesse nível dá para ouvir " + Hearing.FormatDuration(Hearing.SafeSecondsPerDay(level)) + " por dia com segurança" + (app != null ? " · vindo de " + app : "") + ".";
            }

            // em silêncio o gráfico não muda: só redesenha quando há som (ou quando o som acabou de parar)
            bool liveHasSound = s.Live != null && s.Live.Any(v => !double.IsNaN(v) && !double.IsInfinity(v));
            double limitLine = st.LimiterEnabled ? st.LimiterMaxSpl : double.NaN, alertLine = st.AlertEnabled ? st.AlertSpl : double.NaN;
            bool linesChanged = !limitLine.Equals(liveLimit) || !alertLine.Equals(liveAlert);
            if (s.Live != null && (liveHasSound || liveHadSound || !liveDrawn || linesChanged))
            {
                live.SetData(s.Live, limitLine, alertLine);
                liveDrawn = true;
                liveLimit = limitLine; liveAlert = alertLine;
            }
            liveHadSound = liveHasSound;

            dailyDoseValue.Text = Fmt.Percent(s.DailyDose);
            dailyBar.Value = Math.Min(150, s.DailyDose);
            dailyBar.Fill = Palette.DoseBrush(s.DailyDose);
            weeklyDoseValue.Text = Fmt.Percent(s.WeeklyDose);
            weeklyBar.Value = Math.Min(150, s.WeeklyDose);
            weeklyBar.Fill = Palette.DoseBrush(s.WeeklyDose);
            listenValue.Text = Hearing.FormatDuration(s.DayActive);
            leqValue.Text = Fmt.Spl(s.DayLeq);
            maxValue.Text = Fmt.Spl(s.DayMax);

            // Limitador
            var limited = s.Apps.Where(a => a.LimiterDb >= 0.5).OrderByDescending(a => a.LimiterDb).FirstOrDefault();
            if (s.Paused)
            {
                limiterDot.Fill = Palette.Muted;
                limiterText.Text = "Proteção pausada até " + s.PausedUntil.ToString("HH:mm") + ". Nada está sendo ajustado.";
            }
            else if (!st.LimiterEnabled)
            {
                limiterDot.Fill = Palette.Muted;
                limiterText.Text = "Limitador desligado (ligue em Ajustes para barrar propagandas e sustos).";
            }
            else if (limited != null)
            {
                limiterDot.Fill = Palette.Serious;
                limiterText.Text = "Limitador segurando " + limited.Name + " em " + Fmt.Db(-limited.LimiterDb) + " para não passar de " + Fmt.Spl(st.LimiterMaxSpl) + ".";
            }
            else
            {
                limiterDot.Fill = Palette.Good;
                limiterText.Text = "Limitador ativo: se algum app passar de " + Fmt.Spl(st.LimiterMaxSpl) + ", ele é baixado na hora.";
            }

            // Volume do Windows
            deviceText.Text = s.HasDevice ? s.DeviceName : "Sem dispositivo";
            if ((DateTime.Now - masterTouched).TotalSeconds > 1.2 && s.HasDevice)
            {
                nowUpdating = true;
                masterSlider.Minimum = s.MasterMinDb;
                masterSlider.Maximum = s.MasterMaxDb;
                masterSlider.SmallChange = Math.Max(0.1, s.MasterStepDb);
                masterSlider.LargeChange = 3;
                masterSlider.Value = s.MasterDb;
                nowUpdating = false;
                masterValue.Text = Fmt.Db(s.MasterDb);
                muteButton.IsChecked = s.MasterMuted;
            }
            masterCaption.Text = "Ajuste fino em dB, inclusive abaixo do 1% do Windows. Este dispositivo vai de " +
                Fmt.Db(s.MasterMinDb) + " a " + Fmt.Db(s.MasterMaxDb) + " em passos de " + s.MasterStepDb.ToString("0.##", Fmt.Br) + " dB.";
        }

        DeviceProfile CurrentProfile()
        {
            if (snap == null || snap.DeviceId == null) return null;
            lock (SettingsStore.Sync) return SettingsStore.Current.Devices.FirstOrDefault(d => d.Id == snap.DeviceId);
        }
    }
}
