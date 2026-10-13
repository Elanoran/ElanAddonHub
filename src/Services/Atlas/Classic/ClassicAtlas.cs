using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services.Atlas.Classic
{
    public enum ClassicState { NotDownloaded, Downloaded, Ready }

    // Layer C of the Atlas: the "Classic baseline". Static entry point for the UI and the other Atlas layers. Everything it returns is labelled
    // Source = "Classic baseline" (ClassicLabel.Source). Until the user has downloaded the dump and the index is built, every query answers "nothing".
    public static class ClassicAtlas
    {
        static readonly object gate = new object();
        static ClassicIndex current;
        static bool tried;
        static readonly ClassicItemSource[] NoSources = new ClassicItemSource[0];

        public static string Source => ClassicLabel.Source;

        // The loaded index (loads index.bin on first use); null when there is none yet.
        public static ClassicIndex Current
        {
            get
            {
                lock (gate)
                {
                    if (current == null && !tried)
                    {
                        tried = true;
                        var f = ClassicPaths.IndexFile();
                        if (File.Exists(f)) current = ClassicIndex.Load(f);
                    }
                    return current;
                }
            }
        }

        public static bool IsReady => Current != null;

        // test hook / after a rebuild: replace the loaded index (null = forget it, load again on next use)
        public static void Use(ClassicIndex index) { lock (gate) { current = index; tried = index != null; } }
        public static void Reload() { lock (gate) { current = null; tried = false; } }

        public static ClassicState State()
        {
            if (IsReady) return ClassicState.Ready;
            return ClassicDownloader.ReadSource() != null ? ClassicState.Downloaded : ClassicState.NotDownloaded;
        }

        // ---- queries
        public static IReadOnlyList<ClassicItemSource> SourcesOf(int itemId) => Current?.SourcesOf(itemId) ?? NoSources;
        public static ClassicNpc NpcInfo(int npcId) => Current?.NpcInfo(npcId);
        public static ClassicObject ObjectInfo(int objectId) => Current?.ObjectInfo(objectId);
        public static ClassicQuest QuestInfo(int questId) => Current?.QuestInfo(questId);
        public static ClassicItemInfo ItemInfo(int itemId) => Current?.ItemInfo(itemId);
        public static IReadOnlyList<ClassicSpawn> Spawns(int npcId) => Current?.Spawns(npcId) ?? new ClassicSpawn[0];
        public static IReadOnlyList<ClassicVendorItem> VendorItems(int npcId) => Current?.VendorItems(npcId) ?? new ClassicVendorItem[0];
        public static IReadOnlyList<ClassicQuest> QuestsRewarding(int itemId) => Current?.QuestsRewarding(itemId) ?? new ClassicQuest[0];
        public static List<ClassicSearchHit> Search(string text, int max = 50, params ClassicEntityKind[] kinds) => Current?.Search(text, max, kinds) ?? new List<ClassicSearchHit>();

        // ---- build / remove

        // Reads the downloaded dump + updates and writes index.bin (on a worker thread). Returns the report; null when nothing was downloaded.
        public static Task<ClassicImportReport> BuildIndex(IProgress<ClassicProgress> progress, CancellationToken ct, string dir = null)
        {
            return Task.Run(() =>
            {
                var src = ClassicDownloader.ReadSource(dir);
                if (src == null) return null;
                var dump = ClassicPaths.DumpFile(src.Str("dump") ?? "", dir);
                if (!File.Exists(dump)) return null;
                var prov = new ClassicProvenance { DumpName = src.Str("dump"), CommitSha = src.Str("commit"), CommitDate = src.Str("commitDate") };
                var rep = ClassicImporter.Import(dump, ClassicPaths.UpdatesDir(dir), ClassicPaths.IndexFile(dir), prov, progress, ct);
                if (dir == null && !rep.Cancelled) Reload();
                return rep;
            }, ct);
        }

        // Removes the downloaded data and the index (the user's "remove" button).
        public static void Delete(string dir = null)
        {
            dir = dir ?? ClassicPaths.Dir;
            if (Directory.Exists(dir)) Util.DeleteDir(dir);
            if (dir == ClassicPaths.Dir) Use(null);
        }
    }
}
