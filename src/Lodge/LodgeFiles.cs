using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // Shared files (Lodge server 2.14, feature "files"): the rules that need no network. The server checks everything again; checking here first
    // just spares an upload that would be refused. Mirrors server/Lodge.Server/FilesModule.cs (FileRules).
    public static class LodgeFiles
    {
        public static readonly Version Server = new Version(2, 14, 0);

        public static readonly HashSet<string> Blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "exe", "dll", "com", "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "msi", "msp", "scr", "cpl",
            "jar", "lnk", "reg", "hta", "sys", "drv", "pif", "application", "appref-ms",
        };

        public const int MaxNameChars = 60, MaxNoteChars = 200, KeptVersions = 3;

        // the last extension, trailing dots and spaces ignored ("setup.exe. " is setup.exe on Windows)
        public static string Extension(string name)
        {
            name = (name ?? "").Trim(' ', '.', '\t');
            var dot = name.LastIndexOf('.');
            var slash = name.LastIndexOfAny(new[] { '/', '\\' });
            return dot > slash && dot >= 0 ? name.Substring(dot + 1).Trim().ToLowerInvariant() : "";
        }

        public static bool IsBlockedName(string name) => Blocked.Contains(Extension(name));

        // ---- opening a file the lodge sent: by its REAL extension, never by the MIME type the server claims (a hostile server can call a program "image/png").
        // Only pictures, text and PDF open from the chat; a zip, a program, a script, a shortcut or anything unknown is never handed to the shell.
        public static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "webp", "bmp" };
        public static readonly HashSet<string> OpenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "webp", "bmp", "txt", "pdf" };

        public static bool IsImageName(string name) => ImageExtensions.Contains(Extension(name));
        public static bool CanOpenDirectly(string pathOrName) => !string.IsNullOrEmpty(pathOrName) && !IsBlockedName(pathOrName) && OpenExtensions.Contains(Extension(pathOrName));

        // true = handed to the shell. False when the extension is not on the list (nothing is started) or Windows could not open it.
        public static bool TryOpen(string path)
        {
            if (!CanOpenDirectly(path)) { Util.Log("files: not opening a file with extension ." + Extension(path) + " from the chat"); return false; }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); return true; }
            catch (Exception) { return false; }
        }

        public static bool UnsafeEntryPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0 || path.IndexOf(':') >= 0) return true;
            var p = path.Replace('\\', '/');
            if (p.StartsWith("/")) return true;
            return p.Split('/').Any(seg => seg == "..");
        }

        // null = fine. Lists a zip's entries, never extracts.
        public static string CheckZip(string path)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    if (zip.Entries.Count > 20000) return "The zip has too many entries";
                    foreach (var e in zip.Entries)
                    {
                        if (UnsafeEntryPath(e.FullName)) return "The zip contains an unsafe path";
                        if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) continue;
                        if (IsBlockedName(e.Name)) return "The zip contains a program or script file (." + Extension(e.Name) + ") - those can't be shared here";
                    }
                }
                return null;
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is NotSupportedException) { return "That is not a valid zip file"; }
        }

        // why this file can't be uploaded (null = go ahead): the same refusals the server would give, in friendly words
        public static string CheckUpload(string path, int maxMb)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "Pick a file first";
                var name = Path.GetFileName(path);
                if (IsBlockedName(name)) return "Programs and scripts (." + Extension(name) + ") can't be shared here";
                var len = new FileInfo(path).Length;
                if (len <= 0) return "That file is empty";
                if (len > maxMb * 1024L * 1024L) return "That file is larger than " + maxMb + " MB";
                if (Extension(name) == "zip") return CheckZip(path);
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return "Couldn't read that file"; }
        }

        public static string Sha256(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
            using (var h = SHA256.Create()) return Hex(h.ComputeHash(fs));
        }

        public static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB";
            return (bytes / 1048576.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }

        public static string Stamp(long ms) =>
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        // "Updated" when this PC saved this slot before and the lodge's newest differs from what it saved; "New" when it never saved it
        public static string Badge(SlotVersionInfo newest, SavedRecord saved)
        {
            if (newest == null) return "";
            if (saved == null) return "New";
            return string.Equals(saved.Sha, newest.Sha256, StringComparison.OrdinalIgnoreCase) ? "" : "Updated";
        }

        public static string NoticeText(string action, string by, string name, int v)
        {
            switch (action)
            {
                case "new": return by + " added " + name + " (v" + v + ")";
                case "version": return by + " updated " + name + " (v" + v + ")";
                case "rollback": return by + " rolled " + name + " back to v" + v;
                case "delete": return by + " removed " + name;
                default: return null;
            }
        }

        // ---- Downloads folder (a Save-as dialog starts here)
        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        public static string DownloadsFolder()
        {
            try
            {
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
                {
                    var s = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(s) && Directory.Exists(s)) return s;
                }
            }
            catch { }
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(fallback) ? fallback : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        // ---- Mark of the Web: the Zone.Identifier alternate data stream with ZoneId=3 (Internet), so Windows treats the file as downloaded
        // (SmartScreen, Office protected view). .NET Framework refuses a ':' in a path, hence CreateFile directly.
        const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, CreateAlways = 2, OpenExisting = 3, ShareRead = 1, ShareWrite = 2;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        public static bool MarkOfTheWeb(string file)
        {
            try
            {
                using (var h = CreateFileW(Path.GetFullPath(file) + ":Zone.Identifier", GenericWrite, ShareRead, IntPtr.Zero, CreateAlways, 0x80, IntPtr.Zero))
                {
                    if (h.IsInvalid) return false;
                    using (var fs = new FileStream(h, FileAccess.Write))
                    {
                        var b = Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n");
                        fs.Write(b, 0, b.Length);
                    }
                }
                return true;
            }
            catch (Exception e) { Util.Log("files: could not mark the download (" + e.GetType().Name + ")"); return false; }
        }

        // the ZoneId of a file, -1 when it has none
        public static int ZoneId(string file)
        {
            try
            {
                using (var h = CreateFileW(Path.GetFullPath(file) + ":Zone.Identifier", GenericRead, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0x80, IntPtr.Zero))
                {
                    if (h.IsInvalid) return -1;
                    using (var fs = new FileStream(h, FileAccess.Read))
                    using (var r = new StreamReader(fs, Encoding.ASCII))
                        foreach (var line in r.ReadToEnd().Split('\n'))
                            if (line.Trim().StartsWith("ZoneId=") && int.TryParse(line.Trim().Substring(7), out var z)) return z;
                }
            }
            catch { }
            return -1;
        }
    }

    // what this PC last saved of a slot (kept in the Outpost's own data folder, per lodge)
    public sealed class SavedRecord { public int V; public string Sha; }

    public sealed class SavedFiles
    {
        readonly string path, tag;
        Dictionary<string, SavedRecord> map;

        public SavedFiles(string server, string dataDir = null)
        {
            path = Path.Combine(dataDir ?? Util.DataDir, "files-saved.txt");
            tag = "server\t" + (server ?? "");
        }

        Dictionary<string, SavedRecord> Map()
        {
            if (map != null) return map;
            map = new Dictionary<string, SavedRecord>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    if (lines.Length > 0 && lines[0] == tag)
                        foreach (var l in lines.Skip(1))
                        {
                            var p = l.Split('\t');
                            if (p.Length == 3 && p[0].Length == 32 && int.TryParse(p[1], out var v)) map[p[0]] = new SavedRecord { V = v, Sha = p[2] };
                        }
                }
            }
            catch (Exception e) { Util.Log("files: could not read the saved record (" + e.GetType().Name + ")"); }
            return map;
        }

        public SavedRecord Get(string slotId) => slotId != null && Map().TryGetValue(slotId, out var r) ? r : null;

        public void Set(string slotId, int v, string sha)
        {
            Map()[slotId] = new SavedRecord { V = v, Sha = sha };
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllLines(tmp, new[] { tag }.Concat(Map().Select(kv => kv.Key + "\t" + kv.Value.V + "\t" + kv.Value.Sha)), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            }
            catch (Exception e) { Util.Log("files: could not save the saved record (" + e.GetType().Name + ")"); }
        }
    }
}
