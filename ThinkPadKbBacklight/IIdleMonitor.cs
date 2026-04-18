using System;

namespace ThinkPadKbBacklight
{
    internal interface IIdleMonitor : IDisposable
    {
        event EventHandler ActivityDetected;
        event EventHandler IdleTimeoutElapsed;

        int TimeoutSeconds { get; set; }
        bool Paused { get; set; }

        void Start();
    }
}
