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
            // one hub at a time: a second start just brings the first one to the front
            single = new Mutex(true, "ElansAddonHub.Single", out var first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "ElansAddonHub.Show");
            if (!first)
            {
                showSignal.Set();
                Shutdown();
                return;
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

            var test = Array.IndexOf(e.Args, "--selftest");
            if (test >= 0 && test + 1 < e.Args.Length) _ = main.SelfTest(e.Args[test + 1]);
            else if (e.Args.Contains("--tray")) main.StartHidden();
            else main.Show();
        }
    }
}
