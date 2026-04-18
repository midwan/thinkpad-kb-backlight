using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ThinkPadKbBacklight
{
    internal sealed class IdleMonitor : IIdleMonitor
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32", SetLastError = true)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32")]
        private static extern uint GetTickCount();

        public event EventHandler ActivityDetected;
        public event EventHandler IdleTimeoutElapsed;

        private readonly Timer _timer;
        private uint _lastSeenInputTick;
        private bool _currentlyIdle;
        private bool _paused;

        public int TimeoutSeconds { get; set; }

        public IdleMonitor(int timeoutSeconds)
        {
            TimeoutSeconds = timeoutSeconds;
            _lastSeenInputTick = ReadLastInputTick();
            _currentlyIdle = false;
            _timer = new Timer { Interval = 500 };
            _timer.Tick += OnTick;
        }

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();

        public bool Paused
        {
            get => _paused;
            set
            {
                _paused = value;
                if (!_paused)
                {
                    _lastSeenInputTick = ReadLastInputTick();
                    _currentlyIdle = false;
                }
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_paused) return;

            uint current = ReadLastInputTick();
            uint now = GetTickCount();

            if (current != _lastSeenInputTick)
            {
                _lastSeenInputTick = current;
                if (_currentlyIdle)
                {
                    _currentlyIdle = false;
                    ActivityDetected?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            uint idleMs = unchecked(now - current);
            if (!_currentlyIdle && idleMs >= (uint)(TimeoutSeconds * 1000))
            {
                _currentlyIdle = true;
                IdleTimeoutElapsed?.Invoke(this, EventArgs.Empty);
            }
        }

        private static uint ReadLastInputTick()
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            GetLastInputInfo(ref lii);
            return lii.dwTime;
        }

        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
        }
    }
}
