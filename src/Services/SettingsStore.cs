using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace ElansAddonHub.Services
{
    public static class SettingsStore
    {
        // Where releases are published. "latest/download" always points at the newest release.
        public const string DefaultManifestUrl = "https://github.com/Elanoran/ElanAddonHub/releases/latest/download/manifest.json";

        static readonly string FilePath = Path.Combine(Util.DataDir, "settings.json");

        // true when this start found no settings file at all = a fresh install (the first-run guide). Not for the self-test.
        public static bool Fresh;
        public static bool SuppressFresh;

        public static Settings Load()
        {
            Settings s = null;
            if (!SuppressFresh && !File.Exists(FilePath)) Fresh = true;
            try { if (File.Exists(FilePath)) s = Util.FromJson<Settings>(File.ReadAllText(FilePath)); }
            catch (Exception e) { Util.Log("settings load failed: " + e.Message); }
            s = s ?? new Settings { RunInBackground = true, CheckMinutes = 30 };
            if (string.IsNullOrWhiteSpace(s.ManifestUrl)) s.ManifestUrl = DefaultManifestUrl;
            if (s.CheckMinutes < 5) s.CheckMinutes = 30;
            s.Notified = s.Notified ?? new List<string>();
            return s;
        }

        public static void Save(Settings s)
        {
            try { File.WriteAllText(FilePath, Util.ToJson(s)); }
            catch (Exception e) { Util.Log("settings save failed: " + e.Message); }
        }

        public static string Protect(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return null;
            var bytes = System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(secret), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            try
            {
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(stored), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ElansAddonHub";

        public static void ApplyStartWithWindows(bool on, string exePath)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunName, $"\"{exePath}\" --tray");
                    else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
                }
            }
            catch (Exception e) { Util.Log("start with windows failed: " + e.Message); }
        }
    }
}
