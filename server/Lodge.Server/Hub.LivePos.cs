using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// Live map positions between members (2.30, feature "liveposition", always advertised). Opt-in per member and off by default: a position is only
// ever relayed while the member's own client sends one ("position", see PresenceModule.Position), and every viewer is checked here, never by the
// client. Who sees a sharer's dot:
//   - the sharer themselves;
//   - nobody who is a guest (guests never share and never see positions);
//   - an invisible ("Appear offline") sharer: only Perm.SeeInvisible (NOT the voice-room viewers CanSee lets in - a position is more than a name);
//   - scope "officers": officers and the owner who can see the member; scope "everyone": every member+ who can see the member.
// A position also needs the member's zone to be shared ("game" with playing and a zone): it is never looser than "share zone".
// Each sharer remembers who holds their dot (LivePosViewers) and what was sent last: an unchanged position is not sent again, a viewer who
// loses the right to see it (scope, rank, opt-out) gets {"t":"memberpos","id":..,"valid":false}; one who loses sight of the member entirely already
// got a "leave" (clients drop the dot on it) and is only forgotten here.
public partial class LodgeHub
{
    public static bool MayShareLivePos(Member m) =>
        m.Personal && Roles.Can(m.Role, Perm.LivePosShare) && m.Game is { } g && g["playing"] is JsonValue p && p.TryGetValue<bool>(out var on) && on && g["zone"] != null;

    // why MayShareLivePos says no, in plain words for the owner's log
    public static string WhyNoLivePos(Member m) =>
        !m.Personal ? "a guest or shared login can't share it" : !Roles.Can(m.Role, Perm.LivePosShare) ? "their rank can't share it"
        : m.Game is not { } g ? "their Outpost shares no game presence" : !(g["playing"] is JsonValue p && p.TryGetValue<bool>(out var on) && on) ? "not in the game"
        : g["zone"] == null ? "their zone is not shared (Settings > Privacy)" : "";

    public bool MaySeeLivePos(Member viewer, Member subject)
    {
        if (subject.LivePos == null) return false;
        if (viewer.Id == subject.Id) return true;
        if (!viewer.Personal || !Roles.Can(viewer.Role, Perm.LivePosSee)) return false;
        if (subject.Invisible) return Roles.Can(viewer.Role, Perm.SeeInvisible);
        if (!CanSee(viewer, subject)) return false;
        return (string)subject.LivePos["scope"] != "officers" || Roles.AtLeast(viewer.Role, "officer");
    }

    // memberpos (typed: Elans.Shared MemberPosMsg) from the cleaned position (PresenceModule.BuildPosition always fills mapId, fx, fy, move)
    static JsonObject PosMessage(Member m) => LodgeJson.Build(new MemberPosMsg
    {
        Id = m.Id, Valid = true, MapId = (int)m.LivePos["mapId"], Fx = (double)m.LivePos["fx"], Fy = (double)m.LivePos["fy"], Move = (int)m.LivePos["move"],
    }.Write);

    // tell everyone who may see m's position (and only them) where m is now; withdraw it from who no longer may
    public async Task BroadcastLivePos(Member m)
    {
        await m.LivePosLock.WaitAsync();
        try
        {
            if (m.LivePos != null && !MayShareLivePos(m)) m.LivePos = null;
            var msg = m.LivePos == null ? null : PosMessage(m);
            var clear = LodgeJson.Build(new MemberPosMsg { Id = m.Id, Valid = false }.Write);
            bool same = msg != null && m.LivePosSent != null && JsonNode.DeepEquals(msg, m.LivePosSent);
            var sends = new List<Task>();
            var online = members.Values.ToList();
            foreach (var v in online)
            {
                bool had = m.LivePosViewers.Contains(v.Id);
                if (msg != null && MaySeeLivePos(v, m))
                {
                    if (had && same) continue;
                    m.LivePosViewers.Add(v.Id);
                    sends.Add(Send(v, msg));
                }
                else if (had)
                {
                    m.LivePosViewers.Remove(v.Id);
                    if (v.Id == m.Id || CanSee(v, m)) sends.Add(Send(v, clear));   // lost sight of the member: their "leave" already took the dot
                }
            }
            m.LivePosViewers.RemoveWhere(id => online.All(v => v.Id != id));
            m.LivePosSent = msg;
            await Task.WhenAll(sends);
        }
        finally { m.LivePosLock.Release(); }
    }

    // something that changes who may see positions happened (someone joined, a rank or a visibility changed): every sharer re-checks.
    // Unchanged positions go only to viewers who do not have them yet.
    public Task RefreshLivePositions() =>
        Task.WhenAll(members.Values.Where(m => m.LivePos != null || m.LivePosViewers.Count > 0).ToList().Select(BroadcastLivePos));
}
