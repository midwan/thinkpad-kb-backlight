using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ThinkPadKbBacklight
{
    // Idle monitor that listens to WM_INPUT via a message-only window, then
    // only treats activity as "real" when the source device is classified as
    // internal (built-in keyboard / TrackPoint / TouchPad). External mice or
    // keyboards are ignored — the backlight won't wake from scrolling an
    // external mouse while reading, for example.
    internal sealed class RawInputIdleMonitor : IIdleMonitor
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(
            [In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32", SetLastError = true)]
        private static extern uint GetRawInputData(
            IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetRawInputDeviceInfoW(
            IntPtr hDevice, uint uiCommand, StringBuilder pData, ref uint pcbSize);

        [DllImport("user32", SetLastError = true)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32")]
        private static extern uint GetTickCount();

        private const int WM_INPUT = 0x00FF;
        private const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIDEV_DEVNOTIFY = 0x00002000;
        private const uint RIDEV_REMOVE = 0x00000001;
        private const uint RID_HEADER = 0x10000005;
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const int HWND_MESSAGE = -3;

        public event EventHandler ActivityDetected;
        public event EventHandler IdleTimeoutElapsed;

        private readonly MessageWindow _window;
        private readonly Timer _timer;
        private readonly string[] _markers;
        private readonly Dictionary<IntPtr, bool> _classifyCache = new Dictionary<IntPtr, bool>();
        private uint _lastInternalTick;
        private uint _lastExternalWmTick;
        private uint _lastInternalWmTick;
        private uint _lastObservedSystemTick;
        private bool _currentlyIdle;
        private bool _paused;
        private bool _registered;

        // How recent a WM_INPUT event must be to be considered the "cause" of
        // a system-input-tick bump. Comfortably larger than the 500ms poll
        // interval to absorb normal scheduling jitter.
        private const uint CausalWindowMs = 750;

        public int TimeoutSeconds { get; set; }

        public bool Paused
        {
            get { return _paused; }
            set
            {
                _paused = value;
                if (!_paused)
                {
                    _lastInternalTick = GetTickCount();
                    _lastObservedSystemTick = ReadSystemInputTick();
                    _currentlyIdle = false;
                }
            }
        }

        public RawInputIdleMonitor(int timeoutSeconds, string[] internalMarkers)
        {
            TimeoutSeconds = timeoutSeconds;
            _markers = (internalMarkers != null && internalMarkers.Length > 0)
                ? internalMarkers
                : DefaultMarkers();
            _lastInternalTick = GetTickCount();
            _lastObservedSystemTick = ReadSystemInputTick();
            _window = new MessageWindow(this);
            _window.CreateHandle(new CreateParams { Parent = (IntPtr)HWND_MESSAGE });
            _timer = new Timer { Interval = 500 };
            _timer.Tick += OnTick;
        }

        private static uint ReadSystemInputTick()
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            GetLastInputInfo(ref lii);
            return lii.dwTime;
        }

        public static string[] DefaultMarkers()
        {
            // Case-insensitive substring match against the raw-input device name.
            //   ACPI\    — PS/2 built-in keyboard + TrackPoint
            //   LEN      — Lenovo ACPI PnP IDs (e.g. LEN0071)
            //   VID_17EF — Lenovo USB VID (internal USB-over-HID peripherals)
            //   ELAN     — Elan touchpads (PnP ID like ELAN0672, surfaces as
            //              \\?\HID#ELAN0672#... not VEN_ELAN)
            //   SYNA     — Synaptics touchpads (SYNA####)
            return new[] { "ACPI\\", "LEN", "VID_17EF", "ELAN", "SYNA" };
        }

        public void Start()
        {
            // Register for keyboard + mouse (usage page 1) AND digitizer usages
            // (usage page 0x0D): touchpad, touchscreen, pen. Precision Touchpads
            // often surface through the digitizer page rather than as an HID
            // mouse, so covering both catches more devices.
            var devs = new[]
            {
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = _window.Handle },
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = _window.Handle },
                new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = _window.Handle },
                new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = _window.Handle },
                new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = _window.Handle },
            };
            _registered = RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            _timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_paused) return;
            uint now = GetTickCount();

            // Hybrid fallback: some Precision Touchpad drivers consume HID
            // reports and inject synthetic mouse input into the system input
            // queue without firing WM_INPUT. We'd miss those via raw-input
            // alone. So we also watch the system-wide input tick — if it
            // advanced and the only recent raw-input source we saw was an
            // external device, ignore it; otherwise count it as internal.
            uint systemTick = ReadSystemInputTick();
            if (systemTick != _lastObservedSystemTick)
            {
                _lastObservedSystemTick = systemTick;
                uint deltaExternal = unchecked(now - _lastExternalWmTick);
                uint deltaInternal = unchecked(now - _lastInternalWmTick);
                bool externalRecent = _lastExternalWmTick != 0 && deltaExternal <= CausalWindowMs;
                bool internalRecent = _lastInternalWmTick != 0 && deltaInternal <= CausalWindowMs;
                bool externalOnly = externalRecent && !internalRecent;
                if (!externalOnly)
                {
                    _lastInternalTick = now;
                    if (_currentlyIdle)
                    {
                        _currentlyIdle = false;
                        var ah = ActivityDetected;
                        if (ah != null) ah(this, EventArgs.Empty);
                    }
                }
            }

            uint idleMs = unchecked(now - _lastInternalTick);
            if (!_currentlyIdle && idleMs >= (uint)(TimeoutSeconds * 1000))
            {
                _currentlyIdle = true;
                var h = IdleTimeoutElapsed;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        private void HandleInput(IntPtr hRawInput)
        {
            uint hdrSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            uint size = hdrSize;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                uint n = GetRawInputData(hRawInput, RID_HEADER, buf, ref size, hdrSize);
                if (n == uint.MaxValue) return;
                var header = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
                bool isInternal;
                if (!_classifyCache.TryGetValue(header.hDevice, out isInternal))
                {
                    isInternal = Classify(header.hDevice);
                    _classifyCache[header.hDevice] = isInternal;
                }
                if (isInternal) OnInternalActivity();
                else OnExternalActivity();
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private void OnInternalActivity()
        {
            if (_paused) return;
            uint now = GetTickCount();
            _lastInternalWmTick = now;
            _lastInternalTick = now;
            if (_currentlyIdle)
            {
                _currentlyIdle = false;
                var h = ActivityDetected;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        private void OnExternalActivity()
        {
            _lastExternalWmTick = GetTickCount();
        }

        private bool Classify(IntPtr hDevice)
        {
            if (hDevice == IntPtr.Zero) return false;
            uint size = 0;
            GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, null, ref size);
            if (size == 0) return false;
            var sb = new StringBuilder((int)size + 1);
            GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, sb, ref size);
            return IsInternalName(sb.ToString(), _markers);
        }

        public static bool IsInternalName(string deviceName, string[] markers)
        {
            if (string.IsNullOrEmpty(deviceName) || markers == null) return false;
            string upper = deviceName.ToUpperInvariant();
            foreach (var m in markers)
            {
                if (!string.IsNullOrEmpty(m) && upper.IndexOf(m.ToUpperInvariant(), StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
            }
            if (_registered)
            {
                var devs = new[]
                {
                    new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                    new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                    new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                    new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                    new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x02, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                };
                try { RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))); } catch { }
                _registered = false;
            }
            if (_window != null && _window.Handle != IntPtr.Zero)
            {
                _window.DestroyHandle();
            }
        }

        private sealed class MessageWindow : NativeWindow
        {
            private readonly RawInputIdleMonitor _owner;
            public MessageWindow(RawInputIdleMonitor owner) { _owner = owner; }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_INPUT)
                {
                    _owner.HandleInput(m.LParam);
                }
                else if (m.Msg == WM_INPUT_DEVICE_CHANGE)
                {
                    _owner._classifyCache.Clear();
                }
                base.WndProc(ref m);
            }
        }
    }
}
