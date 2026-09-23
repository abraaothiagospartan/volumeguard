using System;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using VolumeGuard.Audio;
using VolumeGuard.Core;

namespace VolumeGuard.UI
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, string name)
        {
            if (Equals(field, value)) return;
            field = value;
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class RelayCommand : ICommand
    {
        readonly Action action;
        public RelayCommand(Action a) { action = a; }
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) { return true; }
        public void Execute(object parameter) { action(); }
    }

    /// <summary>Linha da lista de apps.</summary>
    public sealed class AppRowVM : Observable
    {
        readonly AudioEngine engine;
        public string Key { get; private set; }
        public string Group { get; private set; }
        public string ExePath { get; private set; }

        string name, status, volumeText, volumeAltText;
        ImageSource icon;
        double volumeDb, meter;
        Brush meterBrush = Palette.Good;
        bool isAuto = true, isFixed, isIgnore;
        bool updating;
        DateTime userTouched = DateTime.MinValue;
        double lastSentDb = double.NaN;

        public AppRowVM(AudioEngine e, string key, string exePath)
        {
            engine = e;
            Key = key;
            Group = "rule_" + key;
            ExePath = exePath;
            icon = IconCache.Get(exePath);
        }

        public string Name { get { return name; } set { Set(ref name, value, "Name"); } }
        public string Status { get { return status; } set { Set(ref status, value, "Status"); } }
        public ImageSource Icon { get { return icon; } }
        public string VolumeText { get { return volumeText; } set { Set(ref volumeText, value, "VolumeText"); } }
        public string VolumeAltText { get { return volumeAltText; } set { Set(ref volumeAltText, value, "VolumeAltText"); } }
        public double Meter { get { return meter; } set { Set(ref meter, value, "Meter"); } }
        public Brush MeterBrush { get { return meterBrush; } set { Set(ref meterBrush, value, "MeterBrush"); } }

        public double VolumeDb
        {
            get { return volumeDb; }
            set
            {
                Set(ref volumeDb, value, "VolumeDb");
                if (updating) return;
                userTouched = DateTime.Now;
                UpdateVolumeText(value);
                if (double.IsNaN(lastSentDb) || Math.Abs(value - lastSentDb) >= 0.1)
                {
                    lastSentDb = value;
                    engine.SetAppVolumeDb(Key, value);
                }
            }
        }

        public bool IsAuto { get { return isAuto; } set { Set(ref isAuto, value, "IsAuto"); if (value && !updating) SendRule(AppRuleMode.Auto); } }
        public bool IsFixed { get { return isFixed; } set { Set(ref isFixed, value, "IsFixed"); if (value && !updating) SendRule(AppRuleMode.Fixed); } }
        public bool IsIgnore { get { return isIgnore; } set { Set(ref isIgnore, value, "IsIgnore"); if (value && !updating) SendRule(AppRuleMode.Ignore); } }

        void SendRule(AppRuleMode m)
        {
            userTouched = DateTime.Now;
            engine.SetAppRuleMode(Key, Name, ExePath, m);
        }

        void UpdateVolumeText(double db)
        {
            bool pct = SettingsStore.Current.ShowPercent;
            VolumeText = Fmt.VolumeMain(db, pct);
            VolumeAltText = Fmt.VolumeAlt(db, pct);
        }

        public void Update(AppSnapshot a)
        {
            updating = true;
            try
            {
                Name = a.Name;
                bool recentlyTouched = (DateTime.Now - userTouched).TotalSeconds < 1.2;
                if (!recentlyTouched)
                {
                    if (Math.Abs(volumeDb - a.VolumeDb) > 0.05) VolumeDb = a.VolumeDb;
                    lastSentDb = double.NaN;
                    UpdateVolumeText(a.VolumeDb);
                    IsAuto = a.RuleMode == 0;
                    IsFixed = a.RuleMode == 1;
                    IsIgnore = a.RuleMode == 2;
                }
                bool hasLevel = a.Sounding && !double.IsInfinity(a.OutSpl);
                Meter = hasLevel ? Math.Max(0, Math.Min(1, (a.OutSpl - 30) / 70.0)) : 0;
                MeterBrush = Palette.Zone(Hearing.Zone(a.OutSpl));
                string s;
                if (a.Muted) s = "Mudo";
                else if (hasLevel) s = "Tocando · " + Fmt.Spl(a.OutSpl);
                else s = "Em silêncio";
                if (a.LimiterDb >= 0.5) s += " · limitador " + Fmt.Db(-a.LimiterDb);
                else if (a.EffectiveMode == 1) s += a.AtMaximum ? " · já no máximo" : " · nível alvo";
                else if (a.EffectiveMode == 2) s += " · ignorado";
                if (a.EffectiveMode == 1 && Math.Abs(a.OffsetDb) >= 0.5) s += " (" + (a.OffsetDb > 0 ? "+" : "") + Fmt.Db(a.OffsetDb) + ")";
                Status = s;
            }
            finally { updating = false; }
        }
    }

    public sealed class SavedRuleVM
    {
        public string Name { get; set; }
        public string Summary { get; set; }
        public ICommand Forget { get; set; }
    }

    public sealed class TopAppVM
    {
        public string Name { get; set; }
        public double Share { get; set; }
        public string ShareText { get; set; }
    }
}
