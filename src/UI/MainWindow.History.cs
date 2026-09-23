using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using VolumeGuard.Core;

namespace VolumeGuard.UI
{
    public sealed partial class MainWindow
    {
        DateTime histDay = DateTime.Today;
        DateTime lastHistRefresh = DateTime.MinValue;
        TextBlock dayTitle, statDose, statListen, statLeq, statMax, statLoud, topAppsEmpty;
        DayChart dayChart;
        WeekChart weekChart;
        ItemsControl topApps;
        Button nextDay;

        void InitHistory()
        {
            var p = pageHistory;
            dayTitle = Xaml.Find<TextBlock>(p, "DayTitle");
            statDose = Xaml.Find<TextBlock>(p, "StatDose");
            statListen = Xaml.Find<TextBlock>(p, "StatListen");
            statLeq = Xaml.Find<TextBlock>(p, "StatLeq");
            statMax = Xaml.Find<TextBlock>(p, "StatMax");
            statLoud = Xaml.Find<TextBlock>(p, "StatLoud");
            dayChart = Xaml.Find<DayChart>(p, "DayChart");
            weekChart = Xaml.Find<WeekChart>(p, "WeekChart");
            topApps = Xaml.Find<ItemsControl>(p, "TopApps");
            topAppsEmpty = Xaml.Find<TextBlock>(p, "TopAppsEmpty");
            nextDay = Xaml.Find<Button>(p, "NextDay");
            Xaml.Find<Button>(p, "PrevDay").Click += (s, a) => { histDay = histDay.AddDays(-1); RefreshHistory(); };
            nextDay.Click += (s, a) => { if (histDay < DateTime.Today) histDay = histDay.AddDays(1); RefreshHistory(); };
            Xaml.Find<Button>(p, "TodayButton").Click += (s, a) => { histDay = DateTime.Today; RefreshHistory(); };
            weekChart.DayClicked += d => { histDay = d.Date; RefreshHistory(); };

            var legend = Xaml.Find<StackPanel>(p, "ZoneLegend");
            string[] labels = { "Seguro <75", "Moderado 75–85", "Alto 85–94", "Perigoso ≥94" };
            for (int z = 0; z < 4; z++)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0) };
                sp.Children.Add(new Rectangle { Width = 9, Height = 9, RadiusX = 2, RadiusY = 2, Fill = Palette.Zone(z), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = labels[z], Foreground = Palette.InkSecondary, FontSize = 11.5, Margin = new Thickness(6, 0, 0, 0) });
                legend.Children.Add(sp);
            }
        }

        void TickHistory()
        {
            if (histDay == DateTime.Today && (DateTime.Now - lastHistRefresh).TotalSeconds > 30) RefreshHistory();
        }

        void RefreshHistory()
        {
            if (dayChart == null) return;
            lastHistRefresh = DateTime.Now;
            if (histDay > DateTime.Today) histDay = DateTime.Today;
            var day = HistoryStore.LoadDay(histDay);
            string when = histDay.ToString("dddd, d 'de' MMMM", Fmt.Br);
            if (histDay == DateTime.Today) when = "Hoje · " + histDay.ToString("d 'de' MMMM", Fmt.Br);
            else if (histDay == DateTime.Today.AddDays(-1)) when = "Ontem · " + histDay.ToString("d 'de' MMMM", Fmt.Br);
            else when = char.ToUpper(when[0]) + when.Substring(1);
            dayTitle.Text = when;
            nextDay.IsEnabled = histDay < DateTime.Today;

            double energy = day.Energy;
            int active = day.ActiveSeconds;
            statDose.Text = active > 0 ? Fmt.Percent(Hearing.DailyDosePercent(energy)) : "—";
            statListen.Text = active > 0 ? Hearing.FormatDuration(active) : "—";
            statLeq.Text = Fmt.Spl(Hearing.LeqFromEnergy(energy, active));
            statMax.Text = Fmt.Spl(day.Max);
            int loud = day.Minutes.Where(m => m.Leq >= 85).Sum(m => m.Active);
            statLoud.Text = active > 0 ? Hearing.FormatDuration(loud) : "—";
            dayChart.SetDay(day);

            DateTime end = histDay.AddDays(13) > DateTime.Today ? DateTime.Today : histDay.AddDays(6);
            if (end < histDay) end = histDay;
            var days = new List<WeekChart.Day>();
            for (int i = 13; i >= 0; i--)
            {
                var date = end.AddDays(-i);
                var d = date == histDay ? day : HistoryStore.LoadDay(date);
                double e = d.Energy;
                int a = d.ActiveSeconds;
                days.Add(new WeekChart.Day { Date = date, Dose = Hearing.DailyDosePercent(e), Active = a, Leq = Hearing.LeqFromEnergy(e, a) });
            }
            weekChart.SetData(days, histDay);

            var apps = day.AppEnergies();
            double total = apps.Sum(kv => kv.Value);
            topApps.ItemsSource = apps.Take(6).Select(kv => new TopAppVM
            {
                Name = kv.Key,
                Share = total > 0 ? kv.Value / total : 0,
                ShareText = total > 0 ? Fmt.Percent(kv.Value / total * 100) : "",
            }).ToList();
            topAppsEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
