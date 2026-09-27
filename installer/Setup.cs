// Instalador/desinstalador do VolumeGuard (por usuário, sem administrador).
//   VolumeGuard-Setup.exe                 instala (interface)
//   VolumeGuard-Setup.exe /silent         instala sem perguntar  [/desktop] [/noautostart] [/nolaunch]
//   uninstall.exe  (ou /uninstall)        desinstala             [/silent] [/removedata]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

[assembly: AssemblyTitle("Instalador do VolumeGuard")]
[assembly: AssemblyProduct("VolumeGuard")]
[assembly: AssemblyCompany("VolumeGuard")]
[assembly: AssemblyCopyright("Copyright © 2026 VolumeGuard · Licença MIT")]
[assembly: AssemblyVersion(VolumeGuard.BuildInfo.Version + ".0")]
[assembly: AssemblyFileVersion(VolumeGuard.BuildInfo.Version + ".0")]
[assembly: AssemblyInformationalVersion(VolumeGuard.BuildInfo.Version)]
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace VolumeGuardSetup
{
    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool SetDefaultDllDirectories(int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr AddDllDirectory(string dir);

        [STAThread]
        static int Main(string[] args)
        {
            // Instaladores costumam rodar da pasta Downloads: não carregar DLLs de lá
            try
            {
                string rt = RuntimeEnvironment.GetRuntimeDirectory();
                AddDllDirectory(rt);
                AddDllDirectory(Path.Combine(rt, "WPF"));
                SetDefaultDllDirectories(0x800 | 0x400);
            }
            catch { }

            Installer.RemoveOldCleanupHelpers();
            var a = new HashSet<string>(args.Select(x => x.ToLowerInvariant().TrimStart('-', '/')));
            if (a.Contains("cleanup") && args.Length >= 3)
            {
                int pid;
                int.TryParse(args[args.Length - 2], out pid);
                return Installer.Cleanup(pid, args[args.Length - 1]);
            }
            bool uninstall = a.Contains("uninstall") ||
                string.Equals(Path.GetFileName(Installer.SelfPath), Installer.UninstallerName, StringComparison.OrdinalIgnoreCase);

            if (a.Contains("silent") || a.Contains("s"))
            {
                try
                {
                    if (uninstall) Installer.Uninstall(a.Contains("removedata"), null);
                    else
                    {
                        Installer.Install(a.Contains("desktop"), !a.Contains("noautostart"), null);
                        if (!a.Contains("nolaunch")) Installer.Launch();
                    }
                    return 0;
                }
                catch (Exception ex) { Installer.LogError(ex); return 1; }
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Setup.Theme.xaml"))
                app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(s));
            var w = new SetupWindow(uninstall);
            app.Run(w);
            return w.ExitCode;
        }
    }

    static class Installer
    {
        public const string ExeName = "VolumeGuard.exe";
        public const string UninstallerName = "uninstall.exe";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VolumeGuard";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ExitEventName = "VolumeGuard.Exit.5b0d3c6e";

        public static string SelfPath { get { return Assembly.GetExecutingAssembly().Location; } }
        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VolumeGuard"); }
        }
        static string ExePath { get { return Path.Combine(InstallDir, ExeName); } }
        static string DataDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VolumeGuard"); } }
        static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "VolumeGuard.lnk"); }
        }
        static string DesktopLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "VolumeGuard.lnk"); }
        }

        public static bool IsInstalled { get { return File.Exists(ExePath); } }
        public static bool HasDesktopLink { get { return File.Exists(DesktopLink); } }
        public static bool AutostartEnabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("VolumeGuard") != null;
            }
        }

        public static void Install(bool desktop, bool autostart, Action<string> progress)
        {
            Report(progress, "Fechando o VolumeGuard, se estiver aberto…");
            CloseRunningApp();

            Report(progress, "Copiando arquivos…");
            Directory.CreateDirectory(InstallDir);
            string tmp = ExePath + ".novo";
            using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("Setup.payload.exe"))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                src.CopyTo(dst);
            ReplaceFile(tmp, ExePath);
            string uninstaller = Path.Combine(InstallDir, UninstallerName);
            if (!string.Equals(SelfPath, uninstaller, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(SelfPath, uninstaller + ".novo", true);
                ReplaceFile(uninstaller + ".novo", uninstaller);
            }

            // registra logo depois dos arquivos: se algo interromper a instalação daqui pra frente,
            // o app já aparece em "Aplicativos instalados" e dá para desinstalar normalmente
            Report(progress, "Registrando no Windows…");
            long sizeKb = (new FileInfo(ExePath).Length + new FileInfo(uninstaller).Length) / 1024;
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "VolumeGuard");
                k.SetValue("DisplayVersion", VolumeGuard.BuildInfo.Version);
                k.SetValue("Publisher", "VolumeGuard");
                k.SetValue("DisplayIcon", "\"" + ExePath + "\",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /silent");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (autostart) run.SetValue("VolumeGuard", "\"" + ExePath + "\" --minimized");
                else if (run.GetValue("VolumeGuard") != null) run.DeleteValue("VolumeGuard");
            }

            // atalho que falha não derruba a instalação (o app continua no Menu Iniciar ou na pasta)
            Report(progress, "Criando atalhos…");
            TryStep(() => CreateShortcut(StartMenuLink, ExePath, "Volume de cada app, limitador e histórico de dB"));
            TryStep(() =>
            {
                if (desktop) CreateShortcut(DesktopLink, ExePath, "VolumeGuard");
                else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);
            });
            Report(progress, "Pronto!");
        }

        static void TryStep(Action step)
        {
            try { step(); } catch (Exception ex) { LogError(ex); }
        }

        public static void Launch()
        {
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = InstallDir });
        }

        public static void Uninstall(bool removeData, Action<string> progress)
        {
            Report(progress, "Fechando o VolumeGuard…");
            CloseRunningApp();

            Report(progress, "Removendo atalhos e registro…");
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (run != null && run.GetValue("VolumeGuard") != null) run.DeleteValue("VolumeGuard");
            foreach (var link in new[] { StartMenuLink, DesktopLink })
                if (File.Exists(link)) File.Delete(link);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

            Report(progress, "Removendo arquivos…");
            TryDelete(ExePath);
            if (removeData) RemoveUserData();

            // o desinstalador não consegue apagar a si mesmo enquanto roda: uma cópia temporária termina o serviço
            string self = SelfPath;
            string uninstaller = Path.Combine(InstallDir, UninstallerName);
            if (string.Equals(self, uninstaller, StringComparison.OrdinalIgnoreCase))
            {
                string helper = Path.Combine(Path.GetTempPath(), "VolumeGuard-cleanup-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(self, helper, true);
                Process.Start(new ProcessStartInfo(helper, "/cleanup " + Process.GetCurrentProcess().Id + " \"" + InstallDir + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath(),
                });
            }
            else
            {
                TryDelete(uninstaller);
                TryDeleteDir(InstallDir);
            }
            Report(progress, "VolumeGuard removido.");
        }

        /// <summary>Roda da pasta temporária: espera o desinstalador fechar e apaga o que sobrou.</summary>
        public static int Cleanup(int pid, string dir)
        {
            // só aceita a pasta de instalação conhecida (nunca apaga outra pasta passada por argumento)
            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\'); } catch { return 2; }
            if (!string.Equals(full, InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return 2;
            try { if (pid > 0) Process.GetProcessById(pid).WaitForExit(15000); } catch { }
            for (int i = 0; i < 20; i++)
            {
                TryDelete(Path.Combine(dir, UninstallerName));
                TryDelete(Path.Combine(dir, ExeName));
                if (!File.Exists(Path.Combine(dir, UninstallerName))) break;
                Thread.Sleep(250);
            }
            TryDeleteDir(dir);
            return 0;
        }

        /// <summary>Apaga cópias temporárias deixadas por desinstalações anteriores.</summary>
        public static void RemoveOldCleanupHelpers()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), "VolumeGuard-cleanup-*.exe"))
                    if (!string.Equals(f, SelfPath, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
            }
            catch { }
        }

        static void RemoveUserData()
        {
            string d = DataDir;
            if (!Directory.Exists(d)) return;
            foreach (var name in new[] { "settings.json", "settings.json.tmp", "settings.json.invalido", "log.txt" })
                TryDelete(Path.Combine(d, name));
            string hist = Path.Combine(d, "historico");
            if (Directory.Exists(hist))
            {
                foreach (var f in Directory.GetFiles(hist, "????-??-??.csv")) TryDelete(f);
                TryDeleteDir(hist);
            }
            TryDeleteDir(d);
        }

        /// <summary>Pede para o app fechar sozinho (salva o histórico); se não fechar, encerra o da pasta de instalação.</summary>
        static void CloseRunningApp()
        {
            if (Process.GetProcessesByName("VolumeGuard").Length == 0) return;
            try { using (var ev = EventWaitHandle.OpenExisting(ExitEventName)) ev.Set(); } catch { }
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000 && Process.GetProcessesByName("VolumeGuard").Length > 0) Thread.Sleep(200);
            foreach (var p in Process.GetProcessesByName("VolumeGuard"))
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (string.Equals(path, ExePath, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
            }
        }

        static void ReplaceFile(string tmp, string target)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);
                    return;
                }
                catch (IOException) { if (i >= 20) throw; Thread.Sleep(250); }
                catch (UnauthorizedAccessException) { if (i >= 20) throw; Thread.Sleep(250); }
            }
        }

        static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }

        static void Report(Action<string> progress, string text) { if (progress != null) progress(text); }

        public static void LogError(Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "VolumeGuard-setup.log"), DateTime.Now + "  " + ex + Environment.NewLine); } catch { }
        }

        // ---- atalhos (.lnk) via IShellLink

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLinkObject { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, int flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int cmd);
            void SetShowCmd(int cmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
            void Resolve(IntPtr hwnd, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        static void CreateShortcut(string linkPath, string target, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath));
            var link = (IShellLinkW)new ShellLinkObject();
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(Path.GetDirectoryName(target));
                link.SetDescription(description);
                link.SetIconLocation(target, 0);
                ((IPersistFile)link).Save(linkPath, true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }
    }

    sealed class SetupWindow : Window
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        readonly bool uninstallMode;
        readonly StackPanel installPanel, uninstallPanel, progressPanel;
        readonly TextBlock titleText, progressText, detailText;
        readonly CheckBox desktopCheck, autostartCheck, launchCheck, removeDataCheck;
        readonly Button primary, cancel;
        bool working, done;
        public int ExitCode = 1;

        public SetupWindow(bool uninstall)
        {
            uninstallMode = uninstall;
            Title = uninstall ? "Desinstalar o VolumeGuard" : "Instalar o VolumeGuard";
            Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            Foreground = Brushes.White;
            Background = (Brush)FindResource("PageBrush");
            UseLayoutRounding = true;

            var root = (FrameworkElement)Load("Setup.Setup.xaml");
            Content = root;
            installPanel = (StackPanel)root.FindName("InstallPanel");
            uninstallPanel = (StackPanel)root.FindName("UninstallPanel");
            progressPanel = (StackPanel)root.FindName("ProgressPanel");
            titleText = (TextBlock)root.FindName("TitleText");
            progressText = (TextBlock)root.FindName("ProgressText");
            detailText = (TextBlock)root.FindName("DetailText");
            desktopCheck = (CheckBox)root.FindName("DesktopCheck");
            autostartCheck = (CheckBox)root.FindName("AutostartCheck");
            launchCheck = (CheckBox)root.FindName("LaunchCheck");
            removeDataCheck = (CheckBox)root.FindName("RemoveDataCheck");
            primary = (Button)root.FindName("PrimaryButton");
            cancel = (Button)root.FindName("CancelButton");
            ((TextBlock)root.FindName("VersionText")).Text = "Versão " + VolumeGuard.BuildInfo.Version;
            ((TextBlock)root.FindName("PathText")).Text = "Pasta: " + Installer.InstallDir;
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Setup.app.ico"))
                {
                    var dec = BitmapDecoder.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var frame = dec.Frames.OrderByDescending(f => f.PixelWidth).First();
                    ((Image)root.FindName("Logo")).Source = frame;
                    Icon = frame;
                }
            }
            catch { }

            if (uninstall)
            {
                titleText.Text = "Desinstalar o VolumeGuard";
                installPanel.Visibility = Visibility.Collapsed;
                uninstallPanel.Visibility = Visibility.Visible;
                primary.Content = "Desinstalar";
                primary.Style = (Style)FindResource("DangerButton");
            }
            else if (Installer.IsInstalled)
            {
                titleText.Text = "Atualizar o VolumeGuard";
                Title = "Atualizar o VolumeGuard";
                primary.Content = "Atualizar";
                autostartCheck.IsChecked = Installer.AutostartEnabled;
                desktopCheck.IsChecked = Installer.HasDesktopLink;
            }

            primary.Click += (s, a) => { if (done) Close(); else Run(); };
            cancel.Click += (s, a) => Close();
            Closing += (s, a) => { if (working) a.Cancel = true; };
            SourceInitialized += (s, a) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int on = 1, caption = 0x000E0F0F;
                DwmSetWindowAttribute(hwnd, 20, ref on, 4);
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
            };
        }

        static object Load(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) return XamlReader.Load(s);
        }

        void Run()
        {
            working = true;
            bool desktop = desktopCheck.IsChecked == true, autostart = autostartCheck.IsChecked == true;
            bool launch = launchCheck.IsChecked == true, removeData = removeDataCheck.IsChecked == true;
            installPanel.Visibility = uninstallPanel.Visibility = Visibility.Collapsed;
            progressPanel.Visibility = Visibility.Visible;
            primary.IsEnabled = cancel.IsEnabled = false;
            Action<string> progress = t => Dispatcher.BeginInvoke(new Action(() => progressText.Text = t));

            // thread STA própria: a criação de atalhos usa COM e a interface continua respondendo
            var th = new Thread(() =>
            {
                Exception error = null;
                try
                {
                    if (uninstallMode) Installer.Uninstall(removeData, progress);
                    else
                    {
                        Installer.Install(desktop, autostart, progress);
                        if (launch) Installer.Launch();
                    }
                }
                catch (Exception ex) { error = ex; Installer.LogError(ex); }
                Dispatcher.BeginInvoke(new Action(() => Finish(error)));
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
        }

        void Finish(Exception error)
        {
            working = false;
            done = true;
            cancel.Visibility = Visibility.Collapsed;
            primary.IsEnabled = true;
            primary.Style = (Style)FindResource("AccentButton");
            primary.Content = "Concluir";
            if (error != null)
            {
                progressText.Text = "Não deu certo";
                detailText.Text = error.Message + "\nFeche o VolumeGuard (botão direito no ícone da bandeja → Sair) e tente de novo.";
                ExitCode = 1;
                return;
            }
            ExitCode = 0;
            if (uninstallMode)
            {
                progressText.Text = "VolumeGuard removido";
                detailText.Text = removeDataCheck.IsChecked == true ? "Histórico e configurações também foram apagados." : "Seu histórico e configurações ficaram guardados, caso instale de novo.";
            }
            else
            {
                progressText.Text = "Tudo pronto!";
                detailText.Text = "O VolumeGuard fica na bandeja, perto do relógio. Ele também aparece no Menu Iniciar.";
            }
        }
    }
}
