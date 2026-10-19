using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ElansAddonHub.Services;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // The Lodge Armory (server 2.16, feature "armory"). Characters the member ticked in Settings > Lodge are uploaded as snapshots (ArmoryShare.Build: gear, talents,
    // professions, pets - never bags, bank or gold), only while "Share my characters with the Lodge" is on, only what changed (a signature per character that ignores the
    // game's own saved-at times), after a quiet moment so a /reload storm is one upload. A character that is unticked, or all of them when the switch goes off, is deleted on
    // the server. The list of the Lodge's shared characters (armory.chars) and a single character (armory.get) are fetched when the Characters page asks for them.
    // On a server without the feature nothing is sent and the page says so.
    public partial class LodgeSession
    {
        public static readonly Version ArmoryServer = new Version(2, 16, 0);
        public static readonly Version Armory2Server = new Version(2, 19, 0);       // learned recipes in the snapshot and "who can craft this"
        public bool SupportsArmory2 { get; private set; }
        public const int ArmoryDebounceSeconds = 20;
        public bool SupportsArmory { get; private set; }
        public bool ArmoryDisabledByServer { get; private set; }            // the owner switched the feature off on the server
        public List<ArmoryEntry> ArmoryEntries { get; private set; } = new List<ArmoryEntry>();
        public bool ArmoryListed { get; private set; }                      // an answer arrived on this connection
        public string ArmoryProblem { get; private set; }                   // the last refusal, in plain words (null = none)
        public event Action ArmoryChanged;

        readonly Dictionary<string, Dictionary<string, object>> armoryChars = new Dictionary<string, Dictionary<string, object>>();   // "owner|key" -> snapshot
        readonly HashSet<string> armoryAsked = new HashSet<string>();
        readonly Dictionary<string, string> armoryInflight = new Dictionary<string, string>();                                           // key -> signature of the put waiting for its answer
        readonly Dictionary<string, string> armoryRefused = new Dictionary<string, string>();                                            // key -> signature the server refused (not retried)
        readonly Dictionary<string, KeyValuePair<string, DateTime>> armoryQuiet = new Dictionary<string, KeyValuePair<string, DateTime>>();   // key -> (signature, first seen)
        Dictionary<string, string> armorySent;                                                                                          // key -> signature the lodge holds (persisted per server)
        DateTime armoryNextPut = DateTime.MinValue, armoryLastList = DateTime.MinValue;
        bool armoryGuest, armoryBacklog, armoryDeleteAll;
        int armoryPollTick;

        string ArmorySentPath => Path.Combine(Util.DataDir, "armory-sent-" + AtlasTag() + ".txt");

        void ReadArmoryFeatures(List<string> features)
        {
            var v = ParseVersion(ServerVersion);
            SupportsArmory = features.Contains("armory") && (v == null || v >= ArmoryServer);
            SupportsArmory2 = SupportsArmory && features.Contains("armory2") && (v == null || v >= Armory2Server);
            SupportsReps = SupportsArmory && features.Contains("reputations");
            ResetStandings();
            craftersAnswers.Clear();
            ArmoryDisabledByServer = false; armoryGuest = false;
            armoryInflight.Clear(); armoryAsked.Clear();
            ArmoryProblem = null; ArmoryListed = false;
        }

        void ResetArmory()
        {
            SupportsArmory = false; SupportsArmory2 = false; SupportsReps = false; ResetStandings(); craftersAnswers.Clear(); ArmoryDisabledByServer = false; armoryGuest = false; armoryBacklog = false; armoryDeleteAll = false;
            ArmoryEntries = new List<ArmoryEntry>(); ArmoryListed = false; ArmoryProblem = null;
            armoryChars.Clear(); armoryAsked.Clear(); armoryInflight.Clear(); armoryRefused.Clear(); armoryQuiet.Clear();
            armorySent = null; armoryNextPut = DateTime.MinValue; armoryLastList = DateTime.MinValue;
        }

        // what the page may say: why the Armory can't be shown, or null when it can
        public string ArmoryOfflineLine
        {
            get
            {
                if (!Online && !TestFed) return "You are offline. The Lodge's Armory shows up here once you are connected.";
                if (!SupportsArmory) return "This Lodge's server is older than 2.16, so it has no Armory yet. Ask the Lodge owner to update it.";
                if (ArmoryDisabledByServer) return "The Lodge owner has switched the Armory off.";
                if (MyRole == "guest" || (Me != null && Me.Guest)) return "The Armory is for Lodge members. Guests can't share or browse characters.";
                return null;
            }
        }

        public bool ArmoryAvailable => ArmoryOfflineLine == null;

        // ---- what the lodge already holds from me (key -> signature). The file names the server, so another lodge starts from nothing.
        Dictionary<string, string> ArmorySent()
        {
            if (armorySent != null) return armorySent;
            armorySent = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(ArmorySentPath) && new FileInfo(ArmorySentPath).Length < 1024 * 1024)
                    foreach (var l in File.ReadAllLines(ArmorySentPath))
                    {
                        var p = l.Split('\t');
                        if (p.Length == 2 && p[0].Length > 2 && p[0].Length <= 80 && p[1].Length > 0 && p[1].Length <= 32) armorySent[p[0]] = p[1];
                    }
            }
            catch (Exception e) { Util.Log("armory: could not read the upload record (" + e.GetType().Name + ")"); }
            return armorySent;
        }

        void SaveArmorySent()
        {
            try
            {
                var tmp = ArmorySentPath + ".tmp";
                File.WriteAllLines(tmp, ArmorySent().Select(kv => kv.Key + "\t" + kv.Value), new UTF8Encoding(false));
                if (File.Exists(ArmorySentPath)) File.Replace(tmp, ArmorySentPath, null); else File.Move(tmp, ArmorySentPath);
            }
            catch (Exception e) { Util.Log("armory: could not save the upload record (" + e.GetType().Name + ")"); }
        }

        bool ArmoryMayShare => Settings != null && SupportsArmory && !ArmoryDisabledByServer && !armoryGuest && MyRole != "guest" && !(Me != null && Me.Guest);

        // the characters to share, by key; empty while the switch is off
        public HashSet<string> ArmoryWanted()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (Settings == null || !Settings.ArmoryShareOn || Settings.ArmoryChars == null) return set;
            foreach (var k in Settings.ArmoryChars) if (!string.IsNullOrEmpty(k) && set.Count < ArmoryShare.MaxChars) set.Add(k);
            return set;
        }

        // After the welcome, after the settings changed, and every ~30 s: take unticked characters (or all, when the switch is off) out of the lodge, upload what changed.
        // `now` = no waiting for a quiet moment (just connected, or the person changed the settings).
        public void ArmorySync(bool now = false)
        {
            if (!SupportsArmory) return;
            if (ArmoryMayShare) SyncArmoryUploads(now);
            if (now && ArmoryAvailable) RequestArmoryList(true);
        }

        void SyncArmoryUploads(bool now)
        {
            var sent = ArmorySent();
            var wanted = ArmoryWanted();
            // ---- take out: the switch is off (everything), or a character was unticked
            if (!Settings.ArmoryShareOn)
            {
                if (sent.Count > 0 || armoryDeleteAll)
                {
                    armoryDeleteAll = false;
                    sent.Clear(); SaveArmorySent();
                    armoryQuiet.Clear(); armoryRefused.Clear();
                    _ = Send(LodgeFieldsDict.Build(new ArmoryDeleteMsg { All = true }.Write));
                }
                return;
            }
            foreach (var k in sent.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                sent.Remove(k); armoryQuiet.Remove(k); armoryRefused.Remove(k);
                SaveArmorySent();
                _ = Send(LodgeFieldsDict.Build(new ArmoryDeleteMsg { Key = k }.Write));
            }
            if (wanted.Count == 0) { armoryBacklog = false; return; }

            // ---- put what changed
            List<InvChar> local;
            try { local = ArmorySourceForTest != null ? ArmorySourceForTest() : ArmoryShare.Local(Settings?.WowRoot); }
            catch (Exception e) { Util.Log("armory: " + e.GetType().Name); return; }
            var clock = Clock();
            bool backlog = false;
            foreach (var c in local.Where(c => wanted.Contains(c.Key)))
            {
                var plain = ArmoryShare.Build(c, false, SupportsArmory2, RepsShared);
                if (plain == null) continue;
                var sig = ArmoryShare.Signature(plain);
                if (sent.TryGetValue(c.Key, out var have) && have == sig) { armoryQuiet.Remove(c.Key); continue; }
                if (armoryRefused.TryGetValue(c.Key, out var refused) && refused == sig) continue;
                if (armoryInflight.TryGetValue(c.Key, out var fl) && fl == sig) { backlog = true; continue; }
                // a quiet moment first: the game rewrites its file on every /reload and logout
                if (!now)
                {
                    if (!armoryQuiet.TryGetValue(c.Key, out var q) || q.Key != sig) { armoryQuiet[c.Key] = new KeyValuePair<string, DateTime>(sig, clock); backlog = true; continue; }
                    if (clock - q.Value < TimeSpan.FromSeconds(ArmoryDebounceSeconds)) { backlog = true; continue; }
                }
                if (clock < armoryNextPut) { backlog = true; continue; }
                var full = ArmoryShare.Build(c, true, SupportsArmory2, RepsShared);
                var frame = LodgeFieldsDict.Build(new ArmoryPutMsg { Key = c.Key, Char = full }.Write);
                if (Json.Write(frame).Length > 60 * 1024) { armoryRefused[c.Key] = sig; ArmoryProblem = c.Name + " has too much to share."; continue; }
                armoryInflight[c.Key] = sig;
                armoryQuiet.Remove(c.Key);
                _ = Send(frame);
            }
            armoryBacklog = backlog;
        }

        // ---- reading
        public void RequestArmoryList(bool force = false)
        {
            if (!SupportsArmory || !ArmoryAvailable) return;
            if (!force && Clock() - armoryLastList < TimeSpan.FromSeconds(20)) return;
            armoryLastList = Clock();
            _ = Send(LodgeFieldsDict.Build(new ArmoryListMsg().Write));
        }

        public Dictionary<string, object> ArmoryChar(string id) => id != null && armoryChars.TryGetValue(id, out var c) ? c : null;

        public void RequestArmoryChar(ArmoryEntry e, bool force = false)
        {
            if (e == null || !SupportsArmory || !ArmoryAvailable) return;
            var id = e.Id;
            if (!force && (armoryAsked.Contains(id) || armoryChars.ContainsKey(id))) return;
            armoryAsked.Add(id);
            _ = Send(LodgeFieldsDict.Build(new ArmoryGetMsg { Owner = e.Owner, Key = e.Key }.Write));
        }

        // a character that changed since it was opened is fetched again when the list says it is newer
        bool ArmoryNewer(ArmoryEntry e) => armoryChars.TryGetValue(e.Id, out var c) && c.Long("at") != e.At;

        void OnArmorySaved(Dictionary<string, object> m)
        {
            if (!SupportsArmory) return;
            var saved = ArmorySavedMsg.Read(new LodgeFieldsDict(m));
            var key = saved.Key;
            if (key != null && armoryInflight.TryGetValue(key, out var sig))
            {
                armoryInflight.Remove(key);
                if (saved.Ok) { ArmorySent()[key] = sig; SaveArmorySent(); ArmoryProblem = null; }
                else
                {
                    var why = saved.Why;
                    if (why == "rate") armoryNextPut = Clock() + TimeSpan.FromSeconds(30);
                    else if (why == "full") { armoryRefused[key] = sig; ArmoryProblem = "The Lodge keeps at most " + ArmoryShare.MaxChars + " of your characters. Untick one to share another."; }
                    else if (why == "guest") armoryGuest = true;
                    else if (why == "disabled") ArmoryDisabledByServer = true;
                    else { armoryRefused[key] = sig; ArmoryProblem = "The Lodge did not take one of your characters (" + (why ?? "refused") + ")."; }
                }
            }
            else if (!saved.Ok)
            {
                var why = saved.Why;
                if (why == "guest") armoryGuest = true; else if (why == "disabled") ArmoryDisabledByServer = true; else if (why == "rate") armoryNextPut = Clock() + TimeSpan.FromSeconds(30);
            }
            if (armoryInflight.Count == 0) RequestArmoryList(true);          // the list shows what I just shared
            ArmoryChanged?.Invoke(); Changed?.Invoke();
        }

        void OnArmoryDeleted(Dictionary<string, object> m)
        {
            if (!SupportsArmory) return;
            var d = ArmoryDeletedMsg.Read(new LodgeFieldsDict(m));
            if (d.Ok && (d.Key != null || d.All || d.Removed != null)) RequestArmoryList(true);
            else if (!d.Ok && d.Why == "guest") armoryGuest = true;
            ArmoryChanged?.Invoke();
        }

        void OnArmoryChars(Dictionary<string, object> m)
        {
            if (!SupportsArmory) return;
            var list = new List<ArmoryEntry>();
            foreach (var o in ArmoryCharsMsg.Read(new LodgeFieldsDict(m)).Chars)
            {
                if (list.Count >= 2000) break;
                var e = ArmoryEntry.FromSummary(o);
                if (e != null) list.Add(e);
            }
            ArmoryEntries = list;
            ArmoryListed = true;
            // an opened character that was shared again since: forget it so it is fetched fresh
            foreach (var e in list)
                if (ArmoryNewer(e)) { armoryChars.Remove(e.Id); armoryAsked.Remove(e.Id); }
            foreach (var id in armoryChars.Keys.Where(id => !list.Any(e => e.Id == id)).ToList()) { armoryChars.Remove(id); armoryAsked.Remove(id); }
            ArmoryChanged?.Invoke();
        }

        void OnArmoryChar(Dictionary<string, object> m)
        {
            if (!SupportsArmory) return;
            var msg = ArmoryCharMsg.Read(new LodgeFieldsDict(m));
            var owner = msg.Owner; var key = msg.Key; var ch = msg.Char as Dictionary<string, object>;      // the sheet, read by the Characters page with its own checks
            if (owner == null || key == null || ch == null) return;
            var id = owner + "|" + key;
            if (!armoryAsked.Contains(id) && !armoryChars.ContainsKey(id)) return;      // only what was asked for
            armoryChars[id] = ch;
            ArmoryChanged?.Invoke();
        }

        // ---- "Who in the Lodge can craft this?" (server 2.19, armory2). Answers are kept for two minutes per set of spells.
        sealed class CraftersAnswer { public DateTime Asked, Answered; public List<LodgeCrafter> Rows = new List<LodgeCrafter>(); }
        readonly Dictionary<string, CraftersAnswer> craftersAnswers = new Dictionary<string, CraftersAnswer>(StringComparer.Ordinal);
        public event Action CraftersChanged;
        public static readonly TimeSpan CraftersKeep = TimeSpan.FromMinutes(2), CraftersRetry = TimeSpan.FromSeconds(10);

        static string CraftersKey(IEnumerable<int> spells) => string.Join(",", spells.Where(s => s > 0).Distinct().OrderBy(s => s).Take(16));

        public bool CraftersAvailable => SupportsArmory2 && ArmoryAvailable;

        // The cached answer, or Pending after the question was sent. Never blocks; ArmoryChanged-style: CraftersChanged fires when an answer arrives.
        public LodgeCrafters CraftersFor(IEnumerable<int> spells)
        {
            var res = new LodgeCrafters();
            var key = CraftersKey(spells ?? Enumerable.Empty<int>());
            if (key.Length == 0) { res.Problem = "No recipe."; return res; }
            if (!SupportsArmory2) { res.Problem = SupportsArmory ? "This Lodge's server is older than 2.19: it cannot say who can craft something." : ArmoryOfflineLine; return res; }
            var off = ArmoryOfflineLine;
            if (off != null) { res.Problem = off; return res; }
            res.Available = true;
            var now = Clock();
            craftersAnswers.TryGetValue(key, out var a);
            bool fresh = a != null && a.Answered != default(DateTime) && now - a.Answered < CraftersKeep;
            if (a != null && a.Answered != default(DateTime)) res.Rows.AddRange(a.Rows);
            if (!fresh && (a == null || a.Asked == default(DateTime) || now - a.Asked > CraftersRetry))
            {
                if (a == null) craftersAnswers[key] = a = new CraftersAnswer();
                a.Asked = now;
                var ids = key.Split(',').Select(s => (long)int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToList();
                _ = Send(LodgeFieldsDict.Build(new ArmoryCraftersGetMsg { Spells = ids }.Write));
            }
            res.Pending = !fresh && a != null && a.Answered == default(DateTime);
            return res;
        }

        void OnArmoryCrafters(Dictionary<string, object> m)
        {
            if (!SupportsArmory2) return;
            // the spells asked are read from the raw frame on purpose: only JSON numbers count, one by one (ArmoryCraftersMsg.Spells is the lenient reading)
            var asked = new List<int>();
            foreach (var o in m.List("spells")) if (o is double d && d >= 1 && d <= int.MaxValue && asked.Count < 16) asked.Add((int)d);
            var answer = ArmoryCraftersMsg.Read(new LodgeFieldsDict(m));
            var key = CraftersKey(asked);
            if (!craftersAnswers.TryGetValue(key, out var a)) return;              // only what was asked for
            var set = new HashSet<int>(asked);
            var rows = new List<LodgeCrafter>();
            foreach (var o in answer.Who)
            {
                if (rows.Count >= 80) break;
                var c = LodgeCrafter.From(o);
                if (c != null && set.Contains(c.Spell)) rows.Add(c);
            }
            a.Rows = rows; a.Answered = Clock();
            CraftersChanged?.Invoke();
        }

        // the server's refusals of a read (a code on the generic error frame)
        void OnArmoryError(string code)
        {
            if (code == "disabled") ArmoryDisabledByServer = true;
            else if (code == "guest") armoryGuest = true;
            else if (code == "notfound") { armoryLastList = DateTime.MinValue; RequestArmoryList(); }
            ArmoryChanged?.Invoke();
        }

        // every 2 s with the other ticks: look at the Hub data about every 30 s; send a waiting character when its quiet moment and slot are there
        void ArmoryTick()
        {
            if (!SupportsArmory) return;
            if (armoryPollTick++ % 15 == 0 || (armoryBacklog && armoryInflight.Count == 0)) ArmorySync();
        }

        // Settings > Lodge: the switch or a checkbox changed
        public void ArmorySettingsChanged()
        {
            if (!SupportsArmory) return;
            armoryDeleteAll = !Settings.ArmoryShareOn;
            ArmorySync(true);
        }

        // ---- test hooks
        public Func<List<InvChar>> ArmorySourceForTest;
        public Dictionary<string, string> ArmorySentForTest => ArmorySent();
        public string ArmorySentPathForTest => ArmorySentPath;
        public int ArmoryInflightForTest => armoryInflight.Count;
        public bool ArmoryBacklogForTest => armoryBacklog;
        public void ArmoryTickForTest() => ArmoryTick();
        public void ForgetCraftersForTest() => craftersAnswers.Clear();
        public void ArmoryResetForTest() { armorySent = null; armoryInflight.Clear(); armoryRefused.Clear(); armoryQuiet.Clear(); armoryNextPut = DateTime.MinValue; armoryLastList = DateTime.MinValue; armoryGuest = false; ArmoryProblem = null; }
    }
}
