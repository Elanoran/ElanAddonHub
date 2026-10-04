using System;
using System.IO;
using System.Text;

namespace ElansAddonHub.Services
{
    // Makes --selftest self-contained: a fake WoW folder (only when the data folder has no settings yet) and a synthetic
    // CurseForge AddonGameInstance.json whose installPath is the fake client folder (unless ELANSHUB_CF_FILE is set).
    // Never touches the real settings, WoW folder or CurseForge files.
    public static class SelfTestFixture
    {
        static string J(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static void Toc(string ao, string folder, string text)
        {
            var d = Path.Combine(ao, folder);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, folder + ".toc"), text);
        }

        public static void Prepare(string testDir)
        {
            try
            {
                var data = Environment.GetEnvironmentVariable("ELANSHUB_DATA");
                Directory.CreateDirectory(data);
                var settings = Path.Combine(data, "settings.json");
                string root;
                if (!File.Exists(settings))
                {
                    root = Path.GetFullPath(Path.Combine(testDir, "wow"));
                    var ao0 = Path.Combine(root, "_classic_beta_", "Interface", "AddOns");
                    Toc(ao0, "Plain", "## Interface: 16001\n## Title: Plain Addon\n## Version: 1.0\n## Author: Me\n");
                    Toc(ao0, "DevAddon", "## Interface: 16001\n## Title: Dev\n"); Directory.CreateDirectory(Path.Combine(ao0, "DevAddon", ".git"));
                    Toc(ao0, "CfMulti", "## Interface: 16001\n## Title: CfMulti Title\n## Version: 1.0\n## Author: Cf\n");
                    Toc(ao0, "CfMulti_Opts", "## Interface: 16001\n## Title: CfMulti Options\n");
                    Toc(ao0, "CfUpToDate", "## Interface: 16001\n## Title: CfUpToDate\n## Version: 3.1\n");
                    Toc(ao0, "Elsewhere", "## Interface: 16001\n## Title: Elsewhere\n");
                    Toc(ao0, "GhLinked", "## Interface: 16001\n## Title: Github Linked\n## Version: 1.0\n## X-Website: https://github.com/example/ghlinked\n");
                    Toc(ao0, "WowiLinked", "## Interface: 16001\n## Title: WoWI Linked\n## Version: 1.0\n## X-WoWI-ID: 4242\n");
                    Toc(ao0, "WagoOnly", "## Interface: 16001\n## Title: Wago Only\n## Version: 1.0\n## X-Wago-ID: abcd1234\n");
                    File.WriteAllText(settings, "{\"wowRoot\":" + J(root) + ",\"manifestUrl\":\"http://127.0.0.1:9/none.json\",\"runInBackground\":false,\"checkMinutes\":30}");
                }
                else
                {
                    var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(settings), "\"wowRoot\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (!m.Success) return;
                    root = m.Groups[1].Value.Replace("\\\\", "\\");
                }
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ELANSHUB_CF_FILE"))) return;
                var flav = Path.Combine(root, "_classic_beta_");
                var ao = Path.Combine(flav, "Interface", "AddOns");
                if (!Directory.Exists(Path.Combine(ao, "CfMulti"))) return;      // not our fixture: leave CurseForge alone
                string Add(int id, string name, string[] folders, int inst, int lat, string fn)
                {
                    var sb = new StringBuilder();
                    var fps = new StringBuilder(); var mods = new StringBuilder();
                    foreach (var f in folders)
                    {
                        if (fps.Length > 0) { fps.Append(','); mods.Append(','); }
                        fps.Append(J(Path.Combine(ao, f))); mods.Append("{\"foldername\":" + J(f) + ",\"fingerprint\":1}");
                    }
                    return "{\"addonID\":" + id + ",\"name\":" + J(name) + ",\"webSiteURL\":" + J("https://www.curseforge.com/wow/addons/x" + id) + ",\"modFolderPath\":" + J(ao)
                        + ",\"filePaths\":[" + fps + "],\"installedFile\":{\"id\":" + inst + ",\"fileName\":" + J(fn.Replace("2.0", "1.0")) + ",\"fileDate\":\"2026-09-01T10:00:00Z\",\"modules\":[" + mods + "]},"
                        + "\"latestFile\":{\"id\":" + lat + ",\"fileName\":" + J(fn) + ",\"fileDate\":\"2026-10-01T10:00:00Z\",\"modules\":[" + mods + "],\"downloadUrl\":\"DO-NOT-USE\"}}";
                }
                var now = DateTimeOffset.Now.AddMinutes(-47).ToString("o");
                var json = "[{\"name\":\"Other\",\"installPath\":\"C:/Nope/\",\"installedAddons\":[]},"
                    + "{\"name\":\"Forever\",\"installPath\":" + J(flav + "\\") + ",\"lastRefreshAttempt\":" + J(now) + ",\"installedAddons\":["
                    + Add(101, "CfMulti", new[] { "CfMulti", "CfMulti_Opts" }, 111, 222, "CfMulti-2.0.zip") + ","
                    + Add(102, "CfUpToDate", new[] { "CfUpToDate" }, 333, 333, "CfUpToDate-3.1.zip") + ","
                    + Add(103, "Missing", new[] { "NotInstalled" }, 1, 2, "M.zip") + "]}]";
                var cf = Path.Combine(data, "AddonGameInstance.json");
                File.WriteAllText(cf, json);
                Environment.SetEnvironmentVariable("ELANSHUB_CF_FILE", cf);
            }
            catch (Exception e) { Util.Log("selftest fixture: " + e.Message); }
        }
    }
}
