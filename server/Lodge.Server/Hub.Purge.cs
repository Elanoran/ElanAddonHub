using System.Text.Json.Nodes;
using Elans.Shared;
using System.Text.RegularExpressions;

namespace Lodge;

// One fan-out for "forget this member" (2.22), called by member.leave, admin.remove, the owner's admin.purge (revert) and the console
// (lodge-admin purge / remove, via the purge.d request folder). Every store that holds something a member authored is listed here; a new
// per-member store must be added to PurgeMember (and to the test that checks it).
//
// What happens to each store (the documented choices):
//   Atlas pool      removed: counters, nodes, fishing spots, finds, spawn points, vendor / quest / revealed / seen rows they authored (merged totals rebuilt)
//   Flights         removed: their leg values and names (merged table rebuilt)
//   Calendar        their sign-ups are removed; the events they organised STAY (organiser becomes "(former member)")
//   Profile         deleted (about, play times, characters, display name)
//   Armory, Badges  deleted
//   Chat history    the messages STAY (a conversation with one side cut out makes no sense) with the author shown as "(former member)"; their reactions and
//                   sign-up card places are removed; pins keep their text
//   Files           uploaded files (chat and shared Files) STAY - they belong to the lodge - but no longer name the uploader
//   Cairn sharing   their "Atlas sharing is on" note (cairn-sharing.json, 2.27) is forgotten
// Backups keep what they held when they were taken until they rotate out.
public partial class LodgeHub
{
    public const string FormerMember = "(former member)";

    public sealed class PurgeSummary
    {
        public string Name; public bool AtlasOnly;
        public int Atlas, Flights, Signups, Organised, Profile, Armory, Badges, Messages, Reactions, Uploads;
        public int Total => Atlas + Flights + Signups + Organised + Profile + Armory + Badges + Messages + Reactions + Uploads;
        // the owner's admin.purged answer (typed: shared/Elans.Shared/Lodge/AdminMessages.cs)
        public AdminPurgedMsg ToMsg() => new()
        {
            Name = Name, AtlasOnly = AtlasOnly, Atlas = Atlas, Flights = Flights, Signups = Signups, Organised = Organised,
            Profile = Profile, Armory = Armory, Badges = Badges, Messages = Messages, Reactions = Reactions, Uploads = Uploads, Total = Total,
        };
    }

    public async Task<PurgeSummary> PurgeMember(string name, bool atlasOnly = false)
    {
        var s = new PurgeSummary { Name = name, AtlasOnly = atlasOnly };
        if (string.IsNullOrWhiteSpace(name)) return s;
        s.Atlas = AtlasPool.Purge(name).Total;
        if (atlasOnly) return s;
        CairnIn.Sharing.Forget(ProfileRules.Key(name));       // 2.27: their "sharing is on" note for the Cairn's data
        CairnIn.Weekly.Forget(ProfileRules.Key(name));        // connected outposts: and this week's anonymous count
        s.Flights = Flight.Purge(name);
        var (signups, organised) = await Calendar.Purge(name, FormerMember);
        s.Signups = signups; s.Organised = organised;
        s.Profile = Profiles.Remove(name) ? 1 : 0;
        s.Armory = Armory.RemoveMember(name);
        s.Badges = Badges.RemoveMember(name) ? 1 : 0;
        var cp = Chat.Purge(name, FormerMember);
        s.Messages = cp.Messages; s.Reactions = cp.Reactions; s.Signups += cp.Signups;
        s.Uploads = Files.Anonymise(name, FormerMember) + SharedFiles.Anonymise(name, FormerMember);
        return s;
    }

    // ---- console requests: lodge-admin writes <codes folder>/purge.d/<id>.req = "<all|atlas>\t<name>" (one atomic file each); the housekeeping runs them
    static readonly Regex PurgeName = new(@"^[\p{L}\p{N}][\p{L}\p{N} _\-]{0,23}$", RegexOptions.Compiled);

    string PurgeDir => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Cfg.CodesFile)) ?? Cfg.DataDir, "purge.d");

    public async Task<int> ProcessPurgeRequests()
    {
        var dir = PurgeDir;
        if (!Directory.Exists(dir)) return 0;
        int done = 0;
        string[] files;
        try { files = Directory.GetFiles(dir, "*.req").OrderBy(f => f, StringComparer.Ordinal).Take(20).ToArray(); } catch { return 0; }
        foreach (var f in files)
        {
            try
            {
                string text = "";
                if (new FileInfo(f).Length <= 512) text = File.ReadAllText(f).Trim();
                File.Delete(f);                                    // first: a request that fails must not run again every 15 seconds
                var parts = text.Split('\t');
                if (parts.Length != 2 || (parts[0] != "all" && parts[0] != "atlas") || !PurgeName.IsMatch(parts[1])) { Log.LogWarning("A purge request was ignored (malformed)"); continue; }
                var r = await PurgeMember(parts[1], parts[0] == "atlas");
                Log.LogInformation("Console purge for {Name}: {Count} item(s) removed{Mode}", Names.ForLog(parts[1]), r.Total, r.AtlasOnly ? " (Atlas only)" : "");
                done++;
            }
            catch (Exception e) { Log.LogWarning("A purge request failed: {Error}", e.GetType().Name); }
        }
        return done;
    }
}
