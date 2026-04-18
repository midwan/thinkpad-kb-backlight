using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ThinkPadKbBacklight
{
    internal static class Diagnostics
    {
        public static string Run(BacklightController ctrl, bool runBacklightCycle)
        {
            var sb = new StringBuilder();
            Header(sb);
            OsAndModel(sb);
            Vantage(sb);
            DllSearch(sb, ctrl);
            IbmPmDrvBlock(sb, ctrl);
            Backend(sb, ctrl);
            RawInputDevices(sb);
            CurrentLevel(sb, ctrl);
            if (runBacklightCycle) Cycle(sb, ctrl);
            return WriteLog(sb.ToString());
        }

        private static void Header(StringBuilder sb)
        {
            sb.AppendLine("ThinkPadKbBacklight diagnostic report");
            sb.AppendLine("=====================================");
            sb.AppendLine("Time (UTC):           " + DateTime.UtcNow.ToString("o"));
            var asm = Assembly.GetExecutingAssembly();
            sb.AppendLine("App version:          " + asm.GetName().Version);
            sb.AppendLine();
        }

        private static void OsAndModel(StringBuilder sb)
        {
            sb.AppendLine("-- OS & hardware --");
            sb.AppendLine("OS version:           " + Environment.OSVersion.VersionString);
            sb.AppendLine("64-bit OS:            " + Environment.Is64BitOperatingSystem);
            sb.AppendLine("64-bit process:       " + Environment.Is64BitProcess);
            sb.AppendLine("Machine:              " + Environment.MachineName);
            TryWmi(sb, "Model/family",
                "SELECT Name, Version, Vendor FROM Win32_ComputerSystemProduct",
                mo => string.Format("name='{0}', version='{1}', vendor='{2}'",
                    mo["Name"], mo["Version"], mo["Vendor"]));
            TryWmi(sb, "BIOS",
                "SELECT SMBIOSBIOSVersion, Manufacturer, ReleaseDate FROM Win32_BIOS",
                mo => string.Format("version='{0}', manuf='{1}', releaseDate='{2}'",
                    mo["SMBIOSBIOSVersion"], mo["Manufacturer"], mo["ReleaseDate"]));
            sb.AppendLine();
        }

        private static void Vantage(StringBuilder sb)
        {
            sb.AppendLine("-- Lenovo Vantage / companion software --");
            string[] candidateRoots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"C:\Program Files\WindowsApps",
            };
            var hits = new List<string>();
            foreach (var root in candidateRoots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                AddMatches(hits, root, "LenovoCompanion*", 2);
                AddMatches(hits, root, "E046963F.LenovoCompanion*", 2);
                AddMatches(hits, root, "Lenovo", 1);
                AddMatches(hits, root, "Lenovo Vantage*", 2);
                AddMatches(hits, root, "HOTKEY", 3);
            }
            if (hits.Count == 0)
            {
                sb.AppendLine("No Lenovo software directories matched common patterns.");
            }
            else
            {
                foreach (var h in hits) sb.AppendLine("  " + h);
            }
            sb.AppendLine();
        }

        private static void AddMatches(List<string> hits, string root, string pattern, int maxDepth)
        {
            try
            {
                var stack = new Stack<Tuple<string, int>>();
                stack.Push(Tuple.Create(root, 0));
                while (stack.Count > 0)
                {
                    var top = stack.Pop();
                    string dir = top.Item1;
                    int depth = top.Item2;
                    string[] subs = null;
                    try { subs = Directory.GetDirectories(dir, pattern); } catch { }
                    if (subs != null) foreach (var s in subs) if (!hits.Contains(s)) hits.Add(s);
                    if (depth >= maxDepth) continue;
                    string[] all = null;
                    try { all = Directory.GetDirectories(dir); } catch { }
                    if (all != null) foreach (var a in all) stack.Push(Tuple.Create(a, depth + 1));
                }
            }
            catch { }
        }

        private static void DllSearch(StringBuilder sb, BacklightController ctrl)
        {
            sb.AppendLine("-- Keyboard_Core.dll search --");
            int count = 0;
            var paths = ctrl.FoundKeyboardCoreDllsWithStatus(out var denied);
            foreach (var path in paths)
            {
                sb.AppendLine("  FOUND: " + path);
                count++;
            }
            if (count == 0) sb.AppendLine("  no matches found");
            if (denied != null && denied.Count > 0)
            {
                sb.AppendLine("  (note: access denied while enumerating: " + string.Join(", ", denied.ToArray()) + ")");
            }
            sb.AppendLine("Loaded DLL:           " + (ctrl.DllPath ?? "(none)"));
            sb.AppendLine("SetKeyboardBackLightStatus resolved: " + ctrl.VantageDllReady);
            sb.AppendLine();
        }

        private static void IbmPmDrvBlock(StringBuilder sb, BacklightController ctrl)
        {
            sb.AppendLine("-- Legacy IbmPmDrv --");
            sb.AppendLine(@"\\.\IBMPmDrv opens: " + ctrl.IbmPmDrvOpen);
            if (ctrl.IbmPmDrvOpen && ctrl.TryGetIbmPmDrvState(out int code, out int cur, out int max, out bool hasBl, out string err))
            {
                sb.AppendLine(string.Format("  raw code:          0x{0:X8}", code));
                sb.AppendLine(string.Format("  current level:     {0}", cur));
                sb.AppendLine(string.Format("  max level:         {0}", max));
                sb.AppendLine(string.Format("  has-backlight bit: {0}", hasBl));
            }
            else if (ctrl.IbmPmDrvOpen)
            {
                sb.AppendLine("  could not read state: " + err);
            }
            sb.AppendLine();
        }

        private static void Backend(StringBuilder sb, BacklightController ctrl)
        {
            sb.AppendLine("-- Active backend --");
            sb.AppendLine("Using:                " + ctrl.ActiveBackend);
            sb.AppendLine();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        [DllImport("user32", SetLastError = true)]
        private static extern uint GetRawInputDeviceList(
            [In, Out] RAWINPUTDEVICELIST[] pRawInputDeviceList,
            ref uint puiNumDevices, uint cbSize);

        [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetRawInputDeviceInfoW(
            IntPtr hDevice, uint uiCommand, StringBuilder pData, ref uint pcbSize);

        private const uint RIDI_DEVICENAME = 0x20000007;

        private static void RawInputDevices(StringBuilder sb)
        {
            sb.AppendLine("-- Raw input devices (HID keyboards/mice) --");
            try
            {
                uint count = 0;
                uint size = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
                GetRawInputDeviceList(null, ref count, size);
                if (count == 0) { sb.AppendLine("  no devices"); sb.AppendLine(); return; }
                var devs = new RAWINPUTDEVICELIST[count];
                GetRawInputDeviceList(devs, ref count, size);
                int printed = 0;
                foreach (var d in devs)
                {
                    if (d.dwType > 2) continue;
                    uint strSize = 0;
                    GetRawInputDeviceInfoW(d.hDevice, RIDI_DEVICENAME, null, ref strSize);
                    if (strSize == 0) continue;
                    var buf = new StringBuilder((int)strSize + 1);
                    GetRawInputDeviceInfoW(d.hDevice, RIDI_DEVICENAME, buf, ref strSize);
                    string type = d.dwType == 0 ? "mouse" : d.dwType == 1 ? "keyboard" : "hid";
                    sb.AppendLine("  [" + type + "] " + buf.ToString());
                    printed++;
                }
                if (printed == 0) sb.AppendLine("  (none listed)");
            }
            catch (Exception ex)
            {
                sb.AppendLine("  error enumerating raw input: " + ex.Message);
            }
            sb.AppendLine();
        }

        private static void CurrentLevel(StringBuilder sb, BacklightController ctrl)
        {
            sb.AppendLine("-- Current backlight state --");
            if (ctrl.TryGetLevel(out int level, out string err))
                sb.AppendLine("Current level:        " + level);
            else
                sb.AppendLine("Cannot read level:    " + err);
            sb.AppendLine();
        }

        private static void Cycle(StringBuilder sb, BacklightController ctrl)
        {
            sb.AppendLine("-- Backlight cycle test --");
            if (!ctrl.IsReady)
            {
                sb.AppendLine("Skipped: no backend ready.");
                sb.AppendLine();
                return;
            }
            int restore = 2;
            if (ctrl.TryGetLevel(out int cur, out _)) restore = cur;

            int[] steps = { 0, 1, 2, 0 };
            foreach (var s in steps)
            {
                bool ok = ctrl.TrySetLevel(s, out string err);
                sb.AppendLine(string.Format("  set level {0}: {1}{2}", s, ok ? "OK" : "FAIL", ok ? "" : " (" + err + ")"));
                Thread.Sleep(1500);
            }
            ctrl.TrySetLevel(restore, out _);
            sb.AppendLine(string.Format("  restored to level {0}", restore));
            sb.AppendLine();
        }

        private static void TryWmi(StringBuilder sb, string label, string query, Func<ManagementObject, string> render)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(query))
                using (var col = s.Get())
                {
                    bool any = false;
                    foreach (ManagementObject mo in col)
                    {
                        sb.AppendLine(label + ": " + render(mo));
                        any = true;
                    }
                    if (!any) sb.AppendLine(label + ": (no results)");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine(label + ": WMI error - " + ex.Message);
            }
        }

        private static string WriteLog(string body)
        {
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string file = Path.Combine(desk, "ThinkPadKbBacklight-diagnostic-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            File.WriteAllText(file, body, Encoding.UTF8);
            return file;
        }
    }
}
