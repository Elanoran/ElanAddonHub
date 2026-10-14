using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ElansAddonHub.Services
{
    // Which embedded WebView2 profile a control belongs to. Each has its own user-data folder (and so its own browser process group):
    //   Player  = the Lodge video player      <data>\webview2           (always InPrivate)
    //   Browser = the Browser page            <data>\webview2\browser   (InPrivate unless "Remember logins")
    //   WorldMap = the Atlas world map page   <data>\webview2\worldmap  (always InPrivate; only ever shows its own virtual host)
    public enum WebProfile { Player, Browser, WorldMap }

    // Everything the two WebView2 users share: the embedded loader, the environments, creating a control into one, and the
    // hardening that applies to every page we embed. Nothing here talks to the network by itself.
    public static class WebEngine
    {
        static readonly object gate = new object();
        static bool loaderReady;
        static Task<CoreWebView2Environment> playerEnv, browserEnv, worldEnv;

        public static string DataFolder => Path.Combine(Util.DataDir, "webview2");   // never the exe folder
        public static string UserDataFolder(WebProfile p) => p == WebProfile.Browser ? Path.Combine(DataFolder, "browser") : p == WebProfile.WorldMap ? Path.Combine(DataFolder, "worldmap") : DataFolder;

        // ---------------------------------------------------------------- loader / environment

        // The exe is a single file (Costura), so WebView2Loader.dll travels inside it as a resource: write it to
        // <data>\webview2\loader\<arch> once and tell WebView2 where it is.
        static void EnsureLoader()
        {
            lock (gate)
            {
                if (loaderReady) return;
                var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : IntPtr.Size == 8 ? "x64" : "x86";
                var dir = Path.Combine(DataFolder, "loader", arch);
                var file = Path.Combine(dir, "WebView2Loader.dll");
                var res = "WebView2Loader." + arch + ".dll";
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res))
                {
                    if (s == null) throw new InvalidOperationException("embedded " + res + " missing");
                    if (!File.Exists(file) || new FileInfo(file).Length != s.Length)
                    {
                        Directory.CreateDirectory(dir);
                        var tmp = file + "." + Process.GetCurrentProcess().Id + ".tmp";
                        using (var f = File.Create(tmp)) s.CopyTo(f);
                        try { if (File.Exists(file)) File.Delete(file); File.Move(tmp, file); }
                        catch { try { File.Delete(tmp); } catch { } if (!File.Exists(file)) throw; }
                    }
                }
                CoreWebView2Environment.SetLoaderDllFolderPath(dir);
                loaderReady = true;
            }
        }

        // true when the WebView2 runtime is installed (the loader is extracted first). Never throws.
        public static bool RuntimeAvailable(out string error)
        {
            try
            {
                EnsureLoader();
                var v = CoreWebView2Environment.GetAvailableBrowserVersionString();
                error = string.IsNullOrEmpty(v) ? "WebView2 runtime not found" : null;
                return error == null;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        public static Task<CoreWebView2Environment> GetEnvironment(WebProfile p)
        {
            lock (gate)
            {
                if (p == WebProfile.Browser) return browserEnv ?? (browserEnv = Make(p));
                if (p == WebProfile.WorldMap) return worldEnv ?? (worldEnv = Make(p));
                return playerEnv ?? (playerEnv = Make(p));
            }
        }

        static async Task<CoreWebView2Environment> Make(WebProfile p)
        {
            try
            {
                EnsureLoader();
                var data = UserDataFolder(p);
                Directory.CreateDirectory(data);
                // only the video player autoplays on its own; a browser tab must not start sound without a click
                var o = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = p == WebProfile.Player ? "--autoplay-policy=no-user-gesture-required" : "--autoplay-policy=document-user-activation-required" };
                return await CoreWebView2Environment.CreateAsync(null, data, o);
            }
            catch
            {
                lock (gate) { if (p == WebProfile.Browser) browserEnv = null; else if (p == WebProfile.WorldMap) worldEnv = null; else playerEnv = null; }
                throw;
            }
        }

        // Creates the control's browser in the profile: InPrivate (nothing survives) or the persistent profile of that environment.
        public static async Task<CoreWebView2Environment> Attach(WebView2 wv, WebProfile p, bool inPrivate)
        {
            var env = await GetEnvironment(p);
            var opts = env.CreateCoreWebView2ControllerOptions();
            opts.IsInPrivateModeEnabled = inPrivate;
            await wv.EnsureCoreWebView2Async(env, opts);
            return env;
        }

        // ---------------------------------------------------------------- hardening

        // The hardening list for every page we embed. kiosk = the video player (no menus, no keys, no zoom, no downloads);
        // otherwise the Browser page (context menu, status bar and zoom stay, our own key handling replaces the browser's accelerators).
        public static void Harden(CoreWebView2 c, bool kiosk)
        {
            var s = c.Settings;
            s.AreDevToolsEnabled = false;
            s.AreHostObjectsAllowed = false;              // no AddHostObjectToScript, no bridge
            s.IsWebMessageEnabled = false;                // WebMessageReceived is never subscribed to
            s.IsPasswordAutosaveEnabled = false;          // the browser never offers to save a password
            s.IsGeneralAutofillEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.IsBuiltInErrorPageEnabled = kiosk;          // the Browser page shows its own calm error page
            s.AreDefaultContextMenusEnabled = !kiosk;
            s.IsStatusBarEnabled = !kiosk;
            s.IsZoomControlEnabled = !kiosk;
            s.IsPinchZoomEnabled = !kiosk;
            try { c.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict; } catch (Exception e) { Util.Log("webengine tracking: " + e.Message); }
            // every permission (camera, microphone, location, notifications, clipboard read, sensors ...) is denied without asking
            c.PermissionRequested += (o, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            // a page cannot hand the URL to another program (steam:, ms-settings:, mailto: ...)
            try { c.LaunchingExternalUriScheme += (o, e) => e.Cancel = true; } catch (Exception e) { Util.Log("webengine external: " + e.Message); }
            if (kiosk) c.DownloadStarting += (o, e) => { e.Cancel = true; e.Handled = true; };
        }

        // a link that must leave the app goes to the real browser (http/https only)
        public static void OpenExternal(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
                try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch (Exception e) { Util.Log("webengine open: " + e.Message); }
        }
    }
}
