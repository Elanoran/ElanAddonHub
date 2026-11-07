using System.Collections.Generic;
using System.Text.Json.Nodes;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // Who runs an old Outpost or old addons (Lodge server 2.38, feature "versions"; MemberVersions has the rules).
    //   out  versions {channel, addons} after every welcome and whenever the update check sees other installed addons (unchanged = not sent)
    //   in   user.app (every member's Outpost version) and user.versions, on each member's entry
    public partial class LodgeSession
    {
        public bool SupportsVersions { get; private set; }
        LodgeVersions myVersions;
        string lastVersionsJson;
        bool versionsWired;

        void ReadVersionsFeatures(List<string> features)
        {
            SupportsVersions = features.Contains("versions");
            lastVersionsJson = null;                                    // a new connection: say it again
            if (versionsWired) return;
            versionsWired = true;
            // the newest release of a channel became known: everyone's "behind" is worked out again
            MemberVersions.Changed += () => { foreach (var m in Members) m.RefreshBehind(); Changed?.Invoke(); };
        }

        // the update check (MainWindow) tells the session what this Outpost has; sent now when connected
        public void SetMyVersions(LodgeVersions v) { myVersions = v; SendVersions(); }

        void SendVersions()
        {
            if (!Online || !SupportsVersions || myVersions == null) return;
            var obj = LodgeJson.Build(myVersions.Write);
            var json = LodgeJson.Serialize(obj);
            if (json == lastVersionsJson) return;
            lastVersionsJson = json;
            _ = Send(obj);
        }

        // what a member entry says; a member on a channel this Outpost hasn't read yet makes it read that channel's newest release
        static void ApplyVersions(MemberVM m, LodgeUser u)
        {
            var v = u.Versions is JsonObject o ? LodgeVersions.Read(new LodgeJsonFields(o)) : null;
            m.SetVersions(u.App, v);
            if (u.App == null && v == null) return;
            if (MemberVersions.Stable == null) MemberVersions.Ensure(false);
            if (v?.Channel == "test" && MemberVersions.Test == null) MemberVersions.Ensure(true);
        }

        public string MyVersionsJsonForTest => myVersions == null ? null : LodgeJson.Serialize(LodgeJson.Build(myVersions.Write));
    }
}
