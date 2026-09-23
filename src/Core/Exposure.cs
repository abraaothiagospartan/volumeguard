using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VolumeGuard.Core
{
    /// <summary>Contas de exposição sonora (NIOSH diário e OMS/ITU H.870 semanal).</summary>
    public static class Hearing
    {
        public const double NioshRefDb = 85, NioshSeconds = 8 * 3600;
        public const double WhoRefDb = 80, WhoSeconds = 40 * 3600;

        /// <summary>Energia relativa de 1 segundo a um nível (10^(L/10)).</summary>
        public static double Energy(double spl) { return Math.Pow(10, spl / 10.0); }

        public static double DailyDosePercent(double energy) { return energy * Math.Pow(10, -NioshRefDb / 10) / NioshSeconds * 100; }
        public static double WeeklyDosePercent(double energy) { return energy * Math.Pow(10, -WhoRefDb / 10) / WhoSeconds * 100; }

        /// <summary>Tempo diário permitido a um nível constante (NIOSH, taxa de troca de 3 dB).</summary>
        public static double SafeSecondsPerDay(double spl) { return NioshSeconds * Math.Pow(10, -(spl - NioshRefDb) / 10); }

        public static double LeqFromEnergy(double energy, double seconds)
        {
            if (seconds <= 0 || energy <= 0) return double.NegativeInfinity;
            return 10 * Math.Log10(energy / seconds);
        }

        /// <summary>0 = seguro, 1 = moderado, 2 = alto, 3 = perigoso.</summary>
        public static int Zone(double spl)
        {
            if (double.IsNaN(spl) || spl < 75) return 0;
            if (spl < 85) return 1;
            if (spl < 94) return 2;
            return 3;
        }

        public static string ZoneName(int zone)
        {
            switch (zone)
            {
                case 0: return "Seguro";
                case 1: return "Moderado";
                case 2: return "Alto";
                default: return "Perigoso";
            }
        }

        public static string FormatDuration(double seconds)
        {
            if (double.IsInfinity(seconds) || seconds > 24 * 3600) return "mais de 24 h";
            if (seconds < 60) return Math.Max(0, (int)Math.Round(seconds)) + " s";
            int total = (int)Math.Round(seconds / 60);
            int h = total / 60, m = total % 60;
            if (h == 0) return m + " min";
            if (m == 0) return h + " h";
            return h + " h " + m + " min";
        }
    }

    public class MinuteRecord
    {
        public int Minute;          // minuto do dia (0..1439)
        public double Leq;          // LAeq (dB SPL estimado) durante os segundos com som
        public double Max;          // maior nível de 1 s
        public int Active;          // segundos com som
        public Dictionary<string, double> Apps = new Dictionary<string, double>();  // fração de energia por app

        public double Energy { get { return Active * Hearing.Energy(Leq); } }
    }

    public class DayData
    {
        public DateTime Date;
        public List<MinuteRecord> Minutes = new List<MinuteRecord>();

        public double Energy { get { double e = 0; foreach (var m in Minutes) e += m.Energy; return e; } }
        public int ActiveSeconds { get { int a = 0; foreach (var m in Minutes) a += m.Active; return a; } }
        public double Max { get { double x = double.NegativeInfinity; foreach (var m in Minutes) x = Math.Max(x, m.Max); return x; } }

        public List<KeyValuePair<string, double>> AppEnergies()
        {
            var d = new Dictionary<string, double>();
            foreach (var m in Minutes)
            {
                double e = m.Energy;
                foreach (var kv in m.Apps)
                {
                    double cur; d.TryGetValue(kv.Key, out cur);
                    d[kv.Key] = cur + e * kv.Value;
                }
            }
            return d.OrderByDescending(kv => kv.Value).ToList();
        }
    }

    public static class HistoryStore
    {
        static readonly object sync = new object();
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Dir
        {
            get { string d = Path.Combine(SettingsStore.DataDir, "historico"); Directory.CreateDirectory(d); return d; }
        }

        public static string PathFor(DateTime date) { return Path.Combine(Dir, date.ToString("yyyy-MM-dd", Inv) + ".csv"); }

        /// <summary>
        /// Nome de app vem da descrição do .exe (controlada por quem fez o programa): tira separadores,
        /// caracteres de controle e o "=+-@" inicial que o Excel interpretaria como fórmula.
        /// </summary>
        static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "?")
            {
                if (char.IsControl(c)) continue;
                sb.Append(c == ';' ? ',' : c == '|' ? '/' : c == ':' ? ' ' : c);
            }
            string r = sb.ToString().Trim().TrimStart('=', '+', '-', '@').Trim();
            if (r.Length > 60) r = r.Substring(0, 60);
            return r.Length == 0 ? "?" : r;
        }

        public static void Append(DateTime date, MinuteRecord r)
        {
            lock (sync)
            {
                try
                {
                    string path = PathFor(date);
                    var sb = new StringBuilder();
                    if (!File.Exists(path)) sb.Append("minuto;laeq_db;max_db;segundos_com_som;apps").Append('\n');
                    sb.Append((r.Minute / 60).ToString("00")).Append(':').Append((r.Minute % 60).ToString("00")).Append(';');
                    sb.Append(r.Leq.ToString("0.0", Inv)).Append(';');
                    sb.Append(r.Max.ToString("0.0", Inv)).Append(';');
                    sb.Append(r.Active.ToString(Inv)).Append(';');
                    bool first = true;
                    foreach (var kv in r.Apps.OrderByDescending(k => k.Value).Take(4))
                    {
                        if (!first) sb.Append('|');
                        sb.Append(Clean(kv.Key)).Append(':').Append(kv.Value.ToString("0.00", Inv));
                        first = false;
                    }
                    sb.Append('\n');
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                        fs.Write(bytes, 0, bytes.Length);
                    }
                }
                catch (Exception ex) { Log.Write("Falha ao gravar histórico: " + ex.Message); }
            }
        }

        public static DayData LoadDay(DateTime date)
        {
            var day = new DayData { Date = date.Date };
            string path = PathFor(date);
            if (!File.Exists(path)) return day;
            try
            {
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
                foreach (var raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("minuto")) continue;
                    var p = line.Split(';');
                    if (p.Length < 4) continue;
                    var hm = p[0].Split(':');
                    int h, m;
                    if (hm.Length != 2 || !int.TryParse(hm[0], out h) || !int.TryParse(hm[1], out m)) continue;
                    var rec = new MinuteRecord { Minute = h * 60 + m };
                    if (!double.TryParse(p[1], NumberStyles.Float, Inv, out rec.Leq)) continue;
                    double.TryParse(p[2], NumberStyles.Float, Inv, out rec.Max);
                    int.TryParse(p[3], out rec.Active);
                    if (p.Length > 4 && p[4].Length > 0)
                    {
                        foreach (var a in p[4].Split('|'))
                        {
                            int idx = a.LastIndexOf(':');
                            double f;
                            if (idx > 0 && double.TryParse(a.Substring(idx + 1), NumberStyles.Float, Inv, out f)) rec.Apps[a.Substring(0, idx)] = f;
                        }
                    }
                    day.Minutes.Add(rec);
                }
            }
            catch (Exception ex) { Log.Write("Falha ao ler histórico: " + ex.Message); }
            return day;
        }
    }

    /// <summary>Acumula níveis de 1 s, grava minutos no histórico e calcula doses. Uso só na thread do motor.</summary>
    public class ExposureTracker
    {
        public const int RecentSeconds = 600;

        int curMinute = -1;
        DateTime today;
        MinuteRecord cur;
        double dayEnergy, dayMax = double.NegativeInfinity;
        int dayActive;
        double prevDaysEnergy;
        readonly double[] recent = new double[RecentSeconds];
        int recentPos;
        int doseAlertLevel;

        public ExposureTracker()
        {
            for (int i = 0; i < recent.Length; i++) recent[i] = double.NaN;
        }

        public void Init(DateTime now)
        {
            today = now.Date;
            var d = HistoryStore.LoadDay(today);
            dayEnergy = d.Energy; dayActive = d.ActiveSeconds; dayMax = d.Max;
            LoadPrevDays();
            double dose = DailyDose;
            doseAlertLevel = dose >= 100 ? 3 : dose >= 80 ? 2 : dose >= 50 ? 1 : 0;
        }

        void LoadPrevDays()
        {
            prevDaysEnergy = 0;
            for (int i = 1; i <= 6; i++) prevDaysEnergy += HistoryStore.LoadDay(today.AddDays(-i)).Energy;
        }

        public double DailyDose { get { return Hearing.DailyDosePercent(dayEnergy + (cur != null ? cur.Energy : 0)); } }
        public double WeeklyDose { get { return Hearing.WeeklyDosePercent(prevDaysEnergy + dayEnergy + (cur != null ? cur.Energy : 0)); } }
        public int DayActiveSeconds { get { return dayActive + (cur != null ? cur.Active : 0); } }
        public double DayLeq { get { return Hearing.LeqFromEnergy(dayEnergy + (cur != null ? cur.Energy : 0), DayActiveSeconds); } }
        public double DayMax { get { return Math.Max(dayMax, cur != null ? cur.Max : double.NegativeInfinity); } }

        /// <summary>Retorna 50/80/100 quando a dose diária cruza um desses marcos pela primeira vez no dia.</summary>
        public int CheckDoseMilestone()
        {
            double dose = DailyDose;
            int lvl = dose >= 100 ? 3 : dose >= 80 ? 2 : dose >= 50 ? 1 : 0;
            if (lvl > doseAlertLevel) { doseAlertLevel = lvl; return lvl == 3 ? 100 : lvl == 2 ? 80 : 50; }
            return 0;
        }

        public void AddSecond(DateTime t, double spl, bool counts, Dictionary<string, double> appShares)
        {
            if (t.Date != today)
            {
                Flush();
                today = t.Date;
                dayEnergy = 0; dayActive = 0; dayMax = double.NegativeInfinity; doseAlertLevel = 0;
                LoadPrevDays();
            }
            int minute = t.Hour * 60 + t.Minute;
            if (minute != curMinute) { Flush(); curMinute = minute; cur = new MinuteRecord { Minute = minute, Leq = double.NegativeInfinity, Max = double.NegativeInfinity }; }

            bool sound = counts && !double.IsInfinity(spl) && !double.IsNaN(spl);
            recent[recentPos] = sound ? spl : double.NaN;
            recentPos = (recentPos + 1) % recent.Length;
            if (!sound) return;

            double e = Hearing.Energy(spl);
            double prevEnergy = cur.Active > 0 ? cur.Energy : 0;
            // guarda as frações de apps como energia absoluta e normaliza no Flush
            if (appShares != null)
                foreach (var kv in appShares)
                {
                    double v; cur.Apps.TryGetValue(kv.Key, out v);
                    cur.Apps[kv.Key] = v + e * kv.Value;
                }
            cur.Active++;
            cur.Leq = Hearing.LeqFromEnergy(prevEnergy + e, cur.Active);
            cur.Max = Math.Max(cur.Max, spl);
        }

        public void Flush()
        {
            if (cur == null || cur.Active == 0) { cur = null; return; }
            double total = cur.Apps.Values.Sum();
            if (total > 0) foreach (var k in cur.Apps.Keys.ToList()) cur.Apps[k] = cur.Apps[k] / total;
            HistoryStore.Append(today, cur);
            dayEnergy += cur.Energy; dayActive += cur.Active; dayMax = Math.Max(dayMax, cur.Max);
            cur = null;
        }

        /// <summary>LAeq dos últimos N segundos (considerando silêncio como silêncio).</summary>
        public double LeqLast(int seconds)
        {
            double e = 0; int n = 0;
            for (int i = 1; i <= seconds && i <= recent.Length; i++)
            {
                double v = recent[(recentPos - i + recent.Length) % recent.Length];
                n++;
                if (!double.IsNaN(v)) e += Hearing.Energy(v);
            }
            return Hearing.LeqFromEnergy(e, n);
        }

        /// <summary>Quantos dos últimos N segundos ficaram em pelo menos <paramref name="threshold"/> dB.</summary>
        public int SecondsAtOrAbove(int seconds, double threshold)
        {
            int n = 0;
            for (int i = 1; i <= seconds && i <= recent.Length; i++)
            {
                double v = recent[(recentPos - i + recent.Length) % recent.Length];
                if (!double.IsNaN(v) && v >= threshold) n++;
            }
            return n;
        }

        public double[] RecentCopy()
        {
            var r = new double[recent.Length];
            for (int i = 0; i < recent.Length; i++) r[i] = recent[(recentPos + i) % recent.Length];
            return r;
        }
    }
}
