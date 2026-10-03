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

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var test = Array.IndexOf(e.Args, "--selftest");
            var testDir = test >= 0 && test + 1 < e.Args.Length ? e.Args[test + 1] : null;
            if (testDir != null)
            {
                // the self-test runs next to a real hub: own data folder, no single-instance check
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ELANSHUB_DATA")))
                    Environment.SetEnvironmentVariable("ELANSHUB_DATA", System.IO.Path.Combine(testDir, "data"));
                showSignal = new EventWaitHandle(false, EventResetMode.AutoReset);
            }
            else
            {
                // one hub at a time: a second start just brings the first one to the front
                single = new Mutex(true, "ElansAddonHub.Single", out var first);
                showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "ElansAddonHub.Show");
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
                MessageBox.Show(ex.Exception.Message, "Elan's Addon Hub", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            else if (e.Args.Contains("--tray")) main.StartHidden();
            else main.Show();
        }
    }
}
