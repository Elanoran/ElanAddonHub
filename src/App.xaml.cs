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

        // Self-test isolation: the data folder must be set before ANY static that reads Util.DataDir can run
        // (Util.DataDir is static readonly; touching Util/SettingsStore/IconStore first locks in the real folder).
        static App()
        {
            var a = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(a, "--selftest");
            if (i >= 0 && i + 1 < a.Length && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ELANSHUB_DATA")))
                Environment.SetEnvironmentVariable("ELANSHUB_DATA", System.IO.Path.Combine(System.IO.Path.GetFullPath(a[i + 1]), "data"));
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
                else MessageBox.Show(ex.Message, Brand.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { try { MessageBox.Show(ex.Message, Brand.Name, MessageBoxButton.OK, MessageBoxImage.Warning); } catch { } }
            finally { reporting = false; }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // hard guard: a self-test must never run against the real data folder
            if (Array.IndexOf(e.Args, "--selftest") >= 0)
            {
                var real = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElansAddonHub");
                if (string.Equals(System.IO.Path.GetFullPath(Util.DataDir).TrimEnd('\\'), System.IO.Path.GetFullPath(real).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Self-test refused: it would use the real data folder (" + real + ").", Brand.Name, MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(2); return;
                }
            }
            base.OnStartup(e);
            // Windows animations off: every Motion.* duration becomes instant (DESIGN.md, Motion)
            if (!SystemParameters.ClientAreaAnimation)
                foreach (var k in new[] { "Motion.Fast", "Motion.Standard", "Motion.Emphasized" }) Resources[k] = new System.Windows.Duration(TimeSpan.Zero);
            var test = Array.IndexOf(e.Args, "--selftest");
            var sheet = Array.IndexOf(e.Args, "--iconsheet");
            if (sheet >= 0 && sheet + 1 < e.Args.Length) { IconSheet.Render(e.Args[sheet + 1]); Shutdown(); return; }
            var probe = Array.IndexOf(e.Args, "--iconprobe");
            if (probe >= 0 && probe + 1 < e.Args.Length) { Shutdown(IconStore.Probe(e.Args[probe + 1])); return; }
            var frames = Array.IndexOf(e.Args, "--splashframes");
            if (frames >= 0 && frames + 1 < e.Args.Length) { Splash.RenderFrames(e.Args[frames + 1]); Shutdown(); return; }
            var testDir = test >= 0 && test + 1 < e.Args.Length ? e.Args[test + 1] : null;
            if (testDir != null || e.Args.Contains("--test-selfupdate")) SettingsStore.SuppressFresh = true;
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

            // the splash (own UI thread) goes up first so it is already animating while the main window is built.
            // Never for the quiet tray start, the self-test or the self-update test; after a self-update it says so.
            var quiet = e.Args.Contains("--tray") || e.Args.Contains("--test-selfupdate") || testDir != null;
            var splash = false;
            if (!quiet)
            {
                try
                {
                    if (!SettingsStore.Load().NoSplash)
                    {
                        splash = true;
                        Splash.Start(() => { try { main?.FadeIn(); } catch { } });
                        if (e.Args.Contains("--updated")) Splash.Step("Updated to v" + Version);
                    }
                }
                catch (Exception ex) { Util.Log("splash start failed: " + ex.Message); splash = false; }
            }
            main = new MainWindow();
            MainWindow = main;
            var thread = new Thread(() =>
            {
                while (showSignal.WaitOne()) Dispatcher.BeginInvoke(new Action(main.ShowFromTray));
            }) { IsBackground = true };
            thread.Start();

            if (testDir != null) _ = main.SelfTest(testDir);
            else if (e.Args.Contains("--test-selfupdate")) { main.Show(); _ = main.TestSelfUpdate(); }
            else if (e.Args.Contains("--tray")) main.StartHidden();
            else if (splash) { main.StartWithSplash(); }
            else main.Show();
        }

        MainWindow main;
    }
}
