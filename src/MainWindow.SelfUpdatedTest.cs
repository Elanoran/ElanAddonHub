using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // The "selfupdated" self-test section: addons that carry a CurseForge / Wago id but are NOT managed by the CurseForge app (RestedXP's RXPGuides
    // is the model: its own updater, a git-describe dev version, a logo in its Textures folder) get the "Self" source instead of a dim CurseForge
    // chip, git-describe versions read as "base + N commits", and every other-addon row shows the addon's own logo when it ships one.
    // Everything runs on a throw-away fake AddOns folder inside the test folder: nothing from the real game folder, no network.
    public partial class MainWindow
    {
        // ---- fixture pieces (also used by the design shot)
        static uint Bgra(int r, int g, int b, int a = 255) => (uint)(b | g << 8 | r << 16 | a << 24);

        // a tiny BLP2: raw BGRA, one mip (what Blp.Decode reads as "enc 3")
        static byte[] MakeBlp(int w, int h, Func<int, int, uint> px)
        {
            var d = new byte[148 + w * h * 4];
            d[0] = (byte)'B'; d[1] = (byte)'L'; d[2] = (byte)'P'; d[3] = (byte)'2';
            BitConverter.GetBytes(1u).CopyTo(d, 4);
            d[8] = 3; d[9] = 8; d[10] = 8; d[11] = 0;
            BitConverter.GetBytes((uint)w).CopyTo(d, 12); BitConverter.GetBytes((uint)h).CopyTo(d, 16);
            BitConverter.GetBytes(148u).CopyTo(d, 20); BitConverter.GetBytes((uint)(w * h * 4)).CopyTo(d, 84);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) BitConverter.GetBytes(px(x, y)).CopyTo(d, 148 + (y * w + x) * 4);
            return d;
        }

        // our own artwork for the fixture (the real RXP logo is licensed CC BY-NC-SA and is not copied): a gold ring on a deep blue tile
        static Func<int, int, uint> RingLogo(int size, int r, int g, int b) => (x, y) =>
        {
            double c = (size - 1) / 2.0, dx = x - c, dy = y - c, d = Math.Sqrt(dx * dx + dy * dy) / (size / 2.0);
            if (d > 0.97) return Bgra(0, 0, 0, 0);
            if (d > 0.62 && d < 0.84) return Bgra(r, g, b);
            if (d <= 0.28) return Bgra(r, g, b);
            return Bgra(24, 38, 82);
        };

        // an uncompressed 32 bit TGA, rows stored bottom first (the usual way)
        static byte[] MakeTga(int w, int h, Func<int, int, uint> px)
        {
            var d = new byte[18 + w * h * 4];
            d[2] = 2; BitConverter.GetBytes((ushort)w).CopyTo(d, 12); BitConverter.GetBytes((ushort)h).CopyTo(d, 14); d[16] = 32; d[17] = 8;   // 8 alpha bits, origin bottom-left
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) BitConverter.GetBytes(px(x, h - 1 - y)).CopyTo(d, 18 + (y * w + x) * 4);
            return d;
        }

        static void Put(string root, string rel, byte[] data)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllBytes(p, data);
        }
        static void PutToc(string ao, string folder, string toc) => Put(ao, folder + "\\" + folder + ".toc", Encoding.UTF8.GetBytes(toc));

        public const string RxpToc = "## Interface: 11509, 38002, 50504, 120100, 120005, 120007, 16001\n## Title: RestedXP Guides\n## Notes: In-game leveling guides\n## Author: RestedXP\n"
            + "## Version: v4.11.23-7-g9bc5970\n## Category: Quests\n## OptionalDeps: Questie, RXPGuides_Themes\n## SavedVariables: RXPData, RXPDB, RXPSettings, RXPString\n"
            + "## X-Curse-Project-ID: 486246\n## X-Wago-ID: ZKbebq61\nRXPGuides.lua\n";

        // the RXPGuides-like addon: both logo sizes in Textures, plus decoys that must NOT be picked
        static void BuildRxpLike(string ao)
        {
            PutToc(ao, "RXPGuides", RxpToc);
            Put(ao, "RXPGuides\\RXPGuides.lua", Encoding.UTF8.GetBytes("-- fixture\n"));
            Put(ao, "RXPGuides\\Textures\\rxp_logo-64.blp", MakeBlp(64, 64, RingLogo(64, 232, 178, 58)));
            Put(ao, "RXPGuides\\Textures\\rxp_logo-128.blp", MakeBlp(128, 128, RingLogo(128, 120, 170, 240)));
        }

        async Task<string> SelfUpdatedTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string info = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (cond || info == null ? "" : " (" + info + ")")); ok &= cond; }
            var reqBefore = FakeUpdateServer.Requests;
            try
            {
                // ================= 1. dev-build labels and comparisons
                Check("label: v4.11.23-7-g9bc5970", DevVersion.Label("v4.11.23-7-g9bc5970") == "v4.11.23 · development build (+7)", DevVersion.Label("v4.11.23-7-g9bc5970"));
                Check("label: 1.2.3-12-gabcdef0", DevVersion.Label("1.2.3-12-gabcdef0") == "1.2.3 · development build (+12)", DevVersion.Label("1.2.3-12-gabcdef0"));
                Check("label: -dirty", DevVersion.Label("v2.0-3-g1a2b3c4-dirty") == "v2.0 · development build (+3, modified)", DevVersion.Label("v2.0-3-g1a2b3c4-dirty"));
                Check("label: beta base keeps its spelling", DevVersion.Label("1.0-beta-4-g1234abc") == "1.0-beta · development build (+4)", DevVersion.Label("1.0-beta-4-g1234abc"));
                Check("label: releases and odd versions are not dev builds",
                    new[] { "v4.11.23", "2024.05.01", "r1234", "v1.2.3-beta", "1.2.3-12", "1.2.3-g1234567", "", null, "v1.0-0-gabcdef0" }.All(v => DevVersion.Label(v) == null));
                Check("tooltip text", DevVersion.Tip("v4.11.23-7-g9bc5970").StartsWith("Development build: 7 commits after v4.11.23") && DevVersion.Tip("v1-1-gabcdef0").Contains("1 commit after"));
                Check("VersionText.Pretty uses the label", VersionText.Pretty("v4.11.23-7-g9bc5970", out _) == "v4.11.23 · development build (+7)" && VersionText.Pretty("1.2.3", out _) == "1.2.3");
                int? C(string a, string b) => DevVersion.Compare(a, b);
                Check("compare: base < base+7 < next release", C("4.11.23", "v4.11.23-7-g9bc5970") < 0 && C("v4.11.23-7-g9bc5970", "4.11.24") < 0 && C("4.11.24", "v4.11.23-7-g9bc5970") > 0);
                Check("compare: same base by commits", C("v4.11.23-9-gdeadbee", "v4.11.23-7-g9bc5970") > 0 && C("v4.11.23-7-gaaaaaaa", "v4.11.23-7-g9bc5970") == 0);
                Check("compare: no number -> undecided", C("abc", "1.0") == null);
                bool Newer(string installed, string remote, string installedRemote = null)
                {
                    var e = new AddonEntry { Key = "X", Version = installed, Modified = DateTime.UtcNow };
                    return AddonSources.IsNewer(e, new AddonLink { Key = "X", InstalledRemote = installedRemote }, new RemoteInfo { Version = remote });
                }
                const string dev = "v4.11.23-7-g9bc5970";
                Check("IsNewer: not outdated against its own base release", !Newer(dev, "v4.11.23") && !Newer(dev, "4.11.23"));
                Check("IsNewer: not outdated against an older release", !Newer(dev, "4.11.22"));
                Check("IsNewer: outdated against the next release", Newer(dev, "4.11.24") && Newer(dev, "v4.12.0") && Newer(dev, "5.0"));
                Check("IsNewer: dev builds of the same base compare by commits", Newer(dev, "v4.11.23-9-gdeadbee") && !Newer(dev, "v4.11.23-5-gdeadbee") && !Newer(dev, "v4.11.23-7-gdeadbee"));
                Check("IsNewer: a release is older than a dev build after it", Newer("4.11.23", "v4.11.23-3-gabcdef0") && Newer("1.2.3", "1.2.3-12-gabcdef0") == true);
                Check("IsNewer: 1.2.3-12-gabcdef0 vs 1.2.3 / 1.3.0", !Newer("1.2.3-12-gabcdef0", "1.2.3") && Newer("1.2.3-12-gabcdef0", "1.3.0"));
                Check("IsNewer: installed-by-hub version is compared the same way", !Newer("x", "4.11.23", dev) && Newer("x", "4.11.24", dev) && !Newer("x", dev, dev));
                Check("IsNewer: plain releases unchanged", Newer("1.0", "1.1") && !Newer("1.1", "1.1") && !Newer("1.2", "1.1") && Newer("v1.9.9", "v1.10.0"));

                // ================= 2. fixture: a fake AddOns folder of its own
                var ao = Path.Combine(dir, "selfupd-wow", "_classic_beta_", "Interface", "AddOns");
                if (Directory.Exists(Path.GetDirectoryName(ao))) Directory.Delete(Path.GetDirectoryName(ao), true);
                Directory.CreateDirectory(ao);
                BuildRxpLike(ao);
                // decoys inside RXPGuides: a Lua-like sibling of the logos that must be skipped, over the size cap / depth limit / not a logo name
                Put(ao, "RXPGuides\\Textures\\logo_256.tga", MakeTga(256, 256, RingLogo(256, 200, 90, 90)));
                Put(ao, "RXPGuides\\Textures\\banner_logo.blp", MakeBlp(128, 32, RingLogo(32, 90, 200, 90)));                       // not square: last
                Put(ao, "RXPGuides\\a\\b\\c\\deep_logo.blp", MakeBlp(32, 32, RingLogo(32, 90, 90, 200)));                            // 3 levels down: found
                Put(ao, "RXPGuides\\a\\b\\c\\d\\too_deep_logo-64.blp", MakeBlp(64, 64, RingLogo(64, 1, 2, 3)));                      // 4 levels down: not searched
                Put(ao, "RXPGuides\\Textures\\big_logo-64.blp", MakeBlp(512, 512, RingLogo(512, 1, 2, 3)));                          // over 1 MB: ignored
                Put(ao, "RXPGuides\\Textures\\icons.blp", MakeBlp(64, 64, RingLogo(64, 1, 2, 3)));                                   // a sheet of many icons: ignored
                Put(ao, "RXPGuides\\Textures\\background.blp", MakeBlp(64, 64, RingLogo(64, 1, 2, 3)));                              // neither "logo" nor "icon"
                Put(ao, "RXPGuides\\Textures\\broken_logo.blp", new byte[300]);                                                      // not a picture
                Put(ao, "outsider_logo-64.blp", MakeBlp(64, 64, RingLogo(64, 1, 2, 3)));                                             // next to the addon folders, not inside one
                // IconTexture beats the heuristic
                PutToc(ao, "IconTexAddon", "## Interface: 16001\n## Title: Icon Tex\n## Version: 1.0\n## IconTexture: Interface\\AddOns\\IconTexAddon\\art\\main\n");
                Put(ao, "IconTexAddon\\art\\main.blp", MakeBlp(32, 32, RingLogo(32, 250, 120, 40)));
                Put(ao, "IconTexAddon\\art\\other_logo-64.blp", MakeBlp(64, 64, RingLogo(64, 40, 120, 250)));
                // IconTexture that tries to leave
                PutToc(ao, "EscapeAddon", "## Interface: 16001\n## Title: Escape\n## Version: 1.0\n## IconTexture: Interface\\AddOns\\IconTexAddon\\art\\main.blp\n");
                // no logo at all, Wago id only
                PutToc(ao, "WagoSelf", "## Interface: 16001\n## Title: Wago Self\n## Version: 2.5\n## X-Wago-ID: abcd1234\n");
                // managed by CurseForge (a card with Cf set), has a logo
                PutToc(ao, "CfManaged", "## Interface: 16001\n## Title: Cf Managed\n## Version: 3.1\n## X-Curse-Project-ID: 777\n");
                Put(ao, "CfManaged\\icon.blp", MakeBlp(64, 64, RingLogo(64, 200, 200, 60)));
                // hand-installed, no ids
                PutToc(ao, "HandMade", "## Interface: 16001\n## Title: Hand Made\n## Version: 1.0\n");
                // a GitHub link beats everything
                PutToc(ao, "GhAndCurse", "## Interface: 16001\n## Title: Gh And Curse\n## Version: 1.0\n## X-Curse-Project-ID: 99\n## X-Website: https://github.com/example/ghandcurse\n");
                PutToc(ao, "JunctionAddon", "## Interface: 16001\n## Title: Junction\n## IconTexture: Interface\\AddOns\\JunctionAddon\\Link\\evil_logo-64.blp\n");
                var before = Directory.GetFiles(ao, "*", SearchOption.AllDirectories).Select(f => f + "|" + new FileInfo(f).Length + "|" + File.GetLastWriteTimeUtc(f).Ticks).OrderBy(x => x).ToList();

                var entries = AddonScanner.Scan(ao, 16001, new List<string>(), out _);
                AddonEntry E(string k) => entries.FirstOrDefault(x => x.Key == k);
                var rxp = E("RXPGuides");
                Check("scan: RXPGuides has both ids and the dev version", rxp != null && rxp.CurseId == "486246" && rxp.WagoId == "ZKbebq61" && rxp.Version == dev && rxp.Author == "RestedXP" && rxp.Cf == null);
                Check("scan: IconTexture is read", E("IconTexAddon")?.IconTexture == "Interface\\AddOns\\IconTexAddon\\art\\main");

                // ================= 3. source classification
                ThirdPartyCard Card(string k, CfAddon cf = null) { var en = E(k); en.Cf = cf; return new ThirdPartyCard(en); }
                var cRxp = Card("RXPGuides");
                Check("Self: RXPGuides shows the Self source", cRxp.Source == "Self" && cRxp.SourceGeometry == SourceIcons.Self && cRxp.SourceImage == null && cRxp.SourceGeoVis == Visibility.Visible && cRxp.SourceChipVis == Visibility.Collapsed, cRxp.Source);
                Check("Self: the glyph is not the gold hand-installed one", SourceIcons.Self != SourceIcons.Manual && !ReferenceEquals(cRxp.SourceGlyphBrush, Application.Current.Resources["Gold"]) && ((SolidColorBrush)cRxp.SourceGlyphBrush).Color != ((SolidColorBrush)Application.Current.Resources["Gold"]).Color);
                Check("Self: the glyph has a real shape", SourceIcons.Self.Bounds.Width > 10 && SourceIcons.Self.Bounds.Height > 10 && SourceIcons.Self.IsFrozen);
                Check("Self: tooltip and LinkedTo say it is updated outside", cRxp.LinkedTo == "Not managed by CurseForge - updated outside the Outpost (by hand or by the addon's own updater)" && cRxp.SourceTip == cRxp.LinkedTo);
                Check("Self: Open on CurseForge / Wago links are kept", cRxp.CurseUrl == "https://www.curseforge.com/projects/486246" && cRxp.CurseVisibility == Visibility.Visible && cRxp.WagoUrl == "https://addons.wago.io/addons/ZKbebq61" && cRxp.WagoVisibility == Visibility.Visible);
                Check("Self: not treated as CurseForge-managed", cRxp.Entry.Cf == null && cRxp.CfVisibility == Visibility.Collapsed && cRxp.ButtonText == "" && !cRxp.ButtonEnabled && cRxp.State == TpState.Local
                    && !(cRxp.PillText ?? "").Contains("CurseForge") && cRxp.LinkVisibility == Visibility.Visible, cRxp.State + " / " + cRxp.PillText);
                cRxp.Suggested = new Suggestion { File = new WowiFile { Id = "1", Name = "RXP Guides", Version = "9" }, Hint = "similar name" };
                Check("Self: no WoWInterface suggestion hides the version line", !cRxp.ShowSuggestion && cRxp.SuggestVisibility == Visibility.Collapsed);
                Check("Self: dev label in the row and the tooltip", cRxp.Subtitle == "v4.11.23 · development build (+7)  ·  RestedXP" && cRxp.RowSubtitle == cRxp.Subtitle && cRxp.VersionLine == "Installed v4.11.23 · development build (+7)"
                    && cRxp.SubtitleTip.Contains("7 commits after v4.11.23") && cRxp.SubtitleTip.Contains(dev) && cRxp.RowSubtitleTip == cRxp.SubtitleTip, cRxp.Subtitle + " | " + cRxp.VersionLine + " | " + cRxp.SubtitleTip);
                var cWago = Card("WagoSelf");
                Check("Self: a Wago id alone is Self too (no more dim Wago chip)", cWago.Source == "Self" && cWago.CurseVisibility == Visibility.Collapsed && cWago.WagoVisibility == Visibility.Visible && cWago.SourceChipVis == Visibility.Collapsed);
                var cf = new CfAddon { Id = 777, Name = "Cf Managed", InstalledId = 5, LatestId = 5, Url = "https://www.curseforge.com/wow/addons/cf-managed", Folders = new List<string> { "CfManaged" } };
                var cCf = Card("CfManaged", cf);
                Check("CurseForge-managed (Cf set) stays CurseForge", cCf.Source == "CurseForge" && cCf.Entry.Cf != null && cCf.LinkedTo.StartsWith("Managed by the CurseForge app") && cCf.CurseUrl == null);
                Check("CurseForge-managed: state from the file ids, version string irrelevant", cCf.State == TpState.UpToDate && cCf.CfVisibility == Visibility.Visible && cCf.VersionLine.StartsWith("Up to date (checked"));
                cf.LatestId = 6;
                var cCf2 = Card("CfManaged", cf);
                Check("CurseForge-managed: update goes through CurseForge", cCf2.State == TpState.UpdateAvailable && cCf2.ButtonText == "Update via CurseForge" && cCf2.Source == "CurseForge");
                var cHand = Card("HandMade");
                Check("Local: no ids -> unchanged gold hand glyph", cHand.Source == "Local" && cHand.SourceGeometry == SourceIcons.Manual && cHand.SourceTip.StartsWith("Installed manually") && cHand.CurseUrl == null && cHand.WagoUrl == null);
                var cGh = Card("GhAndCurse");
                Check("GitHub link beats a Curse id", cGh.Source == "GitHub" && cGh.CurseUrl != null);
                Check("Self cards are skipped by the CurseForge auto-check (it only looks at cards with Cf)", cRxp.Entry.Cf == null && cWago.Entry.Cf == null);

                // ================= 4. logo lookup
                var names = new Func<List<string>, string>(l => string.Join(",", l.Select(p => p.Substring(ao.Length + 1).Replace('\\', '/'))));
                var rxpList = AddonLogo.Candidates(ao, new List<string> { "RXPGuides" }, null);
                Check("logo order: 64, then 128, then the largest square, then the rest; decoys left out",
                    names(rxpList) == "RXPGuides/Textures/rxp_logo-64.blp,RXPGuides/Textures/rxp_logo-128.blp,RXPGuides/Textures/logo_256.tga,RXPGuides/a/b/c/deep_logo.blp,RXPGuides/Textures/banner_logo.blp", names(rxpList));
                Check("logo depth: 3 levels found, 4 not", rxpList.Any(p => p.EndsWith("deep_logo.blp")) && !rxpList.Any(p => p.EndsWith("too_deep_logo-64.blp")));
                Check("logo size cap: a file over 1 MB is ignored", !rxpList.Any(p => p.EndsWith("big_logo-64.blp")) && new FileInfo(Path.Combine(ao, "RXPGuides", "Textures", "big_logo-64.blp")).Length > AddonLogo.MaxBytes);
                Check("logo: names without logo / icon, 'icons' sheets and broken files are ignored", !rxpList.Any(p => p.EndsWith("background.blp") || p.EndsWith("icons.blp") || p.EndsWith("broken_logo.blp")));
                Check("logo: a file next to the addon folders is never used", !rxpList.Any(p => p.EndsWith("outsider_logo-64.blp")));
                var tex = AddonLogo.Candidates(ao, new List<string> { "IconTexAddon" }, E("IconTexAddon").IconTexture);
                Check("IconTexture (no extension) beats the heuristic", names(tex) == "IconTexAddon/art/main.blp,IconTexAddon/art/other_logo-64.blp", names(tex));
                Check("IconTexture with .blp / forward slashes works", AddonLogo.FromToc(ao, new List<string> { "IconTexAddon" }, "Interface/AddOns/IconTexAddon/art/main.blp") != null);
                Check("IconTexture of an other addon's folder is refused", AddonLogo.FromToc(ao, new List<string> { "EscapeAddon" }, E("EscapeAddon").IconTexture) == null);
                var bad = new[]
                {
                    "Interface\\AddOns\\EscapeAddon\\..\\IconTexAddon\\art\\main.blp", "..\\AddOns\\IconTexAddon\\art\\main.blp", "Interface\\AddOns\\IconTexAddon\\..\\..\\AddOns\\IconTexAddon\\art\\main",
                    Path.Combine(ao, "IconTexAddon", "art", "main.blp"), "C:\\Windows\\Web\\Wallpaper\\x.blp", "D:\\x.blp", "\\\\server\\share\\x.blp", "\\IconTexAddon\\art\\main.blp",
                    "http://example.com/IconTexAddon/logo.blp", "https://example.com/logo.tga", "134400", "", "Interface\\AddOns\\IconTexAddon\\art\\main.blp:stream", "Interface\\AddOns\\IconTexAddon\\*",
                };
                Check("IconTexture: .., absolute, drive, UNC, URL, file id and empty paths are rejected", bad.All(b => AddonLogo.FromToc(ao, new List<string> { "IconTexAddon", "EscapeAddon" }, b) == null), string.Join(" | ", bad.Where(b => AddonLogo.FromToc(ao, new List<string> { "IconTexAddon", "EscapeAddon" }, b) != null)));
                Check("IconTexture over 1 MB is refused", AddonLogo.FromToc(ao, new List<string> { "RXPGuides" }, "Interface\\AddOns\\RXPGuides\\Textures\\big_logo-64.blp") == null);
                Check("IconTexture of a missing file falls back to the heuristic", AddonLogo.Candidates(ao, new List<string> { "RXPGuides" }, "Interface\\AddOns\\RXPGuides\\Textures\\nope").Count == rxpList.Count);
                // a junction inside an addon folder that points out of AddOns is not followed
                var outside = Path.Combine(dir, "selfupd-outside"); Directory.CreateDirectory(outside);
                File.WriteAllBytes(Path.Combine(outside, "evil_logo-64.blp"), MakeBlp(64, 64, RingLogo(64, 1, 2, 3)));
                var link = Path.Combine(ao, "JunctionAddon", "Link");
                bool made = false;
                try
                {
                    var psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + outside + "\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    using (var pr = Process.Start(psi)) { pr.StandardOutput.ReadToEnd(); pr.StandardError.ReadToEnd(); pr.WaitForExit(10000); }
                    made = Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
                }
                catch { }
                if (made)
                {
                    Check("a junction pointing out of AddOns is not followed (heuristic and IconTexture)",
                        AddonLogo.Candidates(ao, new List<string> { "JunctionAddon" }, "Interface\\AddOns\\JunctionAddon\\Link\\evil_logo-64.blp").Count == 0 && File.Exists(Path.Combine(link, "evil_logo-64.blp")));
                    Directory.Delete(link, false);   // the link only; the target keeps its file
                    Check("junction removed without touching its target", File.Exists(Path.Combine(outside, "evil_logo-64.blp")));
                }
                else notes.Add("ok    junction test skipped (mklink not available)");

                // ================= 5. decoding + cache
                Check("cache folder is inside the data folder", AddonLogo.Dir.StartsWith(Util.DataDir, StringComparison.OrdinalIgnoreCase) && AddonLogo.Dir.EndsWith("cache\\addon-logos"), AddonLogo.Dir);
                if (Directory.Exists(AddonLogo.Dir)) foreach (var f in Directory.GetFiles(AddonLogo.Dir)) File.Delete(f);
                var src64 = Path.Combine(ao, "RXPGuides", "Textures", "rxp_logo-64.blp");
                int d0 = AddonLogo.Decodes;
                var png1 = AddonLogo.EnsurePng(src64);
                Check("decode: a PNG appears in the cache", png1 != null && File.Exists(png1) && Path.GetDirectoryName(png1) == AddonLogo.Dir && AddonLogo.Decodes == d0 + 1);
                var bi = new BitmapImage(new Uri(png1));
                Check("decode: the PNG is the 64 x 64 picture with its transparent corner", bi.PixelWidth == 64 && bi.PixelHeight == 64 && PixelAt(bi, 0, 0).A == 0 && PixelAt(bi, 32, 32).A == 255 && PixelAt(bi, 32, 32).R == 232);
                var png2 = AddonLogo.EnsurePng(src64);
                Check("cache: the second request decodes nothing", png2 == png1 && AddonLogo.Decodes == d0 + 1);
                File.WriteAllBytes(src64, MakeBlp(64, 64, RingLogo(64, 20, 220, 120)));
                File.SetLastWriteTimeUtc(src64, DateTime.UtcNow.AddMinutes(5));
                var png3 = AddonLogo.EnsurePng(src64);
                Check("cache: a changed file (mtime + size) is decoded again under a new key", png3 != null && png3 != png1 && AddonLogo.Decodes == d0 + 2 && PixelAt(new BitmapImage(new Uri(png3)), 32, 32).G == 220);
                Check("cache: the key is path + mtime + size", AddonLogo.CacheName(src64) != AddonLogo.CacheName(Path.Combine(ao, "RXPGuides", "Textures", "rxp_logo-128.blp")) && AddonLogo.CacheName(src64) == AddonLogo.CacheName(src64));
                Check("a file that is not a picture decodes to nothing (and is not retried)", AddonLogo.EnsurePng(Path.Combine(ao, "RXPGuides", "Textures", "broken_logo.blp")) == null && AddonLogo.EnsurePng(Path.Combine(ao, "RXPGuides", "Textures", "broken_logo.blp")) == null);
                // TGA: bottom-origin and RLE, orientation + colours
                var tga = AddonLogo.Tga(MakeTga(4, 2, (x, y) => y == 0 ? Bgra(255, 0, 0) : Bgra(0, 0, 255)), out int tw, out int th);
                Check("TGA: uncompressed, bottom-origin rows come out top first", tw == 4 && th == 2 && tga[0] == 0 && tga[2] == 255 && tga[4 * 4 * 1] == 255 && tga[4 * 4 * 1 + 2] == 0 && tga[3] == 255);
                var rle = new byte[18 + 2 + 4 + 2 + 8]; rle[2] = 10; rle[12] = 4; rle[14] = 1; rle[16] = 32; rle[17] = 0x28;
                rle[18] = 0x81; rle[19] = 9; rle[20] = 8; rle[21] = 7; rle[22] = 255;               // a run of 2 pixels
                rle[23] = 0x01; for (int i = 0; i < 8; i++) rle[24 + i] = (byte)(i + 1);            // 2 raw pixels
                var rpx = AddonLogo.Tga(rle, out _, out _);
                Check("TGA: RLE runs and raw packets", rpx[0] == 9 && rpx[4] == 9 && rpx[8] == 1 && rpx[12] == 5);
                Check("TGA: truncated / unsupported files are refused", Throws(() => AddonLogo.Tga(new byte[18], out _, out _)) && Throws(() => AddonLogo.Tga(new byte[] { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 4, 0, 32, 8, 1, 2, 3 }, out _, out _)));
                var tgaPng = AddonLogo.EnsurePng(Path.Combine(ao, "RXPGuides", "Textures", "logo_256.tga"));
                Check("TGA: a 256 x 256 file becomes a PNG", tgaPng != null && new BitmapImage(new Uri(tgaPng)).PixelWidth == 256);

                // ================= 6. the rows: logo arrives asynchronously, the rest keeps the letter
                var ui = Environment.CurrentManagedThreadId;
                var cardsNow = entries.Where(x => x.Key == "RXPGuides" || x.Key == "WagoSelf" || x.Key == "IconTexAddon" || x.Key == "CfManaged").ToList();
                var rows = cardsNow.Select(en => new ThirdPartyCard(en)).ToList();
                foreach (var r in rows) r.LoadLogo(ao);
                Check("rows: nothing is decoded on the calling (UI) thread", rows.All(r => r.Logo == null) && rows[0].LogoVis == Visibility.Collapsed && rows[0].LetterVis == Visibility.Visible);
                for (int i = 0; i < 40 && rows.Any(r => r.Entry.Key != "WagoSelf" && r.Logo == null); i++) await Task.Delay(100);
                ThirdPartyCard Row(string k) => rows.First(r => r.Entry.Key == k);
                Check("rows: RXPGuides shows its logo (letter hidden)", Row("RXPGuides").Logo != null && Row("RXPGuides").LogoVis == Visibility.Visible && Row("RXPGuides").LetterVis == Visibility.Collapsed);
                Check("rows: IconTexture addon uses its IconTexture picture", Row("IconTexAddon").Logo is BitmapSource b1 && b1.PixelWidth == 32);
                Check("rows: a CurseForge-managed addon gets its logo too", Row("CfManaged").Logo != null);
                Check("rows: no logo -> the letter, as before", Row("WagoSelf").Logo == null && Row("WagoSelf").LetterVis == Visibility.Visible && Row("WagoSelf").Letter == "W");
                Check("rows: the decoder ran off the UI thread", AddonLogo.LastDecodeThread != 0 && AddonLogo.LastDecodeThread != ui, AddonLogo.LastDecodeThread + " vs " + ui);
                var dn = AddonLogo.Decodes;
                foreach (var r in rows) r.LoadLogo(ao);
                await Task.Delay(300);
                Check("rows: asking again for the same addon neither decodes nor reloads", AddonLogo.Decodes == dn && Row("RXPGuides").Logo != null);

                // ================= 7. nothing written to the addons folder, nothing from the network
                var after = Directory.GetFiles(ao, "*", SearchOption.AllDirectories).Select(f => f + "|" + new FileInfo(f).Length + "|" + File.GetLastWriteTimeUtc(f).Ticks).OrderBy(x => x).ToList();
                var changedByTest = before.Except(after).Select(x => x.Split('|')[0]).Where(f => !f.EndsWith("rxp_logo-64.blp")).ToList();   // the one file the cache test rewrote on purpose
                Check("the AddOns folder is only read", !after.Except(before).Any(x => !x.StartsWith(Path.Combine(ao, "RXPGuides", "Textures", "rxp_logo-64.blp"))) && changedByTest.Count == 0 && !Directory.GetFiles(ao, "*.png", SearchOption.AllDirectories).Any());
                Check("no network: the fake update server saw nothing and nothing went through Net", FakeUpdateServer.Requests == reqBefore);
                Check("no network: AddonLogo has no network members", !typeof(AddonLogo).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Any(m =>
                    m.GetParameters().Any(p => (p.ParameterType.Namespace ?? "").StartsWith("System.Net")) || (m.ReturnType.Namespace ?? "").StartsWith("System.Net")));
            }
            catch (Exception e) { Check("section crashed: " + e, false); }
            return "selfupdated: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        static Color PixelAt(BitmapSource bi, int x, int y)
        {
            var conv = new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
            var px = new byte[4];
            conv.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
            return Color.FromArgb(px[3], px[2], px[1], px[0]);
        }

        static bool Throws(Action a) { try { a(); return false; } catch { return true; } }
    }
}
