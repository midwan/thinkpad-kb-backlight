using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

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

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(
            string filename, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice, uint code,
            byte[] inBuf, uint inSize,
            byte[] outBuf, uint outSize,
            out uint bytesReturned, IntPtr overlapped);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        private const uint IOCTL_IBMPMDRV_READ = 2238080;
        private const uint IOCTL_IBMPMDRV_WRITE = 2238084;
        private const int IBMPMDRV_HAS_BACKLIGHT_BIT = 0x200000;
        private const int IBMPMDRV_WRITE_FLAG = 0x100;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetBacklightDelegate(int level);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetBacklightDelegate();

        public enum Backend { None, VantageDll, IbmPmDrv }

        private IntPtr _dllHandle = IntPtr.Zero;
        private SetBacklightDelegate _setDll;
        private GetBacklightDelegate _getDll;

        private IntPtr _pmHandle = IntPtr.Zero;

        public string DllPath { get; private set; }
        public bool VantageDllReady => _setDll != null;
        public bool IbmPmDrvOpen => _pmHandle != IntPtr.Zero && _pmHandle != INVALID_HANDLE_VALUE;

        public Backend ActiveBackend
        {
            get
            {
                if (VantageDllReady) return Backend.VantageDll;
                if (IbmPmDrvOpen) return Backend.IbmPmDrv;
                return Backend.None;
            }
        }

        public bool IsReady => ActiveBackend != Backend.None;

        public BacklightController()
        {
            OpenIbmPmDrv();
            TryLoadKeyboardCore();
        }

        public bool TrySetLevel(int level, out string error)
        {
            error = null;
            if (level < 0) level = 0;
            if (level > 2) level = 2;

            if (VantageDllReady)
            {
                try
                {
                    int rc = _setDll(level);
                    if (rc == 0) return true;
                    error = "Vantage DLL SetKeyboardBackLightStatus returned " + rc;
                }
                catch (Exception ex)
                {
                    error = "Vantage DLL threw: " + ex.Message;
                }
            }

            if (IbmPmDrvOpen)
            {
                if (IbmPmDrvSetLevel(level, out string drvErr)) { error = null; return true; }
                if (error == null) error = drvErr;
                else error += "; then IbmPmDrv: " + drvErr;
                return false;
            }

            if (error == null) error = "No backlight backend available.";
            return false;
        }

        public bool TryGetLevel(out int level, out string error)
        {
            level = -1;
            error = null;
            if (VantageDllReady && _getDll != null)
            {
                try { level = _getDll(); return true; }
                catch (Exception ex) { error = "Vantage DLL threw: " + ex.Message; }
            }
            if (IbmPmDrvOpen)
            {
                if (IbmPmDrvRead(out int code, out string drvErr))
                {
                    level = code & 0x0F;
                    return true;
                }
                error = drvErr;
                return false;
            }
            if (error == null) error = "No backlight backend available.";
            return false;
        }

        public bool TryGetIbmPmDrvState(out int code, out int currentLevel, out int maxLevel, out bool hasBacklight, out string error)
        {
            code = 0; currentLevel = -1; maxLevel = -1; hasBacklight = false;
            if (!IbmPmDrvOpen) { error = "IbmPmDrv not open."; return false; }
            if (!IbmPmDrvRead(out code, out error)) return false;
            currentLevel = code & 0x0F;
            maxLevel = (code >> 8) & 0x0F;
            hasBacklight = (code & IBMPMDRV_HAS_BACKLIGHT_BIT) != 0;
            return true;
        }

        public IEnumerable<string> FoundKeyboardCoreDlls() => SearchForDll(out _);

        public IEnumerable<string> FoundKeyboardCoreDllsWithStatus(out List<string> accessDenied)
            => SearchForDll(out accessDenied);

        private bool IbmPmDrvRead(out int code, out string error)
        {
            code = 0;
            byte[] inp = BitConverter.GetBytes(0);
            byte[] outp = new byte[4];
            bool ok = DeviceIoControl(_pmHandle, IOCTL_IBMPMDRV_READ, inp, 4, outp, 4, out _, IntPtr.Zero);
            if (!ok)
            {
                error = "DeviceIoControl(read) failed, win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            code = BitConverter.ToInt32(outp, 0);
            error = null;
            return true;
        }

        private bool IbmPmDrvWrite(int arg, out string error)
        {
            byte[] inp = BitConverter.GetBytes(arg);
            byte[] outp = new byte[4];
            bool ok = DeviceIoControl(_pmHandle, IOCTL_IBMPMDRV_WRITE, inp, 4, outp, 4, out _, IntPtr.Zero);
            if (!ok)
            {
                error = "DeviceIoControl(write) failed, win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            error = null;
            return true;
        }

        private bool IbmPmDrvSetLevel(int level, out string error)
        {
            if (!IbmPmDrvRead(out int code, out error)) return false;
            int arg = ((code & IBMPMDRV_HAS_BACKLIGHT_BIT) != 0 ? IBMPMDRV_WRITE_FLAG : 0)
                      | (code & 0xF0)
                      | (level & 0x0F);
            return IbmPmDrvWrite(arg, out error);
        }

        private void OpenIbmPmDrv()
        {
            IntPtr h = CreateFileW(@"\\.\IBMPmDrv", GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h != IntPtr.Zero && h != INVALID_HANDLE_VALUE) _pmHandle = h;
        }

        private void TryLoadKeyboardCore()
        {
            foreach (var candidate in SearchForDll(out _))
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
                _setDll = (SetBacklightDelegate)Marshal.GetDelegateForFunctionPointer(setPtr, typeof(SetBacklightDelegate));
                DllPath = candidate;

                IntPtr getPtr = GetProcAddress(h, "GetKeyboardBackLightStatus");
                if (getPtr != IntPtr.Zero)
                    _getDll = (GetBacklightDelegate)Marshal.GetDelegateForFunctionPointer(getPtr, typeof(GetBacklightDelegate));
                return;
            }
        }

        private static IEnumerable<string> SearchForDll(out List<string> deniedRoots)
        {
            var roots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"C:\Program Files\WindowsApps",
                @"C:\Program Files (x86)\Lenovo",
                @"C:\Program Files\Lenovo",
            };

            deniedRoots = new List<string>();
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                bool denied;
                foreach (var path in SafeSearch(root, "Keyboard_Core.dll", 6, out denied))
                {
                    if (seen.Add(path)) results.Add(path);
                }
                if (denied) deniedRoots.Add(root);
            }
            return results;
        }

        private static IEnumerable<string> SafeSearch(string root, string fileName, int maxDepth, out bool deniedAnywhere)
        {
            var result = new List<string>();
            bool denied = false;
            var stack = new Stack<Tuple<string, int>>();
            stack.Push(Tuple.Create(root, 0));
            while (stack.Count > 0)
            {
                var top = stack.Pop();
                string dir = top.Item1;
                int depth = top.Item2;
                try
                {
                    foreach (var f in Directory.GetFiles(dir, fileName)) result.Add(f);
                }
                catch (UnauthorizedAccessException) { denied = true; }
                catch { }

                if (depth >= maxDepth) continue;
                try
                {
                    foreach (var s in Directory.GetDirectories(dir)) stack.Push(Tuple.Create(s, depth + 1));
                }
                catch (UnauthorizedAccessException) { denied = true; }
                catch { }
            }
            deniedAnywhere = denied;
            return result;
        }

        public void Dispose()
        {
            _setDll = null;
            _getDll = null;
            if (_dllHandle != IntPtr.Zero)
            {
                FreeLibrary(_dllHandle);
                _dllHandle = IntPtr.Zero;
            }
            if (IbmPmDrvOpen)
            {
                CloseHandle(_pmHandle);
                _pmHandle = IntPtr.Zero;
            }
        }
    }
}
