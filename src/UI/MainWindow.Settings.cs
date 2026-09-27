using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VolumeGuard.Audio;
using VolumeGuard.Core;

namespace VolumeGuard.UI
{
    public sealed partial class MainWindow
    {
        bool setUpdating;
        TextBlock calDevice, calValue, calcResult, masterModeText, modeExplain;
        TextBlock initialValue, targetValue, limiterValue, alertValue, alertSecondsValue;
        WrapPanel presetPanel;
        Slider calSlider, initialSlider, targetSlider, limiterSlider, alertSlider, alertSecondsSlider;
        CheckBox countExposureCheck, rememberCheck, existingCheck, percentCheck, limiterCheck, alertCheck, doseCheck, startupCheck, minimizedCheck;
        ToggleButton calcToggle;
        Border calcPanel, calCard;
        TextBox calcSens, calcImp, calcVolt;
        RadioButton calcUnitMw, setModeFixed, setModeTarget;
        readonly Dictionary<string, RadioButton> presetButtons = new Dictionary<string, RadioButton>();
        readonly Dictionary<string, RadioButton> startPageButtons = new Dictionary<string, RadioButton>();
        double calcValue = double.NaN;
        string shownDeviceId;

        void InitSettings()
        {
            var p = pageSettings;
            calCard = Xaml.Find<Border>(p, "CalCard");
            calDevice = Xaml.Find<TextBlock>(p, "CalDevice");
            presetPanel = Xaml.Find<WrapPanel>(p, "PresetPanel");
            calValue = Xaml.Find<TextBlock>(p, "CalValue");
            calSlider = Xaml.Find<Slider>(p, "CalSlider");
            countExposureCheck = Xaml.Find<CheckBox>(p, "CountExposureCheck");
            calcToggle = Xaml.Find<ToggleButton>(p, "CalcToggle");
            calcPanel = Xaml.Find<Border>(p, "CalcPanel");
            calcSens = Xaml.Find<TextBox>(p, "CalcSens");
            calcImp = Xaml.Find<TextBox>(p, "CalcImp");
            calcVolt = Xaml.Find<TextBox>(p, "CalcVolt");
            calcUnitMw = Xaml.Find<RadioButton>(p, "CalcUnitMw");
            calcResult = Xaml.Find<TextBlock>(p, "CalcResult");
            masterModeText = Xaml.Find<TextBlock>(p, "MasterModeText");
            setModeFixed = Xaml.Find<RadioButton>(p, "SetModeFixed");
            setModeTarget = Xaml.Find<RadioButton>(p, "SetModeTarget");
            modeExplain = Xaml.Find<TextBlock>(p, "ModeExplain");
            initialValue = Xaml.Find<TextBlock>(p, "InitialValue");
            initialSlider = Xaml.Find<Slider>(p, "InitialSlider");
            targetValue = Xaml.Find<TextBlock>(p, "TargetValue");
            targetSlider = Xaml.Find<Slider>(p, "TargetSlider");
            rememberCheck = Xaml.Find<CheckBox>(p, "RememberCheck");
            existingCheck = Xaml.Find<CheckBox>(p, "ExistingCheck");
            percentCheck = Xaml.Find<CheckBox>(p, "PercentCheck");
            limiterCheck = Xaml.Find<CheckBox>(p, "LimiterCheck");
            limiterSlider = Xaml.Find<Slider>(p, "LimiterSlider");
            limiterValue = Xaml.Find<TextBlock>(p, "LimiterValue");
            alertCheck = Xaml.Find<CheckBox>(p, "AlertCheck");
            alertSlider = Xaml.Find<Slider>(p, "AlertSlider");
            alertValue = Xaml.Find<TextBlock>(p, "AlertValue");
            alertSecondsSlider = Xaml.Find<Slider>(p, "AlertSecondsSlider");
            alertSecondsValue = Xaml.Find<TextBlock>(p, "AlertSecondsValue");
            doseCheck = Xaml.Find<CheckBox>(p, "DoseCheck");
            startupCheck = Xaml.Find<CheckBox>(p, "StartupCheck");
            minimizedCheck = Xaml.Find<CheckBox>(p, "MinimizedCheck");
            startPageButtons["now"] = Xaml.Find<RadioButton>(p, "StartPageNow");
            startPageButtons["apps"] = Xaml.Find<RadioButton>(p, "StartPageApps");
            startPageButtons["history"] = Xaml.Find<RadioButton>(p, "StartPageHistory");
            startPageButtons["settings"] = Xaml.Find<RadioButton>(p, "StartPageSettings");
            foreach (var kv in startPageButtons)
            {
                string page = kv.Key;
                kv.Value.Checked += (s, a) =>
                {
                    if (setUpdating) return;
                    SettingsStore.Current.StartPage = page;
                    SettingsStore.MarkDirty();
                };
            }

            foreach (var preset in HeadphonePresets.All)
            {
                var content = new StackPanel();
                var head = new DockPanel();
                var val = new TextBlock { Text = Fmt.Spl(preset.CalSpl), Foreground = Palette.InkSecondary, FontSize = 12 };
                DockPanel.SetDock(val, Dock.Right);
                head.Children.Add(val);
                head.Children.Add(new TextBlock { Text = preset.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                content.Children.Add(head);
                content.Children.Add(new TextBlock { Text = preset.Description, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Muted, FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0) });
                var rb = new RadioButton { Content = content, GroupName = "preset", Style = (Style)FindResource("PresetCard") };
                string key = preset.Key;
                rb.Checked += (s, a) => { if (!setUpdating) ApplyPreset(key); };
                presetButtons[key] = rb;
                presetPanel.Children.Add(rb);
            }

            calSlider.ValueChanged += (s, a) =>
            {
                if (setUpdating) return;
                var prof = CurrentProfile();
                if (prof == null) return;
                prof.CalSpl = Math.Round(a.NewValue * 2) / 2;
                var preset = HeadphonePresets.Find(prof.Preset);
                if (preset == null || Math.Abs(preset.CalSpl - prof.CalSpl) > 0.25) prof.Preset = "custom";
                prof.Configured = true;
                SettingsStore.MarkDirty();
                LoadSettingsToUi();
            };
            countExposureCheck.Click += (s, a) =>
            {
                var prof = CurrentProfile();
                if (prof == null) return;
                prof.CountExposure = countExposureCheck.IsChecked == true;
                prof.Configured = true;
                SettingsStore.MarkDirty();
            };
            calcToggle.Click += (s, a) => calcPanel.Visibility = calcToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            calcSens.TextChanged += (s, a) => RecalcHeadphone();
            calcImp.TextChanged += (s, a) => RecalcHeadphone();
            calcVolt.TextChanged += (s, a) => RecalcHeadphone();
            calcUnitMw.Checked += (s, a) => RecalcHeadphone();
            Xaml.Find<RadioButton>(p, "CalcUnitV").Checked += (s, a) => RecalcHeadphone();
            Xaml.Find<Button>(p, "CalcApply").Click += (s, a) =>
            {
                var prof = CurrentProfile();
                if (prof == null || double.IsNaN(calcValue)) return;
                prof.CalSpl = Math.Round(calcValue * 2) / 2;
                prof.Preset = "custom";
                prof.Configured = true;
                SettingsStore.MarkDirty();
                LoadSettingsToUi();
            };

            setModeFixed.Checked += (s, a) => { if (!setUpdating) SetMode(VolumeMode.Fixed); };
            setModeTarget.Checked += (s, a) => { if (!setUpdating) SetMode(VolumeMode.Target); };
            initialSlider.ValueChanged += (s, a) =>
            {
                if (setUpdating) return;
                SettingsStore.Current.InitialVolumeDb = Math.Round(a.NewValue * 2) / 2;
                SettingsChangedFromUi();
            };
            targetSlider.ValueChanged += (s, a) =>
            {
                if (setUpdating) return;
                SettingsStore.Current.TargetSpl = Math.Round(a.NewValue * 2) / 2;
                SettingsChangedFromUi();
            };
            limiterSlider.ValueChanged += (s, a) => { if (!setUpdating) { SettingsStore.Current.LimiterMaxSpl = Math.Round(a.NewValue * 2) / 2; SettingsChangedFromUi(); } };
            alertSlider.ValueChanged += (s, a) => { if (!setUpdating) { SettingsStore.Current.AlertSpl = Math.Round(a.NewValue * 2) / 2; SettingsChangedFromUi(); } };
            alertSecondsSlider.ValueChanged += (s, a) => { if (!setUpdating) { SettingsStore.Current.AlertSeconds = (int)Math.Round(a.NewValue); SettingsChangedFromUi(); } };

            rememberCheck.Click += (s, a) => { SettingsStore.Current.RememberPerApp = rememberCheck.IsChecked == true; SettingsChangedFromUi(); };
            existingCheck.Click += (s, a) => { SettingsStore.Current.ApplyToExistingOnStart = existingCheck.IsChecked == true; SettingsChangedFromUi(); };
            percentCheck.Click += (s, a) => { SettingsStore.Current.ShowPercent = percentCheck.IsChecked == true; SettingsChangedFromUi(); };
            limiterCheck.Click += (s, a) => { SettingsStore.Current.LimiterEnabled = limiterCheck.IsChecked == true; SettingsChangedFromUi(); };
            alertCheck.Click += (s, a) => { SettingsStore.Current.AlertEnabled = alertCheck.IsChecked == true; SettingsChangedFromUi(); };
            doseCheck.Click += (s, a) => { SettingsStore.Current.DoseAlerts = doseCheck.IsChecked == true; SettingsChangedFromUi(); };
            minimizedCheck.Click += (s, a) => { SettingsStore.Current.StartMinimized = minimizedCheck.IsChecked == true; SettingsChangedFromUi(); };
            startupCheck.Click += (s, a) =>
            {
                try { StartupManager.SetEnabled(startupCheck.IsChecked == true); }
                catch (Exception ex) { MessageBox.Show(this, "Não consegui mudar a inicialização: " + ex.Message, "VolumeGuard"); }
                startupCheck.IsChecked = StartupManager.IsEnabled;
            };
            Xaml.Find<Button>(p, "OpenDataButton").Click += (s, a) => { try { Process.Start("explorer.exe", "\"" + SettingsStore.DataDir + "\""); } catch { } };
            Xaml.Find<Button>(p, "OpenMixerButton").Click += (s, a) => { try { Process.Start("sndvol.exe"); } catch { } };
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Xaml.Find<TextBlock>(p, "VersionText").Text = "VolumeGuard " + ver.Major + "." + ver.Minor + "." + ver.Build +
                " · funciona 100% offline: não acessa a internet e seus dados ficam só neste PC.";
            RecalcHeadphone();
        }

        void SettingsChangedFromUi()
        {
            SettingsStore.MarkDirty();
            engine.SettingsChanged(SettingsStore.Current.Mode);
            LoadSettingsToUi();
            LoadModeToNow();
        }

        void ApplyPreset(string key)
        {
            var prof = CurrentProfile();
            if (prof == null) return;
            HeadphonePresets.Apply(prof, key);
            prof.Configured = true;
            SettingsStore.MarkDirty();
            LoadSettingsToUi();
        }

        void RecalcHeadphone()
        {
            if (calcResult == null) return;
            double sens = Fmt.ParseNumber(calcSens.Text, double.NaN);
            double z = Fmt.ParseNumber(calcImp.Text, double.NaN);
            double v = Fmt.ParseNumber(calcVolt.Text, double.NaN);
            bool perMw = calcUnitMw.IsChecked == true;
            calcImp.IsEnabled = perMw;
            calcValue = double.NaN;
            if (double.IsNaN(sens) || double.IsNaN(v) || v <= 0 || (perMw && (double.IsNaN(z) || z <= 0)))
            {
                calcResult.Text = "Preencha os valores";
                return;
            }
            // dB/mW: SPL = S + 10·log10(P/1 mW), P = V²/Z. dB/V: SPL = S + 20·log10(V)
            calcValue = perMw ? sens + 10 * Math.Log10(v * v / z * 1000) : sens + 20 * Math.Log10(v);
            calcResult.Text = "≈ " + calcValue.ToString("0.#", Fmt.Br) + " dB SPL no máximo";
        }

        void LoadSettingsToUi()
        {
            if (calSlider == null) return;
            var s = SettingsStore.Current;
            setUpdating = true;
            try
            {
                var prof = CurrentProfile();
                calCard.IsEnabled = prof != null;
                if (prof != null)
                {
                    calDevice.Text = prof.Name;
                    foreach (var kv in presetButtons) kv.Value.IsChecked = kv.Key == prof.Preset && prof.Configured;
                    calSlider.Value = prof.CalSpl;
                    calValue.Text = Fmt.Spl(prof.CalSpl);
                    countExposureCheck.IsChecked = prof.CountExposure;
                    switch (prof.MasterMode)
                    {
                        case (int)MasterMode.PreMaster:
                            masterModeText.Text = "Volume do Windows: detectado que ele é aplicado depois da medição — o VolumeGuard soma o volume do Windows na conta.";
                            break;
                        case (int)MasterMode.PostMaster:
                            masterModeText.Text = "Volume do Windows: detectado que a medição já inclui o volume do Windows.";
                            break;
                        default:
                            masterModeText.Text = "Volume do Windows: ainda detectando como ele afeta a medição (precisa de algo tocando com o volume do Windows abaixo de 50%). Até lá, ele é somado na conta.";
                            break;
                    }
                }
                else calDevice.Text = "Nenhum dispositivo de saída";

                bool fixedMode = s.Mode == (int)VolumeMode.Fixed;
                setModeFixed.IsChecked = fixedMode;
                setModeTarget.IsChecked = !fixedMode;
                modeExplain.Text = fixedMode
                    ? "Volume fixo: todo app abre com o volume abaixo. Se você mexer em um app, ele passa a abrir do seu jeito. O limitador continua cuidando dos sustos."
                    : "Nível alvo: o VolumeGuard mede quanto cada app está soando e ajusta o volume dele para ficar perto do alvo. Propaganda alta é abaixada; vídeo baixo sobe até o máximo do app.";
                initialSlider.Value = s.InitialVolumeDb;
                initialValue.Text = Fmt.VolumeMain(s.InitialVolumeDb, s.ShowPercent) + " · " + Fmt.VolumeAlt(s.InitialVolumeDb, s.ShowPercent);
                targetSlider.Value = s.TargetSpl;
                targetValue.Text = Fmt.Spl(s.TargetSpl);
                rememberCheck.IsChecked = s.RememberPerApp;
                existingCheck.IsChecked = s.ApplyToExistingOnStart;
                percentCheck.IsChecked = s.ShowPercent;
                limiterCheck.IsChecked = s.LimiterEnabled;
                limiterSlider.Value = s.LimiterMaxSpl;
                limiterSlider.IsEnabled = s.LimiterEnabled;
                limiterValue.Text = Fmt.Spl(s.LimiterMaxSpl);
                alertCheck.IsChecked = s.AlertEnabled;
                alertSlider.Value = s.AlertSpl;
                alertSlider.IsEnabled = alertSecondsSlider.IsEnabled = s.AlertEnabled;
                alertValue.Text = Fmt.Spl(s.AlertSpl);
                alertSecondsSlider.Value = s.AlertSeconds;
                alertSecondsValue.Text = s.AlertSeconds + " s";
                doseCheck.IsChecked = s.DoseAlerts;
                startupCheck.IsChecked = StartupManager.IsEnabled;
                minimizedCheck.IsChecked = s.StartMinimized;
                foreach (var kv in startPageButtons) kv.Value.IsChecked = kv.Key == (s.StartPage ?? "now");
            }
            finally { setUpdating = false; }
        }

        void UpdateSettingsLive(EngineSnapshot s)
        {
            if (s.DeviceId != shownDeviceId)
            {
                shownDeviceId = s.DeviceId;
                LoadSettingsToUi();
            }
        }
    }
}
