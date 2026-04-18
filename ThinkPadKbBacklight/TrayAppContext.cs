using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ThinkPadKbBacklight
{
    internal sealed class TrayAppContext : ApplicationContext
    {
        private readonly Config _config;
        private readonly string _configPath;
        private readonly BacklightController _ctrl;
        private readonly IdleMonitor _idle;
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _miPause;
        private readonly ToolStripMenuItem _miStatus;
        private readonly ToolStripMenuItem _miTimeout;

        public TrayAppContext()
        {
            _configPath = Config.DefaultPath;
            bool firstRun = !File.Exists(_configPath);
            _config = Config.LoadOrDefault(_configPath);

            _ctrl = new BacklightController();
            _idle = new IdleMonitor(_config.TimeoutSeconds) { Paused = _config.Paused };
            _idle.ActivityDetected += OnActivity;
            _idle.IdleTimeoutElapsed += OnIdle;

            _tray = new NotifyIcon
            {
                Icon = MakeIcon(),
                Visible = true,
                Text = BuildTooltip(),
            };

            var menu = new ContextMenuStrip();
            _miStatus = new ToolStripMenuItem("Status: starting…") { Enabled = false };
            menu.Items.Add(_miStatus);
            menu.Items.Add(new ToolStripSeparator());

            _miPause = new ToolStripMenuItem("Pause", null, OnTogglePause) { CheckOnClick = true, Checked = _config.Paused };
            menu.Items.Add(_miPause);

            _miTimeout = new ToolStripMenuItem("Timeout");
            foreach (int sec in new[] { 10, 15, 30, 60, 120, 300, 600 })
            {
                int s = sec;
                var item = new ToolStripMenuItem(FormatSeconds(s), null, (_, __) => SetTimeout(s)) { CheckOnClick = false };
                if (s == _config.TimeoutSeconds) item.Checked = true;
                _miTimeout.DropDownItems.Add(item);
            }
            menu.Items.Add(_miTimeout);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Run diagnostics…", null, OnDiagnose);
            menu.Items.Add("Open config folder", null, OnOpenConfig);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, OnExit);
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, __) => OnTogglePause(_miPause, EventArgs.Empty);

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            RefreshStatus();
            if (!_config.Paused) SetBacklight(_config.OnLevel);
            _idle.Start();

            if (firstRun)
            {
                try
                {
                    string path = Diagnostics.Run(_ctrl, runBacklightCycle: true);
                    _tray.ShowBalloonTip(5000, "ThinkPadKbBacklight",
                        "First-run diagnostics written to Desktop.", ToolTipIcon.Info);
                    Process.Start("notepad.exe", "\"" + path + "\"");
                }
                catch { }
            }
            _config.Save(_configPath);
        }

        private string BuildTooltip()
        {
            return string.Format("ThinkPadKbBacklight — {0}s timeout{1}",
                _config.TimeoutSeconds, _config.Paused ? " (paused)" : "");
        }

        private static string FormatSeconds(int s)
        {
            if (s < 60) return s + " s";
            if (s % 60 == 0) return (s / 60) + " min";
            return s + " s";
        }

        private void OnActivity(object sender, EventArgs e)
        {
            SetBacklight(_config.OnLevel);
            RefreshStatus();
        }

        private void OnIdle(object sender, EventArgs e)
        {
            SetBacklight(_config.OffLevel);
            RefreshStatus();
        }

        private void OnTogglePause(object sender, EventArgs e)
        {
            bool paused;
            if (sender == _miPause)
            {
                paused = _miPause.Checked;
            }
            else
            {
                paused = !_config.Paused;
                _miPause.Checked = paused;
            }
            _config.Paused = paused;
            _idle.Paused = paused;
            if (paused) SetBacklight(_config.OnLevel);
            _config.Save(_configPath);
            RefreshStatus();
        }

        private void SetTimeout(int seconds)
        {
            _config.TimeoutSeconds = seconds;
            _idle.TimeoutSeconds = seconds;
            foreach (ToolStripMenuItem mi in _miTimeout.DropDownItems)
                mi.Checked = (mi.Text == FormatSeconds(seconds));
            _config.Save(_configPath);
            RefreshStatus();
        }

        private void OnDiagnose(object sender, EventArgs e)
        {
            try
            {
                string path = Diagnostics.Run(_ctrl, runBacklightCycle: true);
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Diagnostics failed: " + ex.Message);
            }
        }

        private void OnOpenConfig(object sender, EventArgs e)
        {
            try
            {
                string dir = Path.GetDirectoryName(_configPath);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }

        private void OnExit(object sender, EventArgs e)
        {
            _tray.Visible = false;
            ExitThread();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                if (!_config.Paused) SetBacklight(_config.OnLevel);
                RefreshStatus();
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect)
            {
                if (!_config.Paused) SetBacklight(_config.OnLevel);
                RefreshStatus();
            }
        }

        private void SetBacklight(int level)
        {
            _ctrl.TrySetLevel(level, out _);
        }

        private void RefreshStatus()
        {
            string state;
            if (_config.Paused) state = "paused";
            else if (!_ctrl.IsReady) state = "ERROR: backlight API not available";
            else state = "active, " + _config.TimeoutSeconds + "s timeout";
            _miStatus.Text = "Status: " + state;
            _tray.Text = BuildTooltip();
        }

        private static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var body = new SolidBrush(Color.FromArgb(230, 30, 30, 30)))
                    g.FillRectangle(body, 2, 8, 28, 18);
                using (var glow = new SolidBrush(Color.FromArgb(220, 250, 200, 60)))
                {
                    for (int r = 0; r < 3; r++)
                        for (int c = 0; c < 6; c++)
                            g.FillRectangle(glow, 4 + c * 4, 11 + r * 5, 3, 3);
                }
                IntPtr h = bmp.GetHicon();
                return Icon.FromHandle(h);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                _idle?.Dispose();
                _ctrl?.Dispose();
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
