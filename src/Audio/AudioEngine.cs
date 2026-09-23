using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using VolumeGuard.Core;

namespace VolumeGuard.Audio
{
    public class AppSnapshot
    {
        public string Key, Name, ExePath;
        public bool IsSystem;
        public int SessionCount;
        public double VolumeDb;      // volume aplicado ao app (dB)
        public double LimiterDb;     // quanto o limitador está tirando
        public double OutSpl;        // nível estimado que o app está produzindo agora
        public bool Sounding, Muted;
        public int RuleMode;         // AppRuleMode
        public int EffectiveMode;    // 0 fixo, 1 alvo, 2 ignorado
        public double OffsetDb;
        public bool AtMaximum;
    }

    public class EngineSnapshot
    {
        public bool HasDevice;
        public string Error;
        public string DeviceId, DeviceName;
        public double CalSpl;
        public bool CountExposure, DeviceConfigured;
        public int MasterMode;
        public double MasterDb, MasterMinDb, MasterMaxDb, MasterStepDb;
        public bool MasterMuted;
        public double LevelFast, Level1s;
        public List<AppSnapshot> Apps = new List<AppSnapshot>();
        public double DailyDose, WeeklyDose, DayLeq, DayMax;
        public int DayActive;
        public double[] Live;        // nível rápido dos últimos 60 s, 10 por segundo
        public bool Paused;
        public DateTime PausedUntil;
        public bool AlertActive;
    }

    public enum AlertKind { Loud, LoudUpdate, LoudEnded, Dose }

    public class AlertInfo
    {
        public AlertKind Kind;
        public double Level;
        public string AppName;
        public double Dose;
        public int Milestone;
    }

    class SessionEntry
    {
        public string InstanceId;
        public int Pid;
        public string AppKey;
        public bool IsOwn, FromStartup;
        public double CreatedAt;
        public IAudioSessionControl2 Control;
        public ISimpleAudioVolume Volume;
        public IAudioMeterInformation Meter;
        public SessionEvents Events;
        public float CurVol = 1;
        public bool Muted;
        public double QEma;
    }

    class AppState
    {
        public string Key, Name, ExePath;
        public bool IsSystem;
        public AppRule Rule;
        public readonly List<SessionEntry> Sessions = new List<SessionEntry>();
        public double FixedDb;
        public bool FixedFromDefault;
        public double AgcDb, OffsetDb;
        public bool Leveled, AtMaximum;
        public double LimiterRedDb, LastCut = -100;
        public double AppliedDb = double.NaN;
        public double SlowPow, SoundTime, InitSum;
        public int InitCount;
        public bool SlowInit;

        /// <summary>Recomeça a medição do nível alvo (a próxima medida define o ganho de uma vez).</summary>
        public void Unlevel() { Leveled = false; InitSum = 0; InitCount = 0; SoundTime = 0; SlowInit = false; }
        // medidas do tick atual
        public double Q, Share, MaxPeakPre, MaxOutPeak;
        public bool Sounding, AllMuted;
    }

    /// <summary>
    /// Motor de áudio: roda numa thread MTA própria (exigência da Core Audio para receber notificações de sessão),
    /// acompanha o dispositivo padrão, as sessões de cada app, mede o nível real via loopback e aplica
    /// volume inicial, nível alvo, limitador e alertas.
    /// </summary>
    public sealed class AudioEngine
    {
        public static readonly Guid Ctx = new Guid("8f1b2a4e-5d43-4c1e-9b7a-2f6c3e8d1a55");
        public const double MinDb = -60;
        const double NewSessionGraceSeconds = 6;

        public event Action<EngineSnapshot> SnapshotReady;
        public event Action<AlertInfo> AlertRaised;

        Thread thread;
        volatile bool stopping;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();
        readonly int ownPid = Process.GetCurrentProcess().Id;
        readonly Stopwatch clock = new Stopwatch();

        IMMDeviceEnumerator enumerator;
        DeviceNotifier deviceNotifier;
        volatile bool deviceDirty = true;
        double nextDeviceTry;
        IMMDevice device;
        string deviceId, deviceName;
        DeviceProfile profile;
        IAudioEndpointVolume endpoint;
        IAudioSessionManager2 manager;
        SessionNotifier sessionNotifier;
        LoopbackMeter loopback;
        string lastError;
        double masterDb, masterMin = -65, masterMax = 0, masterInc = 0.5;
        bool masterMuted;

        readonly Dictionary<string, SessionEntry> sessions = new Dictionary<string, SessionEntry>();
        readonly Dictionary<string, AppState> apps = new Dictionary<string, AppState>(StringComparer.OrdinalIgnoreCase);

        double fastPow, medPow;
        double secSumSq, secStart;
        long secFrames;
        readonly Dictionary<string, double> secApps = new Dictionary<string, double>();
        double lastSecondSpl = double.NegativeInfinity;
        string lastDominantApp;
        double limiterHoldUntil;
        readonly ExposureTracker exposure = new ExposureTracker();
        bool alertActive;
        double alertSnoozeUntil, pausedUntil;
        DateTime pausedUntilWall;
        readonly double[] live = new double[600];
        int livePos;
        double nextLiveSample;
        double detWinStart, detLbPeak, detTop, detSecond;
        int votesPre, votesPost;

        // sem som por alguns segundos: o laço roda a 10 Hz em vez de 50 Hz (quase zero CPU na bandeja)
        double lastActivity;
        const double IdleAfterSeconds = 3;

        /// <summary>A janela está aberta: publica 10 vezes por segundo e manda o gráfico ao vivo; senão, 2 por segundo.</summary>
        public volatile bool UiActive = true;

        double Now { get { return clock.Elapsed.TotalSeconds; } }

        public void Start()
        {
            for (int i = 0; i < live.Length; i++) live[i] = double.NaN;
            thread = new Thread(Run) { IsBackground = true, Name = "VolumeGuard audio" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop()
        {
            stopping = true;
            wake.Set();
            if (thread != null) thread.Join(4000);
        }

        public void Post(Action a) { queue.Enqueue(a); wake.Set(); }

        // ---------------------------------------------------------------- API (thread-safe, via fila)

        public void SetMasterDb(double db)
        {
            Post(() =>
            {
                if (endpoint == null) return;
                Guid g = Ctx;
                endpoint.SetMasterVolumeLevel((float)Clamp(db, masterMin, masterMax), ref g);
                ReadMaster();
            });
        }

        public void SetMasterMute(bool mute)
        {
            Post(() => { if (endpoint == null) return; Guid g = Ctx; endpoint.SetMute(mute, ref g); ReadMaster(); });
        }

        public void SetAppVolumeDb(string key, double db)
        {
            Post(() => { AppState app; if (apps.TryGetValue(key, out app)) ApplyUserVolume(app, db); });
        }

        public void SetAppRuleMode(string key, string name, string path, AppRuleMode mode)
        {
            Post(() => ChangeRuleMode(key, name, path, mode));
        }

        public void ForgetRule(string key)
        {
            Post(() =>
            {
                SettingsStore.RemoveRule(key);
                AppState app;
                if (apps.TryGetValue(key, out app))
                {
                    app.Rule = null;
                    ResetAppToDefault(app);
                }
            });
        }

        public void ResetAllToDefault()
        {
            Post(() =>
            {
                lock (SettingsStore.Sync)
                {
                    foreach (var r in SettingsStore.Current.Apps)
                    {
                        if (r.Mode == (int)AppRuleMode.Fixed) continue;
                        r.HasVolume = false; r.TargetOffsetDb = 0; r.HasAgc = false;
                    }
                }
                SettingsStore.MarkDirty();
                foreach (var app in apps.Values)
                    if (EffectiveMode(app) != 2 && (app.Rule == null || app.Rule.Mode != (int)AppRuleMode.Fixed)) ResetAppToDefault(app);
            });
        }

        /// <summary>Chamar depois de mudar configurações globais (modo, volume padrão etc.).</summary>
        public void SettingsChanged(int previousMode)
        {
            Post(() =>
            {
                var s = SettingsStore.Current;
                foreach (var app in apps.Values)
                {
                    if (s.Mode != previousMode)
                    {
                        if (s.Mode == (int)VolumeMode.Target)
                        {
                            app.AgcDb = double.IsNaN(app.AppliedDb) ? s.InitialVolumeDb : app.AppliedDb + app.LimiterRedDb;
                            app.Unlevel();
                        }
                        else
                        {
                            bool remembered = app.Rule != null && app.Rule.HasVolume && s.RememberPerApp;
                            app.FixedDb = remembered ? app.Rule.VolumeDb : s.InitialVolumeDb;
                            app.FixedFromDefault = !remembered;
                        }
                    }
                    else if (app.FixedFromDefault) app.FixedDb = s.InitialVolumeDb;
                }
            });
        }

        public void Pause(double minutes)
        {
            Post(() =>
            {
                pausedUntil = Now + minutes * 60;
                pausedUntilWall = DateTime.Now.AddMinutes(minutes);
                // pausado = não interferir: tira o corte do limitador e volta ao volume escolhido
                foreach (var app in apps.Values)
                {
                    int mode = EffectiveMode(app);
                    if (app.LimiterRedDb > 0 && mode != 2) SetAppVolume(app, Clamp(DesiredDb(app, mode), MinDb, 0));
                    app.LimiterRedDb = 0;
                }
                if (alertActive) EndAlert();
            });
        }

        public void Resume() { Post(() => { pausedUntil = 0; }); }

        public void SnoozeAlerts(double minutes)
        {
            Post(() => { alertSnoozeUntil = Now + minutes * 60; if (alertActive) EndAlert(); });
        }

        /// <summary>Botão "Baixar agora" do alerta: tira dos apps que estão tocando o que passa do nível seguro.</summary>
        public void ReduceNow()
        {
            Post(() =>
            {
                var s = SettingsStore.Current;
                double current = Math.Max(ToSpl(medPow), exposure.LeqLast(3));
                if (double.IsInfinity(current)) return;
                double delta = current - (s.AlertSpl - 10);
                if (delta <= 0) return;
                double maxShare = apps.Values.Select(a => a.Share).DefaultIfEmpty(0).Max();
                foreach (var app in apps.Values.ToList())
                {
                    if (app.Share < 0.25 * maxShare || app.Share < 0.02) continue;
                    double cur = double.IsNaN(app.AppliedDb) ? CurrentDb(app) : app.AppliedDb;
                    if (EffectiveMode(app) == 2) SetAppVolume(app, Clamp(cur - delta, MinDb, 0));
                    else ApplyUserVolume(app, cur - delta);
                }
                fastPow *= Math.Pow(10, -delta / 10);
                medPow *= Math.Pow(10, -delta / 10);
                if (alertActive) EndAlert();
            });
        }

        // ---------------------------------------------------------------- callbacks COM (qualquer thread)

        internal void OnDeviceEvent(string id, bool defaultChanged)
        {
            if (defaultChanged || id == null || id == deviceId) { deviceDirty = true; wake.Set(); }
        }

        internal void OnNewSession(object session)
        {
            Post(() =>
            {
                var c = session as IAudioSessionControl2;
                if (c == null || manager == null) { CoreAudio.SafeRelease(session); return; }
                string inst;
                if (c.GetSessionInstanceIdentifier(out inst) < 0 || inst == null || sessions.ContainsKey(inst)) { CoreAudio.SafeRelease(c); return; }
                lastActivity = Now;
                AddSession(c, inst, false);
            });
        }

        internal void OnSessionVolume(string inst, float vol, bool mute)
        {
            Post(() => OnExternalVolume(inst, vol, mute));
        }

        internal void OnSessionGone(string inst)
        {
            Post(() => RemoveSession(inst));
        }

        // ---------------------------------------------------------------- laço principal

        void Run()
        {
            clock.Start();
            try { exposure.Init(DateTime.Now); } catch (Exception ex) { Log.Write("Histórico: " + ex); }
            try
            {
                enumerator = CoreAudio.CreateEnumerator();
                deviceNotifier = new DeviceNotifier(this);
                enumerator.RegisterEndpointNotificationCallback(deviceNotifier);
            }
            catch (Exception ex) { lastError = "Core Audio indisponível: " + ex.Message; Log.Write(lastError); }

            double last = Now, nextSnapshot = 0, nextRefresh = 0, nextMaster = 0, nextSave = 5, nextVolRead = 0;
            secStart = Now;
            while (!stopping)
            {
                bool idle = Now - lastActivity > IdleAfterSeconds;
                wake.WaitOne(idle ? 100 : 20);
                double now = Now;
                double dt = Math.Min(0.5, Math.Max(0, now - last));
                last = now;
                Action a;
                while (queue.TryDequeue(out a))
                {
                    try { a(); } catch (Exception ex) { HandleError(ex); }
                }
                try
                {
                    if (deviceDirty && enumerator != null && now >= nextDeviceTry)
                    {
                        OpenDevice();
                        if (device == null) nextDeviceTry = now + 2;
                    }
                    if (device != null)
                    {
                        if (now >= nextMaster) { ReadMaster(); nextMaster = now + 0.1; }
                        if (now >= nextRefresh) { RefreshSessions(false); nextRefresh = now + (idle ? 5 : 2); }
                        bool readVols = now >= nextVolRead;
                        if (readVols) nextVolRead = now + 0.5;
                        Tick(now, dt, readVols);
                    }
                    SecondBlock(now);
                    if (now >= nextLiveSample)
                    {
                        live[livePos] = ToSpl(fastPow);
                        livePos = (livePos + 1) % live.Length;
                        nextLiveSample = now + 0.1;
                    }
                    if (now >= nextSnapshot) { Publish(); nextSnapshot = now + (UiActive ? 0.1 : 0.5); }
                    if (now >= nextSave) { SettingsStore.SaveIfDirty(); nextSave = now + 5; }
                }
                catch (Exception ex) { HandleError(ex); }
            }
            try { exposure.Flush(); } catch { }
            foreach (var app in apps.Values) RememberAgc(app);
            CloseDevice();
            try { if (enumerator != null && deviceNotifier != null) enumerator.UnregisterEndpointNotificationCallback(deviceNotifier); } catch { }
            SettingsStore.SaveIfDirty();
        }

        void HandleError(Exception ex)
        {
            Log.Write("Motor: " + ex);
            if (ex is COMException || ex is InvalidCastException || ex is InvalidComObjectException)
            {
                lastError = "O dispositivo de áudio mudou ou ficou indisponível. Reconectando…";
                CloseDevice();
                deviceDirty = true;
                nextDeviceTry = Now + 1;
            }
        }

        // ---------------------------------------------------------------- dispositivo

        void OpenDevice()
        {
            CloseDevice();
            deviceDirty = false;
            IMMDevice d;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out d) < 0 || d == null)
            {
                lastError = "Nenhuma saída de áudio encontrada.";
                deviceDirty = true;
                return;
            }
            device = d;
            device.GetId(out deviceId);
            deviceName = CoreAudio.GetFriendlyName(device);
            profile = SettingsStore.GetOrCreateDevice(deviceId, deviceName, SuggestPreset(device));

            endpoint = CoreAudio.Activate<IAudioEndpointVolume>(device, CoreAudio.IID_IAudioEndpointVolume);
            float mn, mx, inc;
            if (endpoint.GetVolumeRange(out mn, out mx, out inc) >= 0 && mx > mn) { masterMin = mn; masterMax = mx; masterInc = inc > 0 ? inc : 0.5; }
            int hw;
            // volume do dispositivo feito no hardware: o loopback nunca "vê" o volume mestre
            if (endpoint.QueryHardwareSupport(out hw) >= 0 && (hw & 1) != 0 && profile.MasterMode == (int)MasterMode.Unknown)
            {
                profile.MasterMode = (int)MasterMode.PreMaster;
                SettingsStore.MarkDirty();
            }
            ReadMaster();

            manager = CoreAudio.Activate<IAudioSessionManager2>(device, CoreAudio.IID_IAudioSessionManager2);
            IAudioSessionEnumerator en;
            if (manager.GetSessionEnumerator(out en) >= 0) CoreAudio.SafeRelease(en); // necessário antes de registrar
            sessionNotifier = new SessionNotifier(this);
            manager.RegisterSessionNotification(sessionNotifier);

            loopback = new LoopbackMeter();
            try { loopback.Start(device); lastError = null; }
            catch (Exception ex)
            {
                Log.Write("Loopback: " + ex);
                lastError = "Não foi possível medir o som deste dispositivo (" + ex.Message + ").";
                loopback.Dispose();
                loopback = null;
            }
            fastPow = medPow = 0;
            votesPre = votesPost = 0;
            RefreshSessions(true);
            Log.Write("Dispositivo: " + deviceName);
        }

        static string SuggestPreset(IMMDevice d)
        {
            string en = (CoreAudio.GetStringProperty(d, CoreAudio.PKEY_Device_EnumeratorName) ?? "").ToUpperInvariant();
            int ff = CoreAudio.GetUIntProperty(d, CoreAudio.PKEY_AudioEndpoint_FormFactor, -1);
            if (en.Contains("BTH")) return "bt";
            if (en == "USB") return "usb";
            if (ff == 8 || ff == 9) return "speakers"; // SPDIF / HDMI
            return "wired";
        }

        void CloseDevice()
        {
            foreach (var app in apps.Values) RememberAgc(app);
            foreach (var se in sessions.Values) ReleaseSession(se);
            sessions.Clear();
            apps.Clear();
            try { if (manager != null && sessionNotifier != null) manager.UnregisterSessionNotification(sessionNotifier); } catch { }
            sessionNotifier = null;
            if (loopback != null) { loopback.Dispose(); loopback = null; }
            CoreAudio.SafeRelease(manager); manager = null;
            CoreAudio.SafeRelease(endpoint); endpoint = null;
            CoreAudio.SafeRelease(device); device = null;
        }

        void ReadMaster()
        {
            if (endpoint == null) return;
            float db; bool mute;
            if (endpoint.GetMasterVolumeLevel(out db) >= 0) masterDb = db;
            if (endpoint.GetMute(out mute) >= 0) masterMuted = mute;
        }

        // ---------------------------------------------------------------- sessões

        void RefreshSessions(bool startup)
        {
            if (manager == null) return;
            IAudioSessionEnumerator en;
            if (manager.GetSessionEnumerator(out en) < 0 || en == null) return;
            var seen = new HashSet<string>();
            try
            {
                int n;
                en.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IAudioSessionControl2 c;
                    if (en.GetSession(i, out c) < 0 || c == null) continue;
                    string inst;
                    if (c.GetSessionInstanceIdentifier(out inst) < 0 || inst == null) { CoreAudio.SafeRelease(c); continue; }
                    AudioSessionState st;
                    c.GetState(out st);
                    if (st == AudioSessionState.Expired) { CoreAudio.SafeRelease(c); continue; }
                    seen.Add(inst);
                    if (sessions.ContainsKey(inst)) { CoreAudio.SafeRelease(c); continue; }
                    AddSession(c, inst, startup);
                }
            }
            finally { CoreAudio.SafeRelease(en); }
            foreach (var kv in sessions.ToList())
                if (!seen.Contains(kv.Key) || (!kv.Value.IsOwn && !ProcessInfo.IsAlive(kv.Value.Pid))) RemoveSession(kv.Key);
        }

        void AddSession(IAudioSessionControl2 c, string inst, bool startup)
        {
            int pid;
            c.GetProcessId(out pid);
            bool isSystem = c.IsSystemSoundsSession() == 0;
            var vol = c as ISimpleAudioVolume;
            var meter = c as IAudioMeterInformation;
            if (vol == null || meter == null) { CoreAudio.SafeRelease(c); return; }
            if (!isSystem && pid > 0 && pid != ownPid && !ProcessInfo.IsAlive(pid)) { CoreAudio.SafeRelease(c); return; }

            var se = new SessionEntry { InstanceId = inst, Pid = pid, Control = c, Volume = vol, Meter = meter, CreatedAt = Now, FromStartup = startup };
            float v; bool m;
            if (vol.GetMasterVolume(out v) >= 0) se.CurVol = v;
            if (vol.GetMute(out m) >= 0) se.Muted = m;
            se.Events = new SessionEvents(this, inst);
            c.RegisterAudioSessionNotification(se.Events);
            sessions[inst] = se;

            if (pid == ownPid)
            {
                // a captura do medidor pertence a esta sessão: precisa ficar em 100% para medir certo
                se.IsOwn = true;
                KeepOwnSessionAtFull(se);
                try { Guid g = Ctx; c.SetDisplayName("VolumeGuard (medidor — deixe em 100%)", ref g); } catch { }
                return;
            }

            string key, name, path;
            ProcessInfo.Describe(pid, isSystem, out key, out name, out path);
            se.AppKey = key;
            AppState app;
            if (!apps.TryGetValue(key, out app))
            {
                app = CreateApp(key, name, path, isSystem, startup, se.CurVol);
                apps[key] = app;
            }
            app.Sessions.Add(se);

            int mode = EffectiveMode(app);
            if (mode == 2) return;
            if (Now < pausedUntil && double.IsNaN(app.AppliedDb)) return;
            double applied = Clamp(DesiredDb(app, mode) - app.LimiterRedDb, MinDb, 0);
            if (double.IsNaN(app.AppliedDb) || app.Sessions.Count == 1) SetAppVolume(app, applied);
            else SetSessionVolume(se, (float)DbToScalar(app.AppliedDb));
        }

        void KeepOwnSessionAtFull(SessionEntry se)
        {
            Guid g = Ctx;
            if (se.CurVol < 0.999f) { se.Volume.SetMasterVolume(1f, ref g); se.CurVol = 1f; }
            if (se.Muted) { se.Volume.SetMute(false, ref g); se.Muted = false; }
        }

        AppState CreateApp(string key, string name, string path, bool isSystem, bool startup, float currentVol)
        {
            var s = SettingsStore.Current;
            var rule = SettingsStore.FindRule(key);
            var app = new AppState { Key = key, Name = name, ExePath = path, IsSystem = isSystem, Rule = rule };
            double curDb = ScalarToDb(currentVol);
            bool remember = s.RememberPerApp;
            if (rule != null && rule.HasVolume && (rule.Mode == (int)AppRuleMode.Fixed || remember)) app.FixedDb = rule.VolumeDb;
            else if (startup && (!s.ApplyToExistingOnStart || currentVol < 0.99f)) app.FixedDb = curDb;
            else { app.FixedDb = s.InitialVolumeDb; app.FixedFromDefault = true; }
            app.OffsetDb = rule != null && remember ? rule.TargetOffsetDb : 0;
            if (startup) app.AgcDb = Math.Min(curDb, 0);
            else app.AgcDb = Math.Min(s.InitialVolumeDb, rule != null && rule.HasAgc && remember ? rule.LastAgcDb : 0);
            if (rule != null && rule.Name != name) { rule.Name = name; rule.ExePath = path; SettingsStore.MarkDirty(); }
            return app;
        }

        void RemoveSession(string inst)
        {
            SessionEntry se;
            if (!sessions.TryGetValue(inst, out se)) return;
            sessions.Remove(inst);
            ReleaseSession(se);
            AppState app;
            if (se.AppKey != null && apps.TryGetValue(se.AppKey, out app))
            {
                app.Sessions.Remove(se);
                if (app.Sessions.Count == 0) { RememberAgc(app); apps.Remove(se.AppKey); }
            }
        }

        static void ReleaseSession(SessionEntry se)
        {
            try { if (se.Events != null) se.Control.UnregisterAudioSessionNotification(se.Events); } catch { }
            CoreAudio.SafeRelease(se.Control);
        }

        void RememberAgc(AppState app)
        {
            if (!app.Leveled || !SettingsStore.Current.RememberPerApp || EffectiveMode(app) != 1) return;
            var rule = app.Rule ?? SettingsStore.GetOrCreateRule(app.Key, app.Name, app.ExePath);
            app.Rule = rule;
            rule.HasAgc = true;
            rule.LastAgcDb = app.AgcDb;
            SettingsStore.MarkDirty();
        }

        void OnExternalVolume(string inst, float vol, bool mute)
        {
            SessionEntry se;
            if (!sessions.TryGetValue(inst, out se)) return;
            se.CurVol = vol; se.Muted = mute;
            if (se.IsOwn) { KeepOwnSessionAtFull(se); return; }
            AppState app;
            if (se.AppKey == null || !apps.TryGetValue(se.AppKey, out app)) return;
            if (EffectiveMode(app) == 2 || double.IsNaN(app.AppliedDb)) return;
            double db = ScalarToDb(vol);
            if (Math.Abs(db - app.AppliedDb) < 0.05) return;
            if (!se.FromStartup && Now - se.CreatedAt < NewSessionGraceSeconds)
            {
                // muitos apps (e o próprio Windows) jogam o volume da sessão para 100% logo ao abrir: desfaz
                SetSessionVolume(se, (float)DbToScalar(app.AppliedDb));
                return;
            }
            ApplyUserVolume(app, db);
        }

        /// <summary>O usuário mudou o volume de um app (no mixer do Windows ou aqui): vira a nova preferência dele.</summary>
        void ApplyUserVolume(AppState app, double db)
        {
            db = Clamp(db, MinDb, 0);
            var s = SettingsStore.Current;
            int mode = EffectiveMode(app);
            if (mode == 0)
            {
                app.FixedDb = db;
                app.FixedFromDefault = false;
                if (s.RememberPerApp || (app.Rule != null && app.Rule.Mode == (int)AppRuleMode.Fixed))
                {
                    var rule = app.Rule ?? SettingsStore.GetOrCreateRule(app.Key, app.Name, app.ExePath);
                    app.Rule = rule;
                    rule.HasVolume = true;
                    rule.VolumeDb = db;
                    SettingsStore.MarkDirty();
                }
            }
            else if (mode == 1)
            {
                app.OffsetDb = Clamp(app.OffsetDb + (db - app.AgcDb), -30, 30);
                app.AgcDb = db;
                if (s.RememberPerApp)
                {
                    var rule = app.Rule ?? SettingsStore.GetOrCreateRule(app.Key, app.Name, app.ExePath);
                    app.Rule = rule;
                    rule.TargetOffsetDb = app.OffsetDb;
                    SettingsStore.MarkDirty();
                }
            }
            app.LimiterRedDb = 0;
            app.LastCut = Now;
            SetAppVolume(app, db);
        }

        void ChangeRuleMode(string key, string name, string path, AppRuleMode mode)
        {
            var rule = SettingsStore.GetOrCreateRule(key, name, path);
            rule.Mode = (int)mode;
            SettingsStore.MarkDirty();
            AppState app;
            if (!apps.TryGetValue(key, out app)) return;
            app.Rule = rule;
            var s = SettingsStore.Current;
            double cur = double.IsNaN(app.AppliedDb) ? CurrentDb(app) : app.AppliedDb + app.LimiterRedDb;
            app.LimiterRedDb = 0;
            switch (mode)
            {
                case AppRuleMode.Fixed:
                    rule.HasVolume = true; rule.VolumeDb = Clamp(cur, MinDb, 0);
                    app.FixedDb = rule.VolumeDb; app.FixedFromDefault = false;
                    SetAppVolume(app, app.FixedDb);
                    break;
                case AppRuleMode.Ignore:
                    if (!double.IsNaN(app.AppliedDb)) SetAppVolume(app, Clamp(cur, MinDb, 0));
                    break;
                default:
                    if (s.Mode == (int)VolumeMode.Target) { app.AgcDb = Clamp(cur, MinDb, 0); app.Unlevel(); }
                    else
                    {
                        bool remembered = rule.HasVolume && s.RememberPerApp;
                        app.FixedDb = remembered ? rule.VolumeDb : s.InitialVolumeDb;
                        app.FixedFromDefault = !remembered;
                    }
                    app.AppliedDb = double.NaN;
                    break;
            }
        }

        void ResetAppToDefault(AppState app)
        {
            var s = SettingsStore.Current;
            app.FixedDb = s.InitialVolumeDb; app.FixedFromDefault = true;
            app.OffsetDb = 0; app.AgcDb = s.InitialVolumeDb; app.Unlevel(); app.LimiterRedDb = 0;
            app.AppliedDb = double.NaN;
        }

        int EffectiveMode(AppState app)
        {
            if (app.Rule != null)
            {
                if (app.Rule.Mode == (int)AppRuleMode.Ignore) return 2;
                if (app.Rule.Mode == (int)AppRuleMode.Fixed) return 0;
            }
            return SettingsStore.Current.Mode == (int)VolumeMode.Target ? 1 : 0;
        }

        static double DesiredDb(AppState app, int mode) { return mode == 1 ? app.AgcDb : app.FixedDb; }

        static double CurrentDb(AppState app)
        {
            return app.Sessions.Count > 0 ? ScalarToDb(app.Sessions[0].CurVol) : 0;
        }

        void SetAppVolume(AppState app, double db)
        {
            float scalar = (float)DbToScalar(db);
            foreach (var se in app.Sessions) SetSessionVolume(se, scalar);
            app.AppliedDb = db;
        }

        static void SetSessionVolume(SessionEntry se, float scalar)
        {
            Guid g = Ctx;
            if (se.Volume.SetMasterVolume(scalar, ref g) >= 0) se.CurVol = scalar;
        }

        // ---------------------------------------------------------------- medição + controle

        void Tick(double now, double dt, bool readVols)
        {
            double sumSqMax = 0, lbPeak = 0;
            long frames = 0;
            if (loopback != null)
            {
                loopback.Poll();
                if (loopback.Invalid) { deviceDirty = true; lastError = "Reconectando ao dispositivo de áudio…"; }
                loopback.Take(out sumSqMax, out frames, out lbPeak);
            }
            double tickPow = frames > 0 ? sumSqMax / frames : 0;
            if (tickPow > 1e-9) lastActivity = now;   // acima de −90 dBFS: algo audível tocando
            fastPow += (tickPow - fastPow) * (1 - Math.Exp(-dt / 0.125));
            medPow += (tickPow - medPow) * (1 - Math.Exp(-dt / 1.0));
            secSumSq += sumSqMax;
            secFrames += frames;

            foreach (var app in apps.Values) { app.Q = 0; app.MaxPeakPre = 0; app.MaxOutPeak = 0; app.AllMuted = true; }
            double qAlpha = 1 - Math.Exp(-dt / 0.15);
            foreach (var se in sessions.Values)
            {
                if (readVols)
                {
                    float v; bool m;
                    if (se.Volume.GetMasterVolume(out v) >= 0) se.CurVol = v;
                    if (se.Volume.GetMute(out m) >= 0) se.Muted = m;
                }
                if (se.IsOwn) { if (readVols) KeepOwnSessionAtFull(se); continue; }
                float pk;
                if (se.Meter.GetPeakValue(out pk) < 0) pk = 0;
                double outp = se.Muted ? 0 : pk * se.CurVol;
                se.QEma += (outp * outp - se.QEma) * qAlpha;
                AppState app;
                if (se.AppKey == null || !apps.TryGetValue(se.AppKey, out app)) continue;
                app.Q += se.QEma;
                if (pk > app.MaxPeakPre) app.MaxPeakPre = pk;
                if (outp > app.MaxOutPeak) app.MaxOutPeak = outp;
                if (!se.Muted) app.AllMuted = false;
            }
            double totalQ = 0;
            foreach (var app in apps.Values) totalQ += app.Q;
            foreach (var app in apps.Values)
            {
                app.Share = totalQ > 1e-14 ? app.Q / totalQ : 0;
                app.Sounding = app.MaxPeakPre > 0.001 && !app.AllMuted;
                if (app.Sounding) lastActivity = now;
                double g = app.Sessions.Count > 0 ? app.Sessions[0].CurVol : 0;
                if (app.Sounding && g > 1e-4 && tickPow > 0)
                {
                    double contentPow = tickPow * app.Share / (g * g);
                    app.SoundTime += dt;
                    if (!app.Leveled)
                    {
                        // primeira medida: média simples, ignorando o começo (buffer e filtro ainda enchendo)
                        if (app.SoundTime > 0.15) { app.InitSum += contentPow; app.InitCount++; }
                        if (app.InitCount > 0) { app.SlowPow = app.InitSum / app.InitCount; app.SlowInit = true; }
                    }
                    else
                    {
                        // sobe rápido (propaganda alta), desce devagar
                        double tau = contentPow > app.SlowPow ? 1.5 : 4.0;
                        app.SlowPow += (contentPow - app.SlowPow) * (1 - Math.Exp(-dt / tau));
                    }
                }
                if (app.Share > 0 && sumSqMax > 0)
                {
                    double cur;
                    secApps.TryGetValue(app.Name, out cur);
                    secApps[app.Name] = cur + app.Share * sumSqMax;
                }
            }

            Control(now, dt);
            DetectMaster(now, lbPeak);
        }

        void Control(double now, double dt)
        {
            var s = SettingsStore.Current;
            bool paused = now < pausedUntil;
            double limit = s.LimiterMaxSpl;
            bool limiterOn = s.LimiterEnabled && !paused;

            if (limiterOn && now >= limiterHoldUntil)
            {
                double lf = ToSpl(fastPow);
                if (lf > limit + 0.5)
                {
                    double excess = lf - limit + 1.0;
                    double maxShare = 0;
                    foreach (var app in apps.Values) maxShare = Math.Max(maxShare, app.Share);
                    bool any = false;
                    foreach (var app in apps.Values)
                    {
                        int mode = EffectiveMode(app);
                        if (mode == 2 || app.Share < 0.25 * maxShare || app.Share < 0.02) continue;
                        double room = DesiredDb(app, mode) - app.LimiterRedDb - MinDb;
                        double cut = Math.Min(excess, Math.Max(0, room));
                        if (cut > 0.05) { app.LimiterRedDb += cut; app.LastCut = now; any = true; }
                    }
                    if (any)
                    {
                        double f = Math.Pow(10, -excess / 10);
                        fastPow *= f; medPow *= f;
                        limiterHoldUntil = now + 0.1;
                    }
                }
            }

            foreach (var app in apps.Values)
            {
                int mode = EffectiveMode(app);
                if (mode == 2) { app.LimiterRedDb = 0; continue; }
                if (paused) continue;
                if (mode == 1) UpdateAgc(app, s, dt);
                if (app.LimiterRedDb > 0)
                {
                    if (!limiterOn) app.LimiterRedDb = Math.Max(0, app.LimiterRedDb - 3 * dt);
                    else if (app.Sounding && now - app.LastCut > 2.0 && ToSpl(medPow) < limit - 3)
                        app.LimiterRedDb = Math.Max(0, app.LimiterRedDb - 1.5 * dt);
                }
                double applied = Clamp(DesiredDb(app, mode) - app.LimiterRedDb, MinDb, 0);
                if (double.IsNaN(app.AppliedDb) || Math.Abs(applied - app.AppliedDb) >= 0.2)
                    SetAppVolume(app, applied);
            }
        }

        void UpdateAgc(AppState app, AppSettings s, double dt)
        {
            if (!app.Sounding || !app.SlowInit || app.SoundTime < 0.8) return;
            double lc = ToSpl(app.SlowPow);
            if (double.IsInfinity(lc) || double.IsNaN(lc)) return;
            double raw = s.TargetSpl + app.OffsetDb - lc;
            double want = Clamp(raw, MinDb, 0);
            app.AtMaximum = raw > 1;
            if (!app.Leveled)
            {
                // primeira medida: pula direto para o ganho certo (para cima com no máximo +12 dB)
                app.AgcDb = Math.Min(want, app.AgcDb + 12);
                app.Leveled = true;
                return;
            }
            double diff = want - app.AgcDb;
            if (Math.Abs(diff) < 0.5) return;
            double step = diff > 0 ? Math.Min(diff, 1.5 * dt) : Math.Max(diff, -8 * dt);
            app.AgcDb += step;
            // quando o nível alvo desce, ele "assume" o corte que o limitador estava fazendo (sem somar os dois)
            if (step < 0 && app.LimiterRedDb > 0) app.LimiterRedDb = Math.Max(0, app.LimiterRedDb + step);
        }

        void DetectMaster(double now, double lbPeak)
        {
            if (profile == null || profile.MasterMode != (int)MasterMode.Unknown) return;
            detLbPeak = Math.Max(detLbPeak, lbPeak);
            double top = 0, second = 0;
            foreach (var app in apps.Values)
            {
                if (app.MaxOutPeak > top) { second = top; top = app.MaxOutPeak; }
                else if (app.MaxOutPeak > second) second = app.MaxOutPeak;
            }
            detTop = Math.Max(detTop, top);
            detSecond = Math.Max(detSecond, second);
            if (now - detWinStart < 0.25) return;
            double m = masterDb - masterMax;
            if (!masterMuted && m <= -6 && detTop > 0.003 && detSecond < detTop * 0.1 && detLbPeak > 0)
            {
                double r = 20 * Math.Log10(detLbPeak / detTop);
                if (Math.Abs(r) < 2.5) votesPre++;
                else if (Math.Abs(r - m) < 2.5) votesPost++;
                if (votesPre >= 20 && votesPre > 4 * votesPost) { profile.MasterMode = (int)MasterMode.PreMaster; SettingsStore.MarkDirty(); }
                else if (votesPost >= 20 && votesPost > 4 * votesPre) { profile.MasterMode = (int)MasterMode.PostMaster; SettingsStore.MarkDirty(); }
            }
            detWinStart = now; detLbPeak = 0; detTop = 0; detSecond = 0;
        }

        /// <summary>Potência média ponderada A (escala cheia = 1) → dB SPL estimado no ouvido.</summary>
        double ToSpl(double pow)
        {
            if (pow <= 1e-13 || masterMuted || profile == null || device == null) return double.NegativeInfinity;
            double dbfs = 10 * Math.Log10(pow) + 3.0103; // seno em escala cheia = 0 dBFS
            double spl = dbfs + profile.CalSpl;
            if (profile.MasterMode != (int)MasterMode.PostMaster) spl += masterDb - masterMax;
            return spl;
        }

        void SecondBlock(double now)
        {
            double dur = now - secStart;
            if (dur < 1.0) return;
            secStart = now;
            double expected = (loopback != null ? loopback.SampleRate : 48000) * Math.Min(dur, 2.0);
            double pow = secSumSq / Math.Max(secFrames, expected);
            double spl = ToSpl(pow);
            bool counts = profile != null && profile.CountExposure && device != null;
            bool sound = !double.IsInfinity(spl) && spl > 20;
            Dictionary<string, double> shares = null;
            lastDominantApp = null;
            if (sound && secApps.Count > 0)
            {
                double total = secApps.Values.Sum(), best = 0;
                shares = new Dictionary<string, double>();
                foreach (var kv in secApps)
                {
                    shares[kv.Key] = kv.Value / total;
                    if (kv.Value > best) { best = kv.Value; lastDominantApp = kv.Key; }
                }
            }
            exposure.AddSecond(DateTime.Now, sound ? spl : double.NegativeInfinity, counts, shares);
            secSumSq = 0; secFrames = 0; secApps.Clear();
            lastSecondSpl = sound ? spl : double.NegativeInfinity;
            CheckAlerts(now, counts);
            if (Debug) LogDebug(spl);
        }

        /// <summary>Com VOLUMEGUARD_DEBUG=1, grava no log o estado do motor a cada segundo.</summary>
        static readonly bool Debug = Environment.GetEnvironmentVariable("VOLUMEGUARD_DEBUG") == "1";

        void LogDebug(double spl)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("dbg nivel=").Append(double.IsInfinity(spl) ? "-" : spl.ToString("F1")).Append(Now - lastActivity > IdleAfterSeconds ? " ocioso" : " ativo").Append(" master=").Append(masterDb.ToString("F1"));
            foreach (var app in apps.Values)
                sb.Append(" | ").Append(app.Key).Append(" vol=").Append(app.AppliedDb.ToString("F1")).Append(" lim=").Append(app.LimiterRedDb.ToString("F1"))
                  .Append(" agc=").Append(app.AgcDb.ToString("F1")).Append(" share=").Append(app.Share.ToString("F2")).Append(app.Sounding ? " som" : "");
            Log.Write(sb.ToString());
        }

        void CheckAlerts(double now, bool counts)
        {
            var s = SettingsStore.Current;
            bool paused = now < pausedUntil;
            if (!s.AlertEnabled || paused || !counts)
            {
                if (alertActive) EndAlert();
            }
            else
            {
                int window = Math.Max(1, s.AlertSeconds);
                double leq = exposure.LeqLast(window);
                // "sustentado": a média passou do limite e a maior parte dos segundos também
                // (um estouro curto que o limitador já cortou não conta)
                bool sustained = exposure.SecondsAtOrAbove(window, s.AlertSpl - 1) >= Math.Ceiling(window * 0.6);
                if (!alertActive)
                {
                    if (now >= alertSnoozeUntil && leq >= s.AlertSpl && sustained)
                    {
                        alertActive = true;
                        Raise(new AlertInfo { Kind = AlertKind.Loud, Level = leq, AppName = lastDominantApp, Dose = exposure.DailyDose });
                    }
                }
                else if (leq < s.AlertSpl - 2 && exposure.LeqLast(2) < s.AlertSpl - 2) EndAlert();
                else Raise(new AlertInfo { Kind = AlertKind.LoudUpdate, Level = Math.Max(exposure.LeqLast(2), leq), AppName = lastDominantApp, Dose = exposure.DailyDose });
            }
            if (s.DoseAlerts && !paused && counts)
            {
                int milestone = exposure.CheckDoseMilestone();
                if (milestone > 0) Raise(new AlertInfo { Kind = AlertKind.Dose, Milestone = milestone, Dose = exposure.DailyDose, Level = lastSecondSpl, AppName = lastDominantApp });
            }
        }

        void EndAlert()
        {
            alertActive = false;
            Raise(new AlertInfo { Kind = AlertKind.LoudEnded });
        }

        void Raise(AlertInfo info)
        {
            var h = AlertRaised;
            if (h != null) try { h(info); } catch (Exception ex) { Log.Write("Alerta: " + ex); }
        }

        void Publish()
        {
            var h = SnapshotReady;
            if (h == null) return;
            var snap = new EngineSnapshot
            {
                HasDevice = device != null,
                Error = lastError,
                DeviceId = deviceId,
                DeviceName = deviceName,
                MasterDb = masterDb, MasterMinDb = masterMin, MasterMaxDb = masterMax, MasterStepDb = masterInc,
                MasterMuted = masterMuted,
                LevelFast = ToSpl(fastPow),
                Level1s = lastSecondSpl,
                DailyDose = exposure.DailyDose,
                WeeklyDose = exposure.WeeklyDose,
                DayLeq = exposure.DayLeq,
                DayMax = exposure.DayMax,
                DayActive = exposure.DayActiveSeconds,
                Paused = Now < pausedUntil,
                PausedUntil = pausedUntilWall,
                AlertActive = alertActive,
            };
            if (profile != null)
            {
                snap.CalSpl = profile.CalSpl;
                snap.CountExposure = profile.CountExposure;
                snap.DeviceConfigured = profile.Configured;
                snap.MasterMode = profile.MasterMode;
            }
            if (UiActive)
            {
                var lv = new double[live.Length];
                for (int i = 0; i < live.Length; i++) lv[i] = live[(livePos + i) % live.Length];
                snap.Live = lv;
            }
            foreach (var app in apps.Values)
            {
                int mode = EffectiveMode(app);
                snap.Apps.Add(new AppSnapshot
                {
                    Key = app.Key, Name = app.Name, ExePath = app.ExePath, IsSystem = app.IsSystem,
                    SessionCount = app.Sessions.Count,
                    VolumeDb = double.IsNaN(app.AppliedDb) ? CurrentDb(app) : app.AppliedDb,
                    LimiterDb = app.LimiterRedDb,
                    OutSpl = ToSpl(fastPow * app.Share),
                    Sounding = app.Sounding,
                    Muted = app.AllMuted && app.Sessions.Count > 0,
                    RuleMode = app.Rule != null ? app.Rule.Mode : 0,
                    EffectiveMode = mode,
                    OffsetDb = app.OffsetDb,
                    AtMaximum = mode == 1 && app.AtMaximum && app.Leveled,
                });
            }
            try { h(snap); } catch (Exception ex) { Log.Write("Snapshot: " + ex); }
        }

        // ---------------------------------------------------------------- util

        public static double DbToScalar(double db) { return db <= MinDb - 0.5 ? 0 : Math.Pow(10, db / 20); }
        public static double ScalarToDb(double v) { return v <= 0.001 ? MinDb : Math.Max(MinDb, 20 * Math.Log10(v)); }
        static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }
    }

    public sealed class SessionEvents : IAudioSessionEvents
    {
        readonly AudioEngine engine;
        readonly string inst;
        internal SessionEvents(AudioEngine e, string instanceId) { engine = e; inst = instanceId; }

        public int OnDisplayNameChanged(string newName, ref Guid eventContext) { return 0; }
        public int OnIconPathChanged(string newPath, ref Guid eventContext) { return 0; }
        public int OnSimpleVolumeChanged(float newVolume, bool newMute, ref Guid eventContext)
        {
            if (eventContext != AudioEngine.Ctx) engine.OnSessionVolume(inst, newVolume, newMute);
            return 0;
        }
        public int OnChannelVolumeChanged(int channelCount, IntPtr newChannelVolumes, int changedChannel, ref Guid eventContext) { return 0; }
        public int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext) { return 0; }
        public int OnStateChanged(AudioSessionState newState)
        {
            if (newState == AudioSessionState.Expired) engine.OnSessionGone(inst);
            return 0;
        }
        public int OnSessionDisconnected(AudioSessionDisconnectReason reason) { engine.OnSessionGone(inst); return 0; }
    }

    public sealed class SessionNotifier : IAudioSessionNotification
    {
        readonly AudioEngine engine;
        internal SessionNotifier(AudioEngine e) { engine = e; }
        public int OnSessionCreated(object newSession) { engine.OnNewSession(newSession); return 0; }
    }

    public sealed class DeviceNotifier : IMMNotificationClient
    {
        readonly AudioEngine engine;
        internal DeviceNotifier(AudioEngine e) { engine = e; }
        public int OnDeviceStateChanged(string deviceId, int newState) { engine.OnDeviceEvent(deviceId, false); return 0; }
        public int OnDeviceAdded(string deviceId) { return 0; }
        public int OnDeviceRemoved(string deviceId) { engine.OnDeviceEvent(deviceId, false); return 0; }
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string defaultDeviceId)
        {
            if (flow == EDataFlow.Render && role == ERole.Multimedia) engine.OnDeviceEvent(defaultDeviceId, true);
            return 0;
        }
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) { return 0; }
    }
}
