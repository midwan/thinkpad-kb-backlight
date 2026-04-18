using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ThinkPadKbBacklight
{
    internal sealed class BacklightController : IDisposable
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr CreateFileW(
            [MarshalAs(UnmanagedType.LPWStr)] string filename,
            uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetBacklightDelegate(int level);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetBacklightDelegate();

        private IntPtr _dllHandle = IntPtr.Zero;
        private SetBacklightDelegate _setBacklight;
        private GetBacklightDelegate _getBacklight;

        public string DllPath { get; private set; }
        public bool IsReady => _setBacklight != null;
        public bool IbmPmDrvPresent { get; private set; }

        public BacklightController()
        {
            IbmPmDrvPresent = ProbeIbmPmDrv();
            TryLoadKeyboardCore();
        }

        public bool TrySetLevel(int level, out string error)
        {
            error = null;
            if (level < 0) level = 0;
            if (level > 2) level = 2;

            if (_setBacklight == null)
            {
                error = "Keyboard_Core.dll not loaded; cannot set backlight.";
                return false;
            }

            try
            {
                int rc = _setBacklight(level);
                if (rc != 0)
                {
                    error = "SetKeyboardBackLightStatus returned " + rc;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "SetKeyboardBackLightStatus threw: " + ex.Message;
                return false;
            }
        }

        public bool TryGetLevel(out int level, out string error)
        {
            level = -1;
            error = null;
            if (_getBacklight == null)
            {
                error = "GetKeyboardBackLightStatus not available.";
                return false;
            }
            try
            {
                level = _getBacklight();
                return true;
            }
            catch (Exception ex)
            {
                error = "GetKeyboardBackLightStatus threw: " + ex.Message;
                return false;
            }
        }

        public IEnumerable<string> FoundKeyboardCoreDlls() => SearchForDll();

        private void TryLoadKeyboardCore()
        {
            foreach (var candidate in SearchForDll())
            {
                IntPtr h = LoadLibrary(candidate);
                if (h == IntPtr.Zero) continue;

                IntPtr setPtr = GetProcAddress(h, "SetKeyboardBackLightStatus");
                if (setPtr == IntPtr.Zero)
                {
                    FreeLibrary(h);
                    continue;
                }

                _dllHandle = h;
                _setBacklight = (SetBacklightDelegate)Marshal.GetDelegateForFunctionPointer(setPtr, typeof(SetBacklightDelegate));
                DllPath = candidate;

                IntPtr getPtr = GetProcAddress(h, "GetKeyboardBackLightStatus");
                if (getPtr != IntPtr.Zero)
                {
                    _getBacklight = (GetBacklightDelegate)Marshal.GetDelegateForFunctionPointer(getPtr, typeof(GetBacklightDelegate));
                }
                return;
            }
        }

        private static IEnumerable<string> SearchForDll()
        {
            var roots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"C:\Program Files\WindowsApps",
                @"C:\Program Files (x86)\Lenovo",
                @"C:\Program Files\Lenovo",
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (var path in SafeSearch(root, "Keyboard_Core.dll", 6))
                {
                    if (seen.Add(path)) yield return path;
                }
            }
        }

        private static IEnumerable<string> SafeSearch(string root, string fileName, int maxDepth)
        {
            var stack = new Stack<Tuple<string, int>>();
            stack.Push(Tuple.Create(root, 0));
            while (stack.Count > 0)
            {
                var top = stack.Pop();
                string dir = top.Item1;
                int depth = top.Item2;
                string[] files = null;
                string[] subs = null;
                try { files = Directory.GetFiles(dir, fileName); } catch { }
                if (files != null)
                {
                    foreach (var f in files) yield return f;
                }
                if (depth >= maxDepth) continue;
                try { subs = Directory.GetDirectories(dir); } catch { }
                if (subs != null)
                {
                    foreach (var s in subs) stack.Push(Tuple.Create(s, depth + 1));
                }
            }
        }

        private static bool ProbeIbmPmDrv()
        {
            IntPtr h = CreateFileW(@"\\.\IBMPmDrv", GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE) return false;
            CloseHandle(h);
            return true;
        }

        public void Dispose()
        {
            _setBacklight = null;
            _getBacklight = null;
            if (_dllHandle != IntPtr.Zero)
            {
                FreeLibrary(_dllHandle);
                _dllHandle = IntPtr.Zero;
            }
        }
    }
}
