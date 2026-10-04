using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    public partial class App : Application
    {
        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        Mutex single;
        EventWaitHandle showSignal;
        static bool reporting;

        // an unhandled error: the themed dialog while the UI is alive, the plain Windows box otherwise (or if the dialog itself fails)
        static void ReportCrash(Exception ex)
        {
            if (reporting) return;
            reporting = true;
            try
            {
                var w = Current?.MainWindow;
                if (w != null && w.IsLoaded) Dialog.Error(w.IsVisible ? w : null, "Something went wrong", ex.Message + "\n\nIt was written to the hub's log; the hub keeps running.");
                else MessageBox.Show(ex.Message, "Elan's Addon Hub", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { try { MessageBox.Show(ex.Message, "Elan's Addon Hub", MessageBoxButton.OK, MessageBoxImage.Warning); } catch { } }
            finally { reporting = false; }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var test = Array.IndexOf(e.Args, "--selftest");
            var sheet = Array.IndexOf(e.Args, "--iconsheet");
            if (sheet >= 0 && sheet + 1 < e.Args.Length) { IconSheet.Render(e.Args[sheet + 1]); Shutdown(); return; }
            var testDir = test >= 0 && test + 1 < e.Args.Length ? e.Args[test + 1] : null;
            if (testDir != null)
            {
                // the self-test runs next to a real hub: own data folder, no single-instance check
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ELANSHUB_DATA")))
                    Environment.SetEnvironmentVariable("ELANSHUB_DATA", System.IO.Path.Combine(testDir, "data"));
                ElansAddonHub.Services.SelfTestFixture.Prepare(testDir);
                showSignal = new EventWaitHandle(false, EventResetMode.AutoReset);
            }
            else
            {
                // one hub at a time: a second start just brings the first one to the front.
                // After a self-update the old hub may still be closing: wait for it instead of giving up.
                // a test run with its own data folder gets its own "one hub" lock, so it never meets the real hub
                var testData = Environment.GetEnvironmentVariable("ELANSHUB_DATA");
                var lockName = "ElansAddonHub.Single" + (string.IsNullOrEmpty(testData) ? "" : "." + testData.GetHashCode().ToString("x"));
                single = new Mutex(false, lockName);
                bool first;
                try { first = single.WaitOne(e.Args.Contains("--updated") ? 15000 : 0); }
                catch (AbandonedMutexException) { first = true; } // the old one exited without releasing it
                showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, lockName.Replace("Single", "Show"));
                if (!first)
                {
                    showSignal.Set();
                    Shutdown();
                    return;
                }
            }

            DispatcherUnhandledException += (s, ex) =>
            {
                Util.Log("crash: " + ex.Exception);
                ReportCrash(ex.Exception);
                ex.Handled = true;
            };

            var main = new MainWindow();
            MainWindow = main;
            var thread = new Thread(() =>
            {
                while (showSignal.WaitOne()) Dispatcher.BeginInvoke(new Action(main.ShowFromTray));
            }) { IsBackground = true };
            thread.Start();

            if (testDir != null) _ = main.SelfTest(testDir);
            else if (e.Args.Contains("--test-selfupdate")) { main.Show(); _ = main.TestSelfUpdate(); }
            else if (e.Args.Contains("--tray")) main.StartHidden();
            else main.Show();
        }
    }
}
