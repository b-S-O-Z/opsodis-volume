// Tray application: hosts VolumeSync and offers enable / autostart / exit.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OpsodisVolume
{
    internal static class Program
    {
        internal static EventWaitHandle ExitRequest;

        [STAThread]
        static void Main(string[] args)
        {
            ExitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\OpsodisVolumeSync.Exit");
            // "--exit" asks a running instance to quit gracefully (restoring app volumes).
            if (args.Length > 0 && args[0] == "--exit")
            {
                ExitRequest.Set();
                return;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\OpsodisVolumeSync", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }
    }

    internal static class Settings
    {
        const string KeyPath = @"Software\OpsodisVolume";
        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "OpsodisVolume";

        public static string DeviceKeyword
        {
            get { return Read("DeviceKeyword", "OPSODIS"); }
        }

        public static bool Enabled
        {
            get { return Read("Enabled", "1") != "0"; }
            set { Write("Enabled", value ? "1" : "0"); }
        }

        public static bool RunAtStartup
        {
            get
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunPath))
                    return k != null && k.GetValue(RunName) != null;
            }
            set
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunPath))
                {
                    if (value) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
        }

        static string Read(string name, string fallback)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                object v = k == null ? null : k.GetValue(name);
                return v == null ? fallback : v.ToString();
            }
        }

        static void Write(string name, string value)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
                k.SetValue(name, value);
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        readonly VolumeSync engine;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem statusItem;
        readonly ToolStripMenuItem enabledItem;
        readonly ToolStripMenuItem startupItem;
        readonly System.Windows.Forms.Timer refresh;
        readonly Icon icon;

        public TrayContext()
        {
            engine = new VolumeSync(Settings.DeviceKeyword, Settings.Enabled);

            statusItem = new ToolStripMenuItem("起動中...");
            statusItem.Enabled = false;
            enabledItem = new ToolStripMenuItem("Windowsの音量を反映する", null, OnToggleEnabled);
            enabledItem.Checked = engine.Enabled;
            startupItem = new ToolStripMenuItem("Windows起動時に実行", null, OnToggleStartup);
            startupItem.Checked = Settings.RunAtStartup;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(enabledItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripMenuItem("音量ミキサーを開く", null, delegate { OpenMixer(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("終了", null, delegate { ExitThread(); }));

            icon = CreateIcon();
            tray = new NotifyIcon();
            tray.Icon = icon;
            tray.Text = "OPSODIS Volume";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { OpenMixer(); };
            tray.Visible = true;

            refresh = new System.Windows.Forms.Timer();
            refresh.Interval = 500;
            refresh.Tick += delegate { UpdateStatus(); };
            refresh.Start();

            SystemEvents.SessionEnding += OnSessionEnding;
        }

        void UpdateStatus()
        {
            if (Program.ExitRequest.WaitOne(0))
            {
                ExitThread();
                return;
            }
            string s = engine.Status;
            if (statusItem.Text != s) statusItem.Text = s;
            // NotifyIcon.Text is limited to 63 characters.
            string tip = s.Length > 63 ? s.Substring(0, 63) : s;
            if (tray.Text != tip) tray.Text = tip;
        }

        void OnToggleEnabled(object sender, EventArgs e)
        {
            engine.Enabled = !engine.Enabled;
            enabledItem.Checked = engine.Enabled;
            Settings.Enabled = engine.Enabled;
        }

        void OnToggleStartup(object sender, EventArgs e)
        {
            Settings.RunAtStartup = !startupItem.Checked;
            startupItem.Checked = Settings.RunAtStartup;
        }

        static void OpenMixer()
        {
            try { Process.Start("ms-settings:apps-volume"); }
            catch { Process.Start("sndvol.exe"); }
        }

        void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            engine.Dispose();
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.SessionEnding -= OnSessionEnding;
            refresh.Stop();
            tray.Visible = false;
            tray.Dispose();
            engine.Dispose();
            DestroyIcon(icon.Handle);
            base.ExitThreadCore();
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        // Speaker glyph on a rounded tile, readable on light and dark taskbars.
        static Icon CreateIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (GraphicsPath tile = RoundedRect(new Rectangle(1, 1, 30, 30), 7))
                using (Brush bg = new SolidBrush(Color.FromArgb(0, 120, 212)))
                    g.FillPath(bg, tile);
                PointF[] speaker =
                {
                    new PointF(6, 12), new PointF(11, 12), new PointF(17, 6),
                    new PointF(17, 26), new PointF(11, 20), new PointF(6, 20)
                };
                g.FillPolygon(Brushes.White, speaker);
                using (Pen p = new Pen(Color.White, 2.2f))
                {
                    g.DrawArc(p, 14, 10, 8, 12, -55, 110);
                    g.DrawArc(p, 13, 6, 14, 20, -55, 110);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
