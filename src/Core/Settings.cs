using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace VolumeGuard.Core
{
    public enum VolumeMode { Fixed = 0, Target = 1 }
    public enum AppRuleMode { Auto = 0, Fixed = 1, Ignore = 2 }
    public enum MasterMode { Unknown = 0, PreMaster = 1, PostMaster = 2 }

    [DataContract]
    public class DeviceProfile
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public string Preset;
        /// <summary>dB SPL que o fone produz com um seno de 1 kHz em escala cheia e volume do sistema em 100%.</summary>
        [DataMember] public double CalSpl;
        [DataMember] public bool CountExposure;
        [DataMember] public bool Configured;
        [DataMember] public int MasterMode;

        public DeviceProfile() { CalSpl = 110; CountExposure = true; Preset = "wired"; }
    }

    [DataContract]
    public class AppRule
    {
        [DataMember] public string Key;
        [DataMember] public string Name;
        [DataMember] public string ExePath;
        [DataMember] public int Mode;
        [DataMember] public bool HasVolume;
        [DataMember] public double VolumeDb;
        [DataMember] public double TargetOffsetDb;
        [DataMember] public bool HasAgc;
        [DataMember] public double LastAgcDb;
    }

    [DataContract]
    public class AppSettings
    {
        [DataMember] public int Mode;
        [DataMember] public double InitialVolumeDb;
        [DataMember] public bool ShowPercent;
        [DataMember] public bool RememberPerApp;
        [DataMember] public bool ApplyToExistingOnStart;
        [DataMember] public double TargetSpl;
        [DataMember] public bool LimiterEnabled;
        [DataMember] public double LimiterMaxSpl;
        [DataMember] public bool AlertEnabled;
        [DataMember] public double AlertSpl;
        [DataMember] public int AlertSeconds;
        [DataMember] public bool DoseAlerts;
        [DataMember] public bool StartMinimized;
        /// <summary>Tela mostrada ao abrir a janela: now, apps, history ou settings.</summary>
        [DataMember] public string StartPage;
        [DataMember] public bool FirstRunDone;
        [DataMember] public int SettingsVersion;
        [DataMember] public List<DeviceProfile> Devices;
        [DataMember] public List<AppRule> Apps;

        public static readonly string[] Pages = { "now", "apps", "history", "settings" };

        public AppSettings() { SetDefaults(); }

        [OnDeserializing]
        void OnDeserializing(StreamingContext c) { SetDefaults(); }

        [OnDeserialized]
        void OnDeserialized(StreamingContext c)
        {
            if (Devices == null) Devices = new List<DeviceProfile>();
            if (Apps == null) Apps = new List<AppRule>();
        }

        /// <summary>Corrige valores fora da faixa (arquivo editado à mão ou corrompido).</summary>
        public void Sanitize()
        {
            Mode = Mode == (int)VolumeMode.Target ? 1 : 0;
            InitialVolumeDb = Clamp(InitialVolumeDb, -60, 0, -20);
            TargetSpl = Clamp(TargetSpl, 40, 85, 68);
            LimiterMaxSpl = Clamp(LimiterMaxSpl, 60, 100, 85);
            AlertSpl = Clamp(AlertSpl, 70, 105, 85);
            AlertSeconds = Math.Max(1, Math.Min(30, AlertSeconds));
            if (Array.IndexOf(Pages, StartPage) < 0) StartPage = "now";
            Devices.RemoveAll(d => d == null || string.IsNullOrEmpty(d.Id));
            if (Devices.Count > 50) Devices.RemoveRange(0, Devices.Count - 50);
            foreach (var d in Devices)
            {
                d.CalSpl = Clamp(d.CalSpl, 80, 135, 110);
                if (d.MasterMode < 0 || d.MasterMode > 2) d.MasterMode = 0;
            }
            Apps.RemoveAll(r => r == null || string.IsNullOrEmpty(r.Key));
            if (Apps.Count > 500) Apps.RemoveRange(0, Apps.Count - 500);
            foreach (var r in Apps)
            {
                if (r.Mode < 0 || r.Mode > 2) r.Mode = 0;
                r.VolumeDb = Clamp(r.VolumeDb, -60, 0, -20);
                r.TargetOffsetDb = Clamp(r.TargetOffsetDb, -30, 30, 0);
                r.LastAgcDb = Clamp(r.LastAgcDb, -60, 0, -20);
            }
        }

        static double Clamp(double v, double lo, double hi, double fallback)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return fallback;
            return v < lo ? lo : v > hi ? hi : v;
        }

        void SetDefaults()
        {
            Mode = (int)VolumeMode.Fixed;
            InitialVolumeDb = -20;
            ShowPercent = false;
            RememberPerApp = true;
            ApplyToExistingOnStart = true;
            TargetSpl = 68;
            LimiterEnabled = true;
            LimiterMaxSpl = 85;
            AlertEnabled = true;
            AlertSpl = 85;
            AlertSeconds = 5;
            DoseAlerts = true;
            StartMinimized = false;
            StartPage = "now";
            SettingsVersion = 1;
            Devices = new List<DeviceProfile>();
            Apps = new List<AppRule>();
        }
    }

    /// <summary>Guarda as configurações em %APPDATA%\VolumeGuard\settings.json.</summary>
    public static class SettingsStore
    {
        public static readonly object Sync = new object();
        public static AppSettings Current = new AppSettings();
        static volatile bool dirty;

        public static string DataDir
        {
            get
            {
                string d = Environment.GetEnvironmentVariable("VOLUMEGUARD_DATA");
                if (string.IsNullOrEmpty(d)) d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VolumeGuard");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static string FilePath { get { return Path.Combine(DataDir, "settings.json"); } }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var bytes = File.ReadAllBytes(FilePath);
                // o Bloco de Notas pode gravar BOM, que o leitor JSON não aceita
                int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                using (var ms = new MemoryStream(bytes, start, bytes.Length - start))
                {
                    var ser = new DataContractJsonSerializer(typeof(AppSettings));
                    var s = (AppSettings)ser.ReadObject(ms);
                    if (s != null) { s.Sanitize(); Current = s; }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Falha ao ler configurações: " + ex.Message);
                try { File.Copy(FilePath, FilePath + ".invalido", true); } catch { }
            }
        }

        public static void MarkDirty() { dirty = true; }

        public static void SaveIfDirty() { if (dirty) Save(); }

        public static void Save()
        {
            try
            {
                byte[] bytes;
                lock (Sync)
                {
                    dirty = false;
                    var ser = new DataContractJsonSerializer(typeof(AppSettings));
                    using (var ms = new MemoryStream())
                    {
                        ser.WriteObject(ms, Current);
                        bytes = ms.ToArray();
                    }
                }
                string tmp = FilePath + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Write("Falha ao salvar configurações: " + ex.Message); }
        }

        public static AppRule FindRule(string key)
        {
            lock (Sync)
            {
                foreach (var r in Current.Apps) if (string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)) return r;
                return null;
            }
        }

        public static AppRule GetOrCreateRule(string key, string name, string exePath)
        {
            lock (Sync)
            {
                foreach (var r in Current.Apps) if (string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)) return r;
                var rule = new AppRule { Key = key, Name = name, ExePath = exePath };
                Current.Apps.Add(rule);
                dirty = true;
                return rule;
            }
        }

        public static void RemoveRule(string key)
        {
            lock (Sync)
            {
                Current.Apps.RemoveAll(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
                dirty = true;
            }
        }

        public static DeviceProfile GetOrCreateDevice(string id, string name, string suggestedPreset)
        {
            lock (Sync)
            {
                foreach (var d in Current.Devices) if (d.Id == id) { d.Name = name; return d; }
                var p = new DeviceProfile { Id = id, Name = name };
                HeadphonePresets.Apply(p, suggestedPreset);
                p.Configured = false;
                Current.Devices.Add(p);
                dirty = true;
                return p;
            }
        }
    }

    public class HeadphonePreset
    {
        public string Key;
        public string Title;
        public string Description;
        public double CalSpl;
        public bool CountExposure;
    }

    public static class HeadphonePresets
    {
        public static readonly HeadphonePreset[] All =
        {
            new HeadphonePreset { Key = "wired", Title = "Fone com fio (P2 no PC)", Description = "Fone comum ligado na placa-mãe ou no gabinete. Típico: ~112 dB no máximo.", CalSpl = 112, CountExposure = true },
            new HeadphonePreset { Key = "iem", Title = "In-ear sensível / IEM", Description = "Fones intra-auriculares de alta sensibilidade, que ficam altos com pouco volume. Típico: ~118 dB.", CalSpl = 118, CountExposure = true },
            new HeadphonePreset { Key = "amp", Title = "Fone com amplificador / DAC", Description = "Com amplificador externo o máximo passa fácil dos 120 dB. Use a calculadora se puder.", CalSpl = 122, CountExposure = true },
            new HeadphonePreset { Key = "usb", Title = "Headset USB / gamer", Description = "Headsets USB costumam ter limite interno. Típico: ~104 dB.", CalSpl = 104, CountExposure = true },
            new HeadphonePreset { Key = "bt", Title = "Fone Bluetooth", Description = "Muitos fones Bluetooth limitam em torno de 100 dB.", CalSpl = 100, CountExposure = true },
            new HeadphonePreset { Key = "speakers", Title = "Caixas de som / monitor", Description = "Não entra na conta de exposição (a distância muda tudo).", CalSpl = 95, CountExposure = false },
        };

        public static HeadphonePreset Find(string key)
        {
            foreach (var p in All) if (p.Key == key) return p;
            return null;
        }

        public static void Apply(DeviceProfile profile, string key)
        {
            var p = Find(key);
            if (p == null) return;
            profile.Preset = p.Key;
            profile.CalSpl = p.CalSpl;
            profile.CountExposure = p.CountExposure;
        }
    }

    public static class Log
    {
        static readonly object sync = new object();
        public static void Write(string msg)
        {
            try
            {
                lock (sync)
                {
                    string path = Path.Combine(SettingsStore.DataDir, "log.txt");
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
