// TopBarDisplayWatch - restarts TopBar after the monitor layout changes.
//
// TopBar positions itself and registers its appbar exactly once, in Build(),
// from Screen.PrimaryScreen.WorkingArea. It never listens for display changes
// (no SystemEvents.DisplaySettingsChanged, no WM_DISPLAYCHANGE hook), so when a
// monitor drops out or comes back the bar loses its place until restarted.
// This does that restart for it.
//
// - Listens for WM_DISPLAYCHANGE on a hidden top-level window. It has to be
//   top-level: message-only windows (HWND_MESSAGE) never receive broadcasts.
// - Waits for the burst to settle - one monitor dropping out fires several.
// - Holds off while a fullscreen app or game has the foreground, so a game
//   switching resolution doesn't get TopBar relaunched underneath it.
// - Leaves TopBar alone if it isn't running: that was a deliberate close.
// - Logs to %AppData%\TopBar\display-watch.log.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TopBarExtras
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            bool created;
            using (Mutex single = new Mutex(true, @"Local\TopBarDisplayWatch",
                                            out created))
            {
                if (!created) return 0;  // already watching
                DisplayWatcher watcher = new DisplayWatcher();
                Application.Run();
                GC.KeepAlive(watcher);
            }
            return 0;
        }
    }

    internal sealed class DisplayWatcher : NativeWindow
    {
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int SettleMs = 3000;
        private const int FullscreenRecheckMs = 2000;

        // SHQueryUserNotificationState results that mean "don't disturb".
        private const int QUNS_BUSY = 2;
        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        private const int QUNS_PRESENTATION_MODE = 4;

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        private readonly System.Windows.Forms.Timer settle =
            new System.Windows.Forms.Timer();
        private readonly string topBarDir;
        private readonly string logPath;
        private bool waitingOnFullscreen;

        public DisplayWatcher()
        {
            topBarDir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "TopBar");
            logPath = Path.Combine(topBarDir, "display-watch.log");

            settle.Tick += OnSettled;

            CreateParams cp = new CreateParams();
            cp.Caption = "TopBarDisplayWatch";
            CreateHandle(cp);
            Log("watching for display changes (" + Layout() + ")");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DISPLAYCHANGE)
            {
                long lp = m.LParam.ToInt64();
                Log(string.Format("WM_DISPLAYCHANGE {0}x{1} - waiting {2} ms "
                                  + "for the layout to settle",
                                  lp & 0xFFFF, (lp >> 16) & 0xFFFF, SettleMs));
                waitingOnFullscreen = false;
                settle.Stop();
                settle.Interval = SettleMs;  // restart the countdown
                settle.Start();
            }
            base.WndProc(ref m);
        }

        private void OnSettled(object sender, EventArgs e)
        {
            if (FullscreenInForeground())
            {
                if (!waitingOnFullscreen)
                    Log("a fullscreen app has the foreground - holding off");
                waitingOnFullscreen = true;
                settle.Interval = FullscreenRecheckMs;
                return;  // timer keeps running; check again shortly
            }
            settle.Stop();
            waitingOnFullscreen = false;
            RestartTopBar();
        }

        private static bool FullscreenInForeground()
        {
            int state;
            if (SHQueryUserNotificationState(out state) != 0) return false;
            return state == QUNS_BUSY || state == QUNS_RUNNING_D3D_FULL_SCREEN
                || state == QUNS_PRESENTATION_MODE;
        }

        private void RestartTopBar()
        {
            Process[] running = Process.GetProcessesByName("TopBar");
            if (running.Length == 0)
            {
                Log("TopBar isn't running - leaving it closed");
                return;
            }

            string exe = null;
            foreach (Process p in running)
            {
                try { if (exe == null) exe = p.MainModule.FileName; }
                catch (Exception) { }
                Stop(p);
            }
            if (exe == null || !File.Exists(exe))
                exe = Path.Combine(topBarDir, "TopBar.exe");

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                psi.UseShellExecute = true;
                Process.Start(psi);
                Log("restarted TopBar for layout " + Layout());
            }
            catch (Exception ex)
            {
                Log("couldn't start TopBar: " + ex.Message);
            }
        }

        // Ask nicely first: TopBar's Closing handler calls Reset(), which
        // releases its appbar reservation. Only kill it if it won't go.
        private static void Stop(Process p)
        {
            try
            {
                if (p.CloseMainWindow() && p.WaitForExit(3000)) return;
                p.Kill();
                p.WaitForExit(5000);
            }
            catch (Exception) { }
        }

        private static string Layout()
        {
            StringBuilder sb = new StringBuilder();
            foreach (Screen s in Screen.AllScreens)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.AppendFormat("{0}{1} {2}x{3}@{4},{5}",
                                s.DeviceName.Replace(@"\\.\", ""),
                                s.Primary ? "*" : "",
                                s.Bounds.Width, s.Bounds.Height,
                                s.Bounds.X, s.Bounds.Y);
            }
            return sb.ToString();
        }

        private void Log(string line)
        {
            try
            {
                Directory.CreateDirectory(topBarDir);
                FileInfo fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > 64 * 1024)
                {
                    string[] all = File.ReadAllLines(logPath);
                    string[] tail = new string[all.Length / 2];
                    Array.Copy(all, all.Length - tail.Length, tail, 0, tail.Length);
                    File.WriteAllLines(logPath, tail);
                }
                File.AppendAllText(logPath, DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }
}
