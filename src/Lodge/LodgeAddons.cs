using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // A Lodge file marked "addon" (Lodge 2.32, feature "filesaddons"), installed straight into the WoW Forever client's AddOns folder (3.4).
    // The mark is the uploader's promise; this PC checks the zip again before anything is touched (Elans.Shared.AddonZip: only addon folders,
    // each with its own .toc, no unsafe path, no program or script, no zip bomb). Then, the same way as a CurseForge update (CurseForgeDirect):
    //   * unpacked into a temporary folder inside AddOns, every path checked to stay inside it;
    //   * a folder that is a git checkout (someone's development copy) is never replaced;
    //   * what is there now is backed up (backups\addons\lodge-<slot name>, the newest 2 kept), together with folders an earlier version of the
    //     same slot installed that the new one no longer has (those are taken out);
    //   * swapped by renames inside AddOns; any failure (WoW holding a file open) puts every folder back. WTF / SavedVariables are never touched.
    public static class LodgeAddons
    {
        // the Forever client's AddOns folder, or null when there is no WoW folder / no Forever client (the self-test replaces it)
        public static Func<string> AddOnsDir = () =>
        {
            var root = ItemData.WowRoot();
            if (string.IsNullOrEmpty(root) || !WowLocator.HasFlavor(root, WowLocator.ForeverFolder)) return null;
            return WowLocator.AddOnsDir(root, WowLocator.ForeverFolder);
        };

        public sealed class Result { public bool Ok; public string Error; public List<string> Folders = new List<string>(); public string BackupDir; }

        internal static Action<string> SwapHook;              // self-test: called before each new folder is moved in (throws = a failure mid-swap)

        // earlier = the folders this slot installed last time (its own, so they may be taken out when the new version drops one)
        public static Result Install(string addOnsDir, string slotName, string zipFile, IEnumerable<string> earlier = null)
        {
            var res = new Result();
            if (string.IsNullOrEmpty(addOnsDir) || !Directory.Exists(addOnsDir)) { res.Error = "The WoW Forever AddOns folder wasn't found - check the WoW folder in Settings."; return res; }
            var check = AddonZip.Check(zipFile);
            if (!check.Ok) { res.Error = "Not installed: " + check.Problem + "."; return res; }
            var tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            var tmp = Path.Combine(addOnsDir, ".elanshub-new-" + tag);
            var oldRoot = Path.Combine(addOnsDir, ".elanshub-old-" + tag);
            var moved = new List<string>(); var placed = new List<string>();
            string bdir = null;
            try
            {
                Directory.CreateDirectory(tmp);
                var tmpFull = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
                try
                {
                    using (var za = ZipFile.OpenRead(zipFile))
                        foreach (var en in za.Entries)
                        {
                            var name = en.FullName.Replace('\\', '/');
                            var target = Path.GetFullPath(Path.Combine(tmp, name));
                            if (!target.StartsWith(tmpFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("unsafe path");
                            if (name.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            en.ExtractToFile(target, true);
                        }
                }
                catch (Exception) { res.Error = "Not installed: the zip couldn't be unpacked - nothing was changed."; return res; }

                var folders = check.Folders;
                foreach (var f in folders)
                    if (Directory.Exists(Path.Combine(addOnsDir, f, ".git"))) { res.Error = f + " is a development copy (git) - not touching it. Nothing was changed."; return res; }
                var retired = (earlier ?? Enumerable.Empty<string>()).Where(m => AddonZip.SafeFolder(m) && !folders.Contains(m, StringComparer.OrdinalIgnoreCase)
                    && Directory.Exists(Path.Combine(addOnsDir, m)) && !Directory.Exists(Path.Combine(addOnsDir, m, ".git"))).ToList();

                var key = "lodge-" + AddonSources.SafeName(slotName ?? "addon");
                bdir = Path.Combine(AddonSources.BackupRoot, key, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                bool any = false;
                foreach (var f in folders.Concat(retired))
                {
                    var dest = Path.Combine(addOnsDir, f);
                    if (Directory.Exists(dest)) { Util.CopyDir(dest, Path.Combine(bdir, f)); any = true; }
                }
                if (!any) bdir = null;

                try
                {
                    Directory.CreateDirectory(oldRoot);
                    foreach (var f in retired) { Directory.Move(Path.Combine(addOnsDir, f), Path.Combine(oldRoot, f)); moved.Add(f); }
                    foreach (var f in folders)
                    {
                        var dest = Path.Combine(addOnsDir, f);
                        if (Directory.Exists(dest)) { Directory.Move(dest, Path.Combine(oldRoot, f)); moved.Add(f); }
                        SwapHook?.Invoke(f);
                        Directory.Move(Path.Combine(tmp, f), dest);
                        placed.Add(f);
                    }
                }
                catch (Exception ex)
                {
                    foreach (var f in placed.AsEnumerable().Reverse()) try { Util.DeleteDir(Path.Combine(addOnsDir, f)); } catch { }
                    foreach (var f in moved.AsEnumerable().Reverse())
                        try { var dest = Path.Combine(addOnsDir, f); if (!Directory.Exists(dest)) Directory.Move(Path.Combine(oldRoot, f), dest); } catch (Exception rex) { Util.Log("lodge addon: couldn't put " + f + " back: " + rex.Message); }
                    if (bdir != null) try { Util.DeleteDir(bdir); } catch { }
                    res.Error = "Couldn't replace the addon folders (is WoW holding files open? close it and try again). Everything was put back.";
                    Util.Log("lodge addon: swap failed: " + ex.GetType().Name);
                    return res;
                }
                res.Ok = true; res.Folders = folders.ToList(); res.BackupDir = bdir;
                AddonSources.Prune(key, 2);
                Util.Log("lodge addon: installed " + string.Join(",", folders) + (retired.Count > 0 ? " (retired " + string.Join(",", retired) + ")" : "") + (bdir != null ? ", backup " + bdir : ""));
                return res;
            }
            finally
            {
                try { Util.DeleteDir(tmp); } catch { }
                try { Util.DeleteDir(oldRoot); } catch { }
            }
        }
    }

    // what this PC installed in WoW from a lodge's addon slots (kept in the Outpost's data folder, per lodge, like SavedFiles):
    // slot id \t version \t sha256 \t keep updated (0/1) \t folders (comma separated)
    public sealed class InstalledRecord { public int V; public string Sha = ""; public bool Auto; public List<string> Folders = new List<string>(); }

    public sealed class InstalledAddons
    {
        readonly string path, tag;
        Dictionary<string, InstalledRecord> map;

        public InstalledAddons(string server, string dataDir = null)
        {
            path = Path.Combine(dataDir ?? Util.DataDir, "files-installed.txt");
            tag = "server\t" + (server ?? "");
        }

        Dictionary<string, InstalledRecord> Map()
        {
            if (map != null) return map;
            map = new Dictionary<string, InstalledRecord>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    if (lines.Length > 0 && lines[0] == tag)
                        foreach (var l in lines.Skip(1))
                        {
                            var p = l.Split('\t');
                            if (p.Length == 5 && p[0].Length == 32 && int.TryParse(p[1], out var v))
                                map[p[0]] = new InstalledRecord { V = v, Sha = p[2], Auto = p[3] == "1", Folders = p[4].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Where(AddonZip.SafeFolder).ToList() };
                        }
                }
            }
            catch (Exception e) { Util.Log("files: could not read the installed record (" + e.GetType().Name + ")"); }
            return map;
        }

        public InstalledRecord Get(string slotId) => slotId != null && Map().TryGetValue(slotId, out var r) ? r : null;

        public void Set(string slotId, InstalledRecord r) { Map()[slotId] = r; Write(); }
        public void Forget(string slotId) { if (Map().Remove(slotId ?? "")) Write(); }

        void Write()
        {
            try
            {
                var tmp = path + ".tmp";
                File.WriteAllLines(tmp, new[] { tag }.Concat(Map().Select(kv => kv.Key + "\t" + kv.Value.V + "\t" + kv.Value.Sha + "\t" + (kv.Value.Auto ? "1" : "0") + "\t" + string.Join(",", kv.Value.Folders))), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            }
            catch (Exception e) { Util.Log("files: could not save the installed record (" + e.GetType().Name + ")"); }
        }
    }
}
