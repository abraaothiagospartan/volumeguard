using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using VolumeGuard.Audio;
using VolumeGuard.Core;
using VolumeGuard.UI;

// P/Invokes só procuram DLLs no System32 (evita carregar uma DLL falsa colocada ao lado do .exe)
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace VolumeGuard
{
    public static class Program
    {
        const string MutexName = "VolumeGuard.SingleInstance.5b0d3c6e";
        internal const string ShowEventName = "VolumeGuard.Show.5b0d3c6e";
        /// <summary>Usado pelo instalador para pedir que o app feche antes de atualizar/desinstalar.</summary>
        internal const string ExitEventName = "VolumeGuard.Exit.5b0d3c6e";

        [STAThread]
        public static void Main(string[] args)
        {
            Native.HardenDllSearch();
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // já está rodando: pede para a instância aberta mostrar a janela
                    try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); } catch { }
                    return;
                }
                var app = new VolumeGuardApp(args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)));
                app.Run();
                GC.KeepAlive(mutex);
            }
        }
    }

    public sealed class VolumeGuardApp : Application
    {
        readonly bool startMinimized;
        AudioEngine engine;
        UI.MainWindow window;
        Tray tray;
        AlertWindow alert;
        volatile EngineSnapshot latest;
        int pending;
        bool hintShown, exiting;
        EventWaitHandle showEvent, exitEvent;

        public VolumeGuardApp(bool minimized) { startMinimized = minimized; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DispatcherUnhandledException += (s, ex) => { Log.Write("UI: " + ex.Exception); ex.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => Log.Write("Fatal: " + ex.ExceptionObject);

            Resources.MergedDictionaries.Add((ResourceDictionary)Xaml.Load("Theme.xaml"));
            SettingsStore.Load();
            StartupManager.RefreshPath();

            engine = new AudioEngine { UiActive = false };
            alert = new AlertWindow(engine);
            tray = new Tray();

            alert.OpenHistory += () => ShowMain("history");
            tray.OpenRequested += () => ShowMain(null);
            tray.ExitRequested += ExitApp;
            tray.ModeRequested += m =>
            {
                if (window != null) window.SetMode((VolumeMode)m);
                else UI.MainWindow.ApplyMode(engine, (VolumeMode)m);
            };
            tray.PauseRequested += min => engine.Pause(min);
            tray.ResumeRequested += () => engine.Resume();

            engine.SnapshotReady += s =>
            {
                latest = s;
                if (Interlocked.Exchange(ref pending, 1) == 0) Dispatcher.BeginInvoke(new Action(DeliverSnapshot));
            };
            engine.AlertRaised += a => Dispatcher.BeginInvoke(new Action(() => alert.Handle(a)));
            engine.Start();

            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
            var signals = new Thread(() =>
            {
                while (true)
                {
                    int which = WaitHandle.WaitAny(new WaitHandle[] { showEvent, exitEvent });
                    if (which == 1) { Dispatcher.BeginInvoke(new Action(ExitApp)); return; }
                    Dispatcher.BeginInvoke(new Action(() => ShowMain(null)));
                }
            }) { IsBackground = true, Name = "VolumeGuard signals" };
            signals.Start();

            var st = SettingsStore.Current;
            bool firstRun = !st.FirstRunDone;
            if (firstRun) { st.FirstRunDone = true; SettingsStore.MarkDirty(); }
            if (firstRun || !(startMinimized || st.StartMinimized)) ShowMain(null);
            else TrimMemory();
        }

        /// <summary>A janela só existe enquanto está aberta: fechada, o app fica só com o motor e a bandeja.</summary>
        void ShowMain(string page)
        {
            if (exiting) return;
            if (window == null)
            {
                window = new UI.MainWindow(engine);
                window.Closing += (s, a) =>
                {
                    if (exiting || hintShown) return;
                    hintShown = true;
                    tray.ShowBalloon("O VolumeGuard continua protegendo", "Ele fica aqui na bandeja. Clique no ícone para abrir; botão direito para sair.");
                };
                window.Closed += (s, a) => { window = null; engine.UiActive = false; TrimMemory(); };
                window.StateChanged += (s, a) =>
                {
                    if (window == null) return;
                    bool visible = window.WindowState != WindowState.Minimized;
                    engine.UiActive = visible;
                    if (!visible) TrimMemory();
                };
                if (latest != null) window.OnSnapshot(latest);
            }
            engine.UiActive = true;
            window.ShowAndActivate(page);
        }

        /// <summary>Devolve ao Windows a memória que a interface usou (o app volta a ocupar só alguns MB).</summary>
        void TrimMemory()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Native.TrimWorkingSet();
            }));
        }

        void DeliverSnapshot()
        {
            Interlocked.Exchange(ref pending, 0);
            var s = latest;
            if (s == null) return;
            if (window != null) window.OnSnapshot(s);

            double lvl = s.LevelFast;
            bool sounding = !double.IsInfinity(lvl) && lvl > 20;
            int zone = sounding ? Hearing.Zone(lvl) : 0;
            string levelText = sounding ? Fmt.Spl(lvl) : "silêncio";
            string statusText = s.Paused ? "Pausado até " + s.PausedUntil.ToString("HH:mm") : levelText + " agora · dose de hoje " + Fmt.Percent(s.DailyDose);
            string tip = "VolumeGuard · " + (s.Paused ? "pausado" : levelText) + " · dose " + Fmt.Percent(s.DailyDose);
            tray.Update(zone, sounding, s.Paused, SettingsStore.Current.Mode, statusText, tip);
        }

        void ExitApp()
        {
            if (exiting) return;
            exiting = true;
            try { engine.Stop(); } catch (Exception ex) { Log.Write("Parar motor: " + ex); }
            tray.Dispose();
            SettingsStore.Save();
            if (window != null) window.Close();
            alert.Close();
            Shutdown();
        }
    }
}
