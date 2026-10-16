using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows.Media;

namespace ElansAddonHub.Services
{
    // Where the 36 px tile of an "Other addons" row gets its picture. Discovery in this order, the first that gives a picture wins:
    //   1. the addon's own toc "## IconTexture:" when it points inside one of its folders (.blp / .tga / .png / .jpg, AddonLogo.FromToc)
    //   2. the toc IconTexture when it names a GAME file instead ("Interface\Icons\Trade_Mining" or a file id) - read from the player's own
    //      game data (local CASC, by name hash or file id), like the item icons
    //   3. a logo / icon file found in the addon's folders (AddonLogo.Heuristic)
    //   4. CurseForge's project logo (thumbnailUrl in CurseForge's local AddonGameInstance.json; https *.forgecdn.net only)
    //   5. the GitHub owner's avatar for a GitHub-linked addon (https github.com / avatars.githubusercontent.com only)
    //   6. nothing: the letter tile
    // Every result is a PNG in cache\addon-logos (remote ones keyed by URL, game ones by path), so a picture is fetched / decoded once; the
    // answer per addon is also remembered for the session. WoWInterface and Wago have no keyless per-addon logo, so they are not asked.
    // Why "Better Name Plates" and "GatherMemory" had a letter (2.70.1): BetterNamePlates' IconTexture is "...\icon.png" and only .blp / .tga
    // were read; GatherMemory's IconTexture is the game icon "Interface\Icons\Trade_Mining", which is outside its folder and was refused.
    // Both also have a CurseForge thumbnailUrl that was never used.
    public class IconSources
    {
        public string AddOnsDir, IconTexture, CfThumb, GithubRepo;
        public IList<string> Folders = new List<string>();
        public DateTime Modified;
    }

    public enum IconKind { None, Toc, Game, Folder, CurseForge, GitHub }

    public static class AddonIcon
    {
        public const int MaxRemoteBytes = 1024 * 1024;

        // seams (the self-test never goes online and has no game data): url -> bytes, game path / file id -> PNG bytes; null = not available
        public static Func<string, byte[]> FetchRemote = DefaultFetch;
        public static Func<string, string, byte[]> GamePng = DefaultGamePng;    // (addOnsDir, IconTexture)
        public static int RemoteFetches;                                         // how many remote downloads really happened (cache check)

        static readonly ConcurrentDictionary<string, Tuple<string, IconKind>> found = new ConcurrentDictionary<string, Tuple<string, IconKind>>();
        public static void ForgetSession() => found.Clear();

        public static ImageSource Load(IconSources s, out IconKind kind)
        {
            var png = Resolve(s, out kind);
            return png == null ? null : IconStore.Load(png);
        }

        public static string Resolve(IconSources s, out IconKind kind)
        {
            kind = IconKind.None;
            if (s == null) return null;
            var key = (s.AddOnsDir ?? "") + "|" + string.Join("+", s.Folders ?? new List<string>()) + "|" + s.Modified.Ticks + "|" + s.IconTexture + "|" + s.CfThumb + "|" + s.GithubRepo;
            if (found.TryGetValue(key, out var hit) && (hit.Item1 == null || File.Exists(hit.Item1))) { kind = hit.Item2; return hit.Item1; }
            string png = null;
            try
            {
                var folders = s.Folders ?? new List<string>();
                // 1. toc, inside the addon
                var toc = string.IsNullOrEmpty(s.AddOnsDir) ? null : AddonLogo.FromToc(s.AddOnsDir, folders, s.IconTexture);
                if (toc != null && (png = AddonLogo.EnsurePng(toc)) != null) kind = IconKind.Toc;
                // 2. toc, a game file
                if (png == null && IsGamePath(s.IconTexture) && (png = GameIcon(s.AddOnsDir, s.IconTexture)) != null) kind = IconKind.Game;
                // 3. a logo file in its folders
                if (png == null && !string.IsNullOrEmpty(s.AddOnsDir))
                {
                    int tried = 0;
                    foreach (var c in AddonLogo.Candidates(s.AddOnsDir, folders, null))
                    {
                        if (++tried > 4) break;
                        if ((png = AddonLogo.EnsurePng(c)) != null) { kind = IconKind.Folder; break; }
                    }
                }
                // 4. CurseForge's project logo
                if (png == null && CfThumbProblem(s.CfThumb) == null && (png = RemotePng(s.CfThumb, CfThumbProblem)) != null) kind = IconKind.CurseForge;
                // 5. GitHub owner avatar
                if (png == null && GithubAvatarUrl(s.GithubRepo) is string gh && (png = RemotePng(gh, GithubProblem)) != null) kind = IconKind.GitHub;
            }
            catch (Exception e) { Util.Log("addon icon: " + e.Message); png = null; kind = IconKind.None; }
            found[key] = Tuple.Create(png, kind);
            return png;
        }

        // "Interface\Icons\X" (any game path that is not in AddOns) or a plain file id
        public static bool IsGamePath(string t)
        {
            t = (t ?? "").Trim().Trim('"').Replace('/', '\\');
            if (t.Length == 0 || t.Length > 240) return false;
            if (t.All(char.IsDigit)) return t.Length <= 9 && int.TryParse(t, out var id) && id > 0;
            if (t.IndexOf(':') >= 0 || t.StartsWith("\\") || t.Split('\\').Any(p => p == ".." || p == "." || p.Length == 0)) return false;
            if (t.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || t.IndexOfAny(new[] { '*', '?' }) >= 0) return false;
            return t.StartsWith("Interface\\", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("Interface\\AddOns\\", StringComparison.OrdinalIgnoreCase);
        }

        static string GameIcon(string addOnsDir, string iconTexture)
        {
            var t = iconTexture.Trim().Trim('"').Replace('/', '\\');
            var png = Path.Combine(AddonLogo.Dir, "game-" + Util.Sha256Text(t.ToLowerInvariant()).Substring(0, 24) + ".png");
            if (File.Exists(png) && new FileInfo(png).Length > 0) return png;
            byte[] data;
            try { data = GamePng(addOnsDir, t); } catch (Exception e) { Util.Log("addon icon (game " + t + "): " + e.Message); return null; }
            if (data == null || data.Length == 0) return null;
            return Save(png, data) ? png : null;
        }

        // the player's own game data, read-only: AddOns = <root>\<flavor>\Interface\AddOns
        static byte[] DefaultGamePng(string addOnsDir, string t)
        {
            if (string.IsNullOrEmpty(addOnsDir)) return null;
            var flavorDir = Path.GetFullPath(Path.Combine(addOnsDir, "..", ".."));
            var root = Path.GetDirectoryName(flavorDir.TrimEnd('\\'));
            var flavor = Path.GetFileName(flavorDir.TrimEnd('\\'));
            if (string.IsNullOrEmpty(root) || !Directory.Exists(Path.Combine(root, "Data"))) return null;
            if (t.All(char.IsDigit))
            {
                var id = int.Parse(t);
                return IconStore.Extract(new HashSet<int> { id }, root, flavor).TryGetValue(id, out var p) ? p : null;
            }
            var names = Path.HasExtension(t) ? new[] { t } : new[] { t + ".blp", t + ".tga" };
            using (var lease = CascShared.Open(root, flavor))
            {
                var want = new HashSet<ulong>(names.Select(CascStorage.NameHash));
                var keys = lease.Casc.ResolveNameHashes(want);
                foreach (var n in names)
                {
                    if (!keys.TryGetValue(CascStorage.NameHash(n), out var ck)) continue;
                    var bytes = lease.Casc.ReadByContentKey(ck);
                    if (bytes == null) continue;
                    if (n.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) return IconStore.ToPng(bytes);
                    var px = AddonLogo.Tga(bytes, out int w, out int h);
                    return PngOf(px, w, h);
                }
            }
            return null;
        }

        static byte[] PngOf(byte[] bgra, int w, int h)
        {
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            bmp.Freeze();
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }

        // ---- remote pictures: only the hosts named here, https, at most 1 MB, must decode as a picture; cached by URL
        public static string CfThumbProblem(string url) => HostProblem(url, h => h == "forgecdn.net" || h.EndsWith(".forgecdn.net", StringComparison.Ordinal));
        public static string GithubProblem(string url) => HostProblem(url, h => h == "github.com" || h == "avatars.githubusercontent.com");

        static string HostProblem(string url, Func<string, bool> hostOk)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return "no address";
            if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || !string.IsNullOrEmpty(u.UserInfo)) return "not plain https";
            return hostOk(u.Host.ToLowerInvariant().TrimEnd('.')) ? null : "host " + u.Host + " not allowed";
        }

        public static string GithubAvatarUrl(string repo)
        {
            var owner = (repo ?? "").Split('/')[0].Trim();
            if (owner.Length == 0 || owner.Length > 39 || !owner.All(c => char.IsLetterOrDigit(c) && c < 128 || c == '-')) return null;
            return "https://github.com/" + owner + ".png?size=128";
        }

        static readonly ConcurrentDictionary<string, bool> remoteFailed = new ConcurrentDictionary<string, bool>();

        static string RemotePng(string url, Func<string, string> problem)
        {
            if (problem(url) != null) return null;
            var png = Path.Combine(AddonLogo.Dir, "remote-" + Util.Sha256Text(url.Trim()).Substring(0, 24) + ".png");
            if (File.Exists(png) && new FileInfo(png).Length > 0) return png;
            if (remoteFailed.ContainsKey(url)) return null;
            byte[] data = null;
            try { data = FetchRemote(url.Trim()); if (data != null) Interlocked.Increment(ref RemoteFetches); }
            catch (Exception e) { Util.Log("addon icon (remote): " + e.Message); }
            if (data == null || data.Length == 0 || data.Length > MaxRemoteBytes) { remoteFailed[url] = true; return null; }
            try
            {
                var px = AddonLogo.Wic(data, out int w, out int h);        // must really be a picture; stored as our own PNG
                return Save(png, PngOf(px, w, h)) ? png : null;
            }
            catch (Exception e) { Util.Log("addon icon (remote picture): " + e.Message); remoteFailed[url] = true; return null; }
        }

        static bool Save(string png, byte[] data)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(png));
                var tmp = png + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, data);
                try { File.Move(tmp, png); } catch { try { File.Delete(tmp); } catch { } if (!File.Exists(png)) throw; }
                return true;
            }
            catch (Exception e) { Util.Log("addon icon cache: " + e.Message); return false; }
        }

        // the network (never in the self-test: the fake update server is running there); redirects must stay on an allowed host
        static byte[] DefaultFetch(string url)
        {
            if (FakeUpdateServer.Active) return null;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (var resp = Net.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode) return null;
                var final = resp.RequestMessage?.RequestUri?.ToString();
                if (final != null && CfThumbProblem(final) != null && GithubProblem(final) != null) return null;
                if (resp.Content.Headers.ContentLength > MaxRemoteBytes) return null;
                using (var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[32768]; int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > MaxRemoteBytes) return null; }
                    return ms.ToArray();
                }
            }
        }
    }
}
