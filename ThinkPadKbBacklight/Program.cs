using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace ThinkPadKbBacklight
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool diagnose = Array.Exists(args, a => a == "--diagnose" || a == "-d");

            if (diagnose)
            {
                using (var ctrl = new BacklightController())
                {
                    string path = Diagnostics.Run(ctrl, runBacklightCycle: true);
                    try { Process.Start("notepad.exe", "\"" + path + "\""); } catch { }
                }
                return 0;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "Global\\ThinkPadKbBacklight-singleton", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("ThinkPadKbBacklight is already running.", "ThinkPadKbBacklight",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var ctx = new TrayAppContext())
                {
                    Application.Run(ctx);
                }
            }
            return 0;
        }
    }
}
