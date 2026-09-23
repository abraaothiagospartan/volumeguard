using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VolumeGuard.Audio
{
    public static class ProcessInfo
    {
        const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const int STILL_ACTIVE = 259;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        public const string SystemKey = "#sistema";

        public static string GetPath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        public static bool IsAlive(int pid)
        {
            if (pid <= 0) return true;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5; // acesso negado = existe
            try
            {
                int code;
                return !GetExitCodeProcess(h, out code) || code == STILL_ACTIVE;
            }
            finally { CloseHandle(h); }
        }

        public static void Describe(int pid, bool isSystem, out string key, out string name, out string path)
        {
            if (isSystem || pid == 0)
            {
                key = SystemKey;
                name = "Sons do sistema";
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "SndVol.exe");
                return;
            }
            path = GetPath(pid);
            if (path != null)
            {
                key = Path.GetFileName(path).ToLowerInvariant();
                name = null;
                try
                {
                    var fvi = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrWhiteSpace(fvi.FileDescription)) name = fvi.FileDescription.Trim();
                    else if (!string.IsNullOrWhiteSpace(fvi.ProductName)) name = fvi.ProductName.Trim();
                }
                catch { }
                if (string.IsNullOrEmpty(name) || name.Length > 40) name = Path.GetFileNameWithoutExtension(path);
                return;
            }
            try
            {
                string pn = Process.GetProcessById(pid).ProcessName;
                key = pn.ToLowerInvariant() + ".exe";
                name = pn;
            }
            catch
            {
                key = "pid:" + pid;
                name = "Processo " + pid;
            }
        }
    }
}
