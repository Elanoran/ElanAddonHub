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
        // 3.1 (.NET 10): string.GetHashCode() differs in every process there, so a name two processes must agree on uses this (FNV-1a)
        static uint StableHash(string s)
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return h;
        }

        static App()
        {
            // .NET 10: Windows code pages (1252 for the radio's song titles) need this provider; .NET Framework had them built in
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var a = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(a, "--selftest");
            if (i >= 0 && i + 1 < a.Length && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ELANSHUB_DATA")))
                Environment.SetEnvironmentVariable("ELANSHUB_DATA", System.IO.Path.Combine(System.IO.Path.GetFullPath(a[i + 1]), "data"));
            // hermetic: a WoW running on this PC must not change what the self-test sees (Round3 swaps the detector itself when it needs to)
            if (i >= 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ELANSHUB_FAKE_WOW"))) Environment.SetEnvironmentVariable("ELANSHUB_FAKE_WOW", "0");
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

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);

        // --list: the self-test section names (stdout when redirected, else the parent console)
        static void PrintSections()
        {
            var h = GetStdHandle(-11);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) AttachConsole(-1);
            // --list --changed <base> [--repo dir] / --list --only a,b: what such a run would select, without running it (Tools\selftest.ps1 -WhatIf)
            var args = Environment.GetCommandLineArgs();
            string text;
            if (TestScope.ArgValue(args, "--changed") != null || TestScope.ArgValue(args, "--only") != null || TestScope.ArgValue(args, "--paths") != null)
            {
                var picked = TestScope.FromArgs(args, out var explain, out var error);
                text = (error ?? explain) + "\r\n" + picked.Count + " section(s): " + string.Join(",", picked) + "\r\n";
            }
            else text = string.Join("\r\n", TestScope.All.Select(n => n.PadRight(15) + TestScope.Info[n])) + "\r\n";
            using (var o = Console.OpenStandardOutput()) { var b = System.Text.Encoding.UTF8.GetBytes(text); o.Write(b, 0, b.Length); o.Flush(); }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (Array.IndexOf(e.Args, "--list") >= 0) { PrintSections(); Shutdown(0); return; }
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
            if (Array.IndexOf(e.Args, "--selftest") >= 0) LinkOpen.SelfTestRun = true;   // the real elanshub:// registration is off limits to a self-test
            if (Array.IndexOf(e.Args, "--selftest") >= 0) ElansAddonHub.Services.ReleaseSignature.SelfTestRun = true;   // the fake update server may sign with its own in-memory key
            if (Array.IndexOf(e.Args, "--selftest") >= 0) ElansAddonHub.Services.Net.AllowDevSources = true;   // local zips and the fake update server stand in for GitHub (the update-source rules have their own checks)
            if (Array.IndexOf(e.Args, "--selftest") >= 0) Sounds.Muted = true;          // and nothing a self-test plays may reach the speakers
            if (Array.IndexOf(e.Args, "--selftest") >= 0) ElansAddonHub.Services.SeenData.AutoStart = false;
            if (Array.IndexOf(e.Args, "--selftest") >= 0) { ElansAddonHub.Services.Db2.ItemData.AutoStart = false; ElansAddonHub.Services.Maps.MapData.AutoStart = false; ElansAddonHub.Services.Db2.SpellData.AutoStart = false; ElansAddonHub.Services.Db2.TalentData.AutoStart = false; ElansAddonHub.Services.Db2.ClassData.AutoStart = false; }   // a self-test never reads the real game files on its own
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
                // After a self-update the old hub may still be closing (it finishes its saves first, at most 15 s): wait for it instead of giving up.
                // a test run with its own data folder gets its own "one hub" lock, so it never meets the real hub
                var testData = Environment.GetEnvironmentVariable("ELANSHUB_DATA");
                var lockName = "ElansAddonHub.Single" + (string.IsNullOrEmpty(testData) ? "" : "." + StableHash(testData).ToString("x"));
                single = new Mutex(false, lockName);
                bool first;
                try { first = single.WaitOne(e.Args.Contains("--updated") ? 30000 : 0); }
                catch (AbandonedMutexException) { first = true; } // the old one exited without releasing it
                showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, lockName.Replace("Single", "Show"));
                if (!first)
                {
                    // an elanshub:// link goes to the running hub (which brings itself forward and opens the join); otherwise just bring it forward
                    var link = LinkOpen.FromArgs(e.Args);
                    if (link == null || !LinkPipe.Send(LinkPipe.NameFor(lockName.Substring("ElansAddonHub.Single".Length)), link)) showSignal.Set();
                    Shutdown();
                    return;
                }
                linkPipeName = LinkPipe.NameFor(lockName.Substring("ElansAddonHub.Single".Length));
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

            // elanshub:// links: keep the registration current (it follows the exe after a self-update or a move) and listen for links from a second start
#if ELANS_DEVPIPE
            // a developer's build (Services/DevPipe.cs): its own pipe; the elanshub:// links stay with the real Outpost
            if (testDir == null) ElansAddonHub.Services.DevPipe.Start(main);
            if (false)
#else
            if (testDir == null && !e.Args.Contains("--test-selfupdate"))
#endif
            {
                try { if (!SettingsStore.Load().NoLinkHandler) LinkOpen.EnsureRegistered(SelfUpdater.ExePath); }
                catch (Exception ex) { Util.Log("link handler: " + ex.Message); }
                if (linkPipeName != null)
                    linkServer = new LinkPipe.Server(linkPipeName, l => Dispatcher.BeginInvoke(new Action(() => main.OpenInviteLink(l))));
            }
            var startLink = testDir == null ? LinkOpen.FromArgs(e.Args) : null;

            if (testDir != null) _ = main.SelfTest(testDir);
            else if (e.Args.Contains("--test-selfupdate")) { main.Show(); _ = main.TestSelfUpdate(); }
            else if (e.Args.Contains("--tray")) main.StartHidden();
            else if (splash) { main.StartWithSplash(); }
            else main.Show();
            if (startLink != null) main.OpenInviteLinkWhenReady(startLink);
        }

        string linkPipeName;
        LinkPipe.Server linkServer;

        MainWindow main;
    }
}
