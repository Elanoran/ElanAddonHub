using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ElansAddonHub.Services
{
    // The one shared way to find an addon's SavedVariables files: <root>\_<flavor>_\WTF\Account\<ACCOUNT>\SavedVariables\<name>.lua.
    // Read-only. Folders and files that are reparse points (junctions, symlinks) are skipped, so a link can never lead the app out of the WoW folder.
    // Every reader (presence, calendar, inventory, health, flights, Hub data) uses this walk, SafeRead and SavedVarsCache below.
    public static class SavedVarsFiles
    {
        // every <root>\_*_\WTF\Account\<ACCOUNT>\SavedVariables folder that exists (also the ones that do not hold the file yet: the file watchers need them)
        public static List<string> Dirs(string root)
        {
            var dirs = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return dirs;
            try
            {
                foreach (var flavor in Directory.GetDirectories(root, "_*_"))
                {
                    var accounts = Path.Combine(flavor, "WTF", "Account");
                    if (!Directory.Exists(accounts) || IsLink(accounts)) continue;
                    foreach (var acc in Directory.GetDirectories(accounts))
                    {
                        if (IsLink(acc)) continue;
                        var dir = Path.Combine(acc, "SavedVariables");
                        if (!Directory.Exists(dir) || IsLink(dir)) continue;
                        dirs.Add(dir);
                    }
                }
            }
            catch { }
            return dirs;
        }

        // the files named addonFileName (a file name, never a path) in all of those folders
        public static List<string> For(string root, string addonFileName)
        {
            var files = new List<string>();
            if (string.IsNullOrEmpty(addonFileName) || addonFileName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return files;
            foreach (var dir in Dirs(root))
            {
                var f = Path.Combine(dir, addonFileName);
                if (File.Exists(f) && !IsLink(f)) files.Add(f);
            }
            return files;
        }

        // a junction / symlink (or a path whose attributes cannot be read)
        public static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; } catch { return true; }
        }

        // The one shared read: the game may still hold the file while it writes, so open it shared (read/write/delete) and retry a few times.
        // Returns null when it stayed locked or the file is not there; other errors (no access) throw like File.Open would.
        public static string SafeRead(string path, int attempts = 4, int waitMs = 60)
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, new UTF8Encoding(false), true)) return sr.ReadToEnd();
                }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                catch (IOException) when (i + 1 < attempts) { Thread.Sleep(waitMs); }
                catch (IOException) { }
            }
            return null;
        }
    }

    // One parse of a SavedVariables file per change, shared by every reader (and every thread).
    //   Get(path) -> the file's globals ("ElansHubDB" -> table, ...), parsed again only when path + mtime + size changed.
    //   A file that cannot be read (locked) or does not parse keeps the last good parse; null when there never was one.
    // The returned dictionaries are SHARED and must be treated as read-only: callers read from them, build their own models, and never write into them.
    // Safe from several threads (HealthMonitor reads on a worker thread): each file has its own lock, so a big file being parsed does not stall a small one.
    public static class SavedVarsCache
    {
        public class Parsed
        {
            public string Key;                                  // path|mtime ticks|size of what was parsed: changes exactly when the file does
            public Dictionary<string, object> Globals;          // read-only
        }

        class Entry { public readonly object Gate = new object(); public Parsed Good; public string FailedKey; }

        static readonly object mapGate = new object();
        static readonly Dictionary<string, Entry> map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static int parses;

        // how many files were actually parsed since the last Reset (tests)
        public static int ParseCount => Volatile.Read(ref parses);

        public static void Reset() { lock (mapGate) { map.Clear(); } Interlocked.Exchange(ref parses, 0); }

        public static Dictionary<string, object> Get(string path) => Load(path)?.Globals;

        // the same with the identity of the parse, for callers that cache a model built from it (HubData)
        public static Parsed Load(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Entry e;
            lock (mapGate)
            {
                if (!map.TryGetValue(path, out e)) map[path] = e = new Entry();
            }
            lock (e.Gate)
            {
                string key;
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists) return e.Good;                  // gone for a moment (the game replaces the file) or for good: callers walk the folders first, so they only ask for files that are there
                    key = path + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length;
                }
                catch { return e.Good; }
                if (e.Good != null && e.Good.Key == key) return e.Good;
                if (e.FailedKey == key) return e.Good;          // this exact content did not parse: do not try again until the file changes
                try
                {
                    var text = SavedVarsFiles.SafeRead(path);
                    if (text == null) return e.Good;             // still locked: keep the last good parse, ask again next time
                    Interlocked.Increment(ref parses);
                    try { e.Good = new Parsed { Key = key, Globals = LuaData.ReadGlobals(text) }; e.FailedKey = null; }
                    catch (Exception ex) { e.FailedKey = key; Util.Log("saved variables: unreadable " + Path.GetFileName(path) + " (" + ex.GetType().Name + ")"); }
                }
                catch (Exception ex) { Util.Log("saved variables: read failed " + Path.GetFileName(path) + " (" + ex.GetType().Name + ")"); }
                return e.Good;
            }
        }
    }
}
