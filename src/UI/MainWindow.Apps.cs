using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VolumeGuard.Audio;
using VolumeGuard.Core;

namespace VolumeGuard.UI
{
    public sealed partial class MainWindow
    {
        readonly ObservableCollection<AppRowVM> appRows = new ObservableCollection<AppRowVM>();
        readonly ObservableCollection<SavedRuleVM> savedRows = new ObservableCollection<SavedRuleVM>();
        TextBlock appsHint, savedEmpty;
        Border appsEmpty;
        DateTime lastSavedRefresh = DateTime.MinValue;
        string savedSignature;

        void InitApps()
        {
            var p = pageApps;
            Xaml.Find<ItemsControl>(p, "AppsList").ItemsSource = appRows;
            Xaml.Find<ItemsControl>(p, "SavedList").ItemsSource = savedRows;
            appsHint = Xaml.Find<TextBlock>(p, "AppsHint");
            appsEmpty = Xaml.Find<Border>(p, "AppsEmpty");
            savedEmpty = Xaml.Find<TextBlock>(p, "SavedEmpty");
            Xaml.Find<Button>(p, "ResetAllButton").Click += (s, a) =>
            {
                var r = MessageBox.Show(this, "Todos os apps (menos os marcados como Fixo) voltam para o volume padrão e os volumes lembrados são apagados. Continuar?",
                    "VolumeGuard", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
                engine.ResetAllToDefault();
                Dispatcher.BeginInvoke(new Action(RefreshSaved), System.Windows.Threading.DispatcherPriority.Background);
            };
        }

        void UpdateApps(EngineSnapshot s)
        {
            var st = SettingsStore.Current;
            appsHint.Text = st.Mode == (int)VolumeMode.Fixed
                ? "Modo volume fixo: cada app abre em " + Fmt.VolumeMain(st.InitialVolumeDb, st.ShowPercent) + ". Arraste para ajustar — o VolumeGuard lembra o volume de cada app."
                : "Modo nível alvo: cada app é ajustado para ~" + Fmt.Spl(st.TargetSpl) + " no ouvido. Arrastar deixa aquele app mais alto ou mais baixo que o alvo.";

            var keys = new HashSet<string>(s.Apps.Select(a => a.Key), StringComparer.OrdinalIgnoreCase);
            for (int i = appRows.Count - 1; i >= 0; i--)
                if (!keys.Contains(appRows[i].Key)) appRows.RemoveAt(i);
            foreach (var a in s.Apps.OrderBy(x => x.IsSystem).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = appRows.FirstOrDefault(r => string.Equals(r.Key, a.Key, StringComparison.OrdinalIgnoreCase));
                if (row == null)
                {
                    row = new AppRowVM(engine, a.Key, a.ExePath);
                    int idx = 0;
                    while (idx < appRows.Count && string.Compare(appRows[idx].Name, a.Name, StringComparison.CurrentCultureIgnoreCase) < 0 && !a.IsSystem) idx++;
                    if (a.IsSystem) idx = appRows.Count;
                    appRows.Insert(idx, row);
                }
                row.Update(a);
            }
            appsEmpty.Visibility = appRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if ((DateTime.Now - lastSavedRefresh).TotalSeconds > 5) RefreshSaved();
        }

        void RefreshSaved()
        {
            lastSavedRefresh = DateTime.Now;
            var st = SettingsStore.Current;
            List<AppRule> rules;
            lock (SettingsStore.Sync) rules = st.Apps.ToList();
            var items = new List<SavedRuleVM>();
            foreach (var r in rules.OrderBy(x => x.Name ?? x.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var parts = new List<string>();
                if (r.Mode == (int)AppRuleMode.Ignore) parts.Add("Ignorado");
                else if (r.Mode == (int)AppRuleMode.Fixed) parts.Add("Fixo em " + Fmt.VolumeMain(r.VolumeDb, st.ShowPercent));
                else
                {
                    if (r.HasVolume) parts.Add("Volume " + Fmt.VolumeMain(r.VolumeDb, st.ShowPercent));
                    if (Math.Abs(r.TargetOffsetDb) >= 0.5) parts.Add("alvo " + (r.TargetOffsetDb > 0 ? "+" : "") + Fmt.Db(r.TargetOffsetDb));
                    if (r.HasAgc) parts.Add("nível auto " + Fmt.VolumeMain(r.LastAgcDb, st.ShowPercent));
                }
                if (parts.Count == 0) continue;
                string key = r.Key;
                items.Add(new SavedRuleVM
                {
                    Name = r.Name ?? r.Key,
                    Summary = string.Join(" · ", parts),
                    Forget = new RelayCommand(() =>
                    {
                        engine.ForgetRule(key);
                        SettingsStore.RemoveRule(key);
                        RefreshSaved();
                    }),
                });
            }
            string sig = string.Join("\n", items.Select(i => i.Name + "|" + i.Summary));
            if (sig != savedSignature)
            {
                savedSignature = sig;
                savedRows.Clear();
                foreach (var i in items) savedRows.Add(i);
            }
            savedEmpty.Visibility = savedRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
