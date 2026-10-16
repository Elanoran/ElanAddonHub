using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub.Services
{
    // An addon's own logo for its row in "Other addons": a .blp / .tga / .png / .jpg that ships inside the addon's folder, decoded once to a PNG in
    // %LOCALAPPDATA%\ElansAddonHub\cache\addon-logos. Local files only: no network, nothing outside Interface\AddOns, nothing written to the WoW folder.
    //   1. the toc's "## IconTexture:" path (relative to Interface\) when it points inside one of the addon's folders;
    //   2. else a small search in those folders (max 3 levels deep) for files named *logo* / *icon*, preferring 64 x 64, then 128 x 128, then the
    //      largest square one; files over 1 MB are ignored.
    public static class AddonLogo
    {
        public const long MaxBytes = 1024 * 1024;
        public const int MaxDepth = 3;       // folders below the addon's own folder that are searched
        public const int MaxSide = 512;      // a logo, not an atlas
        public static int LastDecodeThread;   // managed thread id of the last real decode (the self-test checks it is not the UI thread)
        public static int Decodes;           // how many files were really decoded (the self-test checks the cache with it)

        public static string Dir => Path.Combine(Util.DataDir, "cache", "addon-logos");

        static bool IsPicture(string path) { var x = Path.GetExtension(path).ToLowerInvariant(); return x == ".blp" || x == ".tga" || IsWic(path); }
        // .png / .jpg: newer clients load them too (e.g. BetterNamePlates' "IconTexture: ...icon.png"); decoded by WPF, not by our own readers
        static bool IsWic(string path) { var x = Path.GetExtension(path).ToLowerInvariant(); return x == ".png" || x == ".jpg" || x == ".jpeg"; }

        // a folder, or anything on the way from the addon's folder to a file, that is a junction / symlink could lead out of AddOns
        static bool Plain(string root, string path)
        {
            try
            {
                var rootTrim = root.TrimEnd('\\');
                var p = path;
                while (!string.IsNullOrEmpty(p) && p.Length >= rootTrim.Length)
                {
                    if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return false;
                    if (string.Equals(p.TrimEnd('\\'), rootTrim, StringComparison.OrdinalIgnoreCase)) return true;
                    p = Path.GetDirectoryName(p);
                }
            }
            catch { }
            return false;
        }

        static string FolderRoot(string addOnsDir, string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || folder.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || folder == "." || folder == "..") return null;
            return Path.GetFullPath(Path.Combine(addOnsDir, folder));
        }

        // the file a toc IconTexture value means, if it is a safe path inside one of the addon's folders (else null)
        public static string FromToc(string addOnsDir, IList<string> folders, string iconTexture)
        {
            try
            {
                var t = (iconTexture ?? "").Trim().Trim('"').Replace('/', '\\');
                if (t.Length == 0 || t.Length > 240 || t.All(char.IsDigit)) return null;                 // empty, or a game file id
                if (Path.IsPathRooted(t) || t.IndexOf(':') >= 0 || t.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || t.IndexOfAny(new[] { '*', '?' }) >= 0) return null;
                if (t.Split('\\').Any(s => s == ".." || s == ".")) return null;
                if (t.StartsWith("Interface\\", StringComparison.OrdinalIgnoreCase)) t = t.Substring("Interface\\".Length);   // the toc writes it from the game root
                var interfaceDir =Path.GetDirectoryName(Path.GetFullPath(addOnsDir).TrimEnd('\\'));
                var full = Path.GetFullPath(Path.Combine(interfaceDir, t));
                foreach (var f in folders ?? new List<string>())
                {
                    var root = FolderRoot(addOnsDir, f);
                    if (root == null || !full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                    var tries = IsPicture(full) ? new[] { full } : new[] { full + ".blp", full + ".tga", full + ".png", full + ".jpg" };
                    foreach (var c in tries)
                        if (File.Exists(c) && new FileInfo(c).Length <= MaxBytes && Plain(root, c)) return c;
                }
            }
            catch (Exception e) { Util.Log("addon logo (toc): " + e.Message); }
            return null;
        }

        class Cand { public string Path; public int Depth, W, H; public bool Logo; }

        // width / height from the header only; false when the file is not a picture we can decode
        public static bool Dims(string path, out int w, out int h)
        {
            w = h = 0;
            try
            {
                if (IsWic(path))
                {
                    using (var fs = File.OpenRead(path))
                    {
                        var fr = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                        w = fr.PixelWidth; h = fr.PixelHeight;
                    }
                    return w > 0 && h > 0 && w <= MaxSide && h <= MaxSide;
                }
                var buf = new byte[20];
                int n;
                using (var fs = File.OpenRead(path)) n = fs.Read(buf, 0, 20);
                if (n < 18) return false;
                if (Path.GetExtension(path).Equals(".blp", StringComparison.OrdinalIgnoreCase))
                {
                    if (n < 20 || buf[0] != 'B' || buf[1] != 'L' || buf[2] != 'P' || buf[3] != '2' || BitConverter.ToUInt32(buf, 4) != 1) return false;
                    w = (int)BitConverter.ToUInt32(buf, 12); h = (int)BitConverter.ToUInt32(buf, 16);
                }
                else
                {
                    if (buf[1] != 0 || (buf[2] != 2 && buf[2] != 10) || (buf[16] != 24 && buf[16] != 32)) return false;
                    w = BitConverter.ToUInt16(buf, 12); h = BitConverter.ToUInt16(buf, 14);
                }
                return w > 0 && h > 0 && w <= MaxSide && h <= MaxSide;
            }
            catch { return false; }
        }

        static void Walk(string dir, int depth, List<Cand> into)
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(dir).ToList(); } catch { return; }
            foreach (var f in files)
            {
                if (!IsPicture(f)) continue;
                var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                bool logo = name.Contains("logo"), icon = name.Contains("icon") && !name.Contains("icons");   // "icons" is a sheet of many
                if (!logo && !icon) continue;
                try { var fi = new FileInfo(f); if (fi.Length > MaxBytes || (fi.Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                if (!Dims(f, out var w, out var h)) continue;
                into.Add(new Cand { Path = f, Depth = depth, W = w, H = h, Logo = logo });
            }
            if (depth >= MaxDepth) return;
            List<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).ToList(); } catch { return; }
            foreach (var d in subs)
            {
                try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                Walk(d, depth + 1, into);
            }
        }

        static int Tier(Cand c) => c.W != c.H ? 3 : c.W == 64 ? 0 : c.W == 128 ? 1 : 2;

        // heuristic matches in one addon folder, best first
        public static List<string> Heuristic(string addOnsDir, string folder)
        {
            var none = new List<string>();
            var root = FolderRoot(addOnsDir, folder);
            if (root == null || !Directory.Exists(root)) return none;
            try { if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return none; } catch { return none; }
            var list = new List<Cand>();
            Walk(root, 0, list);
            return list.OrderBy(Tier).ThenByDescending(c => c.W * c.H).ThenByDescending(c => c.Logo).ThenBy(c => c.Depth)
                .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Select(c => c.Path).ToList();
        }

        // every source worth trying, best first: the toc's IconTexture, then the heuristic matches (primary folder first)
        public static List<string> Candidates(string addOnsDir, IList<string> folders, string iconTexture)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(addOnsDir) || folders == null) return res;
            var t = FromToc(addOnsDir, folders, iconTexture);
            if (t != null) res.Add(t);
            foreach (var f in folders)
                foreach (var h in Heuristic(addOnsDir, f))
                    if (!res.Contains(h, StringComparer.OrdinalIgnoreCase)) res.Add(h);
            return res;
        }

        // ---- decoding + cache
        static readonly HashSet<string> bad = new HashSet<string>();
        static readonly ConcurrentDictionary<string, string> found = new ConcurrentDictionary<string, string>();

        // keyed by path + modified time + size, so an updated logo is decoded again
        public static string CacheName(string src)
        {
            var fi = new FileInfo(src);
            return Util.Sha256Text(src.ToLowerInvariant() + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length).Substring(0, 24) + ".png";
        }

        // the cached PNG for a source file (decoded now if it is not cached yet); null when it cannot be decoded. Blocking: call off the UI thread.
        public static string EnsurePng(string src)
        {
            string png;
            try
            {
                png = Path.Combine(Dir, CacheName(src));
                if (File.Exists(png) && new FileInfo(png).Length > 0) return png;
                lock (bad) if (bad.Contains(png)) return null;
            }
            catch { return null; }
            try
            {
                LastDecodeThread = Environment.CurrentManagedThreadId;
                var data = File.ReadAllBytes(src);
                int w, h; byte[] bgra;
                if (Path.GetExtension(src).Equals(".blp", StringComparison.OrdinalIgnoreCase)) bgra = Blp.Decode(data, out w, out h);
                else if (IsWic(src)) bgra = Wic(data, out w, out h);
                else bgra = Tga(data, out w, out h);
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
                bmp.Freeze();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                Directory.CreateDirectory(Dir);
                var tmp = png + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var fs = File.Create(tmp)) enc.Save(fs);
                try { File.Move(tmp, png); }
                catch { try { File.Delete(tmp); } catch { } if (!File.Exists(png)) throw; }
                Interlocked.Increment(ref Decodes);
                return png;
            }
            catch (Exception e)
            {
                Util.Log("addon logo: cannot decode " + Path.GetFileName(src) + ": " + e.Message);
                lock (bad) bad.Add(png);
                return null;
            }
        }

        // The PNG for an addon (null: keep the letter tile). Blocking and disk-bound, so call it from a background task.
        // Remembered per session, keyed by the addon's toc time, so the folder is not walked again on every rescan.
        public static string Resolve(string addOnsDir, IList<string> folders, string iconTexture, DateTime modified)
        {
            var key = (addOnsDir ?? "") + "|" + string.Join("+", folders ?? new List<string>()) + "|" + modified.Ticks + "|" + iconTexture;
            if (found.TryGetValue(key, out var hit) && (hit.Length == 0 || File.Exists(hit))) return hit.Length == 0 ? null : hit;
            string png = null;
            int tried = 0;
            foreach (var c in Candidates(addOnsDir, folders, iconTexture))
            {
                if (++tried > 4) break;
                png = EnsurePng(c);
                if (png != null) break;
            }
            found[key] = png ?? "";
            return png;
        }

        public static ImageSource Load(string addOnsDir, IList<string> folders, string iconTexture, DateTime modified)
        {
            var png = Resolve(addOnsDir, folders, iconTexture, modified);
            return png == null ? null : IconStore.Load(png);
        }

        // ---- PNG / JPEG (and anything else WPF reads): the first frame as BGRA, at most 1024 px a side
        public static byte[] Wic(byte[] d, out int w, out int h)
        {
            using (var ms = new MemoryStream(d))
            {
                var fr = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                w = fr.PixelWidth; h = fr.PixelHeight;
                if (w <= 0 || h <= 0 || w > 1024 || h > 1024) throw new InvalidDataException("odd picture size " + w + " x " + h);
                var conv = new FormatConvertedBitmap(fr, PixelFormats.Bgra32, null, 0);
                var px = new byte[w * h * 4];
                conv.CopyPixels(px, w * 4, 0);
                return px;
            }
        }

        // ---- TGA: uncompressed or RLE, 24 / 32 bit true colour
        public static byte[] Tga(byte[] d, out int w, out int h)
        {
            if (d == null || d.Length < 18) throw new InvalidDataException("not a TGA file");
            int idLen = d[0], type = d[2], bpp = d[16], desc = d[17];
            w = BitConverter.ToUInt16(d, 12); h = BitConverter.ToUInt16(d, 14);
            if (d[1] != 0 || (type != 2 && type != 10) || (bpp != 24 && bpp != 32)) throw new NotSupportedException("TGA type " + type + " / " + bpp + " bit");
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) throw new InvalidDataException("odd TGA size");
            int bytes = bpp / 8, p = 18 + idLen, n = w * h;
            var px = new byte[n * 4];
            void Put(int i, int s)
            {
                px[i * 4] = d[s]; px[i * 4 + 1] = d[s + 1]; px[i * 4 + 2] = d[s + 2];
                px[i * 4 + 3] = bytes == 4 && (desc & 0x0F) != 0 ? d[s + 3] : (byte)255;
            }
            if (type == 2)
            {
                if (p + n * bytes > d.Length) throw new InvalidDataException("TGA is truncated");
                for (int i = 0; i < n; i++) Put(i, p + i * bytes);
            }
            else
            {
                int i = 0;
                while (i < n)
                {
                    if (p >= d.Length) throw new InvalidDataException("TGA is truncated");
                    int hd = d[p++], cnt = (hd & 0x7F) + 1;
                    if (i + cnt > n) throw new InvalidDataException("TGA run is too long");
                    if ((hd & 0x80) != 0)
                    {
                        if (p + bytes > d.Length) throw new InvalidDataException("TGA is truncated");
                        for (int k = 0; k < cnt; k++) Put(i++, p);
                        p += bytes;
                    }
                    else
                    {
                        if (p + cnt * bytes > d.Length) throw new InvalidDataException("TGA is truncated");
                        for (int k = 0; k < cnt; k++) { Put(i++, p); p += bytes; }
                    }
                }
            }
            if ((desc & 0x20) == 0)   // stored bottom row first
            {
                var row = new byte[w * 4];
                for (int y = 0; y < h / 2; y++)
                {
                    Buffer.BlockCopy(px, y * w * 4, row, 0, w * 4);
                    Buffer.BlockCopy(px, (h - 1 - y) * w * 4, px, y * w * 4, w * 4);
                    Buffer.BlockCopy(row, 0, px, (h - 1 - y) * w * 4, w * 4);
                }
            }
            return px;
        }
    }
}
