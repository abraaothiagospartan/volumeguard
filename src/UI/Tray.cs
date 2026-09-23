using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VolumeGuard.UI
{
    /// <summary>Ícone na bandeja: as barras acendem conforme o nível; menu com modo, pausa e sair.</summary>
    public sealed class Tray : IDisposable
    {
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        readonly NotifyIcon icon;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem status, modeFixed, modeTarget, pause15, pause60, resume;
        string lastKey;
        IntPtr lastHandle = IntPtr.Zero;

        public event Action OpenRequested, ExitRequested, ResumeRequested;
        public event Action<int> ModeRequested;
        public event Action<double> PauseRequested;

        public Tray()
        {
            menu = new ContextMenuStrip
            {
                Renderer = new ToolStripProfessionalRenderer(new DarkColors()),
                ShowImageMargin = false,
                ShowCheckMargin = true,
                BackColor = Color.FromArgb(0x1A, 0x1A, 0x19),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f),
            };
            status = Item("VolumeGuard", null);
            status.Enabled = false;
            var open = Item("Abrir VolumeGuard", () => Fire(OpenRequested));
            open.Font = new Font(menu.Font, FontStyle.Bold);
            modeFixed = Item("Modo: volume fixo", () => { if (ModeRequested != null) ModeRequested(0); });
            modeTarget = Item("Modo: nível alvo (auto)", () => { if (ModeRequested != null) ModeRequested(1); });
            pause15 = Item("Pausar proteção por 15 min", () => { if (PauseRequested != null) PauseRequested(15); });
            pause60 = Item("Pausar proteção por 1 hora", () => { if (PauseRequested != null) PauseRequested(60); });
            resume = Item("Retomar proteção", () => Fire(ResumeRequested));
            var exit = Item("Sair", () => Fire(ExitRequested));
            menu.Items.AddRange(new ToolStripItem[]
            {
                status, new ToolStripSeparator(), open, new ToolStripSeparator(),
                modeFixed, modeTarget, new ToolStripSeparator(), pause15, pause60, resume, new ToolStripSeparator(), exit
            });

            icon = new NotifyIcon { ContextMenuStrip = menu, Text = "VolumeGuard", Visible = true };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Fire(OpenRequested); };
            SetIcon(0, false, false);
        }

        ToolStripMenuItem Item(string text, Action onClick)
        {
            var it = new ToolStripMenuItem(text) { ForeColor = Color.White };
            if (onClick != null) it.Click += (s, e) => onClick();
            return it;
        }

        static void Fire(Action a) { if (a != null) a(); }

        public void Update(int zone, bool sounding, bool paused, int mode, string statusText, string tooltip)
        {
            SetIcon(zone, sounding, paused);
            status.Text = statusText;
            modeFixed.Checked = mode == 0;
            modeTarget.Checked = mode == 1;
            pause15.Visible = pause60.Visible = !paused;
            resume.Visible = paused;
            string t = tooltip ?? "VolumeGuard";
            icon.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void SetIcon(int zone, bool sounding, bool paused)
        {
            string key = zone + "|" + sounding + "|" + paused;
            if (key == lastKey) return;
            lastKey = key;
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = IconArt.Render(size, IconArt.TrayColors(zone, sounding, paused)))
            {
                IntPtr h = bmp.GetHicon();
                icon.Icon = Icon.FromHandle(h);
                if (lastHandle != IntPtr.Zero) DestroyIcon(lastHandle);
                lastHandle = h;
            }
        }

        public void ShowBalloon(string title, string text)
        {
            try { icon.ShowBalloonTip(5000, title, text, ToolTipIcon.None); } catch { }
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
            if (lastHandle != IntPtr.Zero) DestroyIcon(lastHandle);
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            static readonly Color Bg = Color.FromArgb(0x1A, 0x1A, 0x19);
            static readonly Color Sel = Color.FromArgb(0x33, 0x33, 0x31);
            static readonly Color Line = Color.FromArgb(0x3A, 0x3A, 0x37);
            public override Color ToolStripDropDownBackground { get { return Bg; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemBorder { get { return Sel; } }
            public override Color MenuItemSelected { get { return Sel; } }
            public override Color MenuItemSelectedGradientBegin { get { return Sel; } }
            public override Color MenuItemSelectedGradientEnd { get { return Sel; } }
            public override Color ImageMarginGradientBegin { get { return Bg; } }
            public override Color ImageMarginGradientMiddle { get { return Bg; } }
            public override Color ImageMarginGradientEnd { get { return Bg; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Line; } }
            public override Color CheckBackground { get { return Color.FromArgb(0x1E, 0x3A, 0x5F); } }
            public override Color CheckSelectedBackground { get { return Color.FromArgb(0x25, 0x4A, 0x78); } }
            public override Color CheckPressedBackground { get { return Color.FromArgb(0x25, 0x4A, 0x78); } }
        }
    }
}
