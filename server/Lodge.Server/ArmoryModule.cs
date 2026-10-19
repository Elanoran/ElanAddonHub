using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// The Armory (2.16). Client to server: armory.put { key, char } (a member's own character; a burst of 30, then one per 8 s per identity; frame limit 64 KB),
// armory.delete { key } or { all: true } (only your own), armory.list {} and armory.get { owner, key } (Page budget). Server to client:
// armory.saved { ok, key, stored?, why? }, armory.deleted { ok, removed, key? }, armory.chars { chars: [summary...] } and armory.char { owner, by, key, char }.
// Members and up share and read; guests (the shared code) do neither. Feature off (LODGE_ARMORY_ENABLED=0): not advertised, put answers why "disabled",
// the rest answer an error. A member who leaves or is removed takes their characters with them. Nothing the clients send is ever logged - only counts.
public sealed class ArmoryModule : IModule
{
    readonly LodgeHub hub;
    public readonly ArmoryStore Store;

    public ArmoryModule(LodgeHub hub)
    {
        this.hub = hub;
        Store = new ArmoryStore(hub.Cfg.DataDir, hub.Log, hub.Clock);
    }

    public bool Enabled => hub.Cfg.ArmoryEnabled;
    public bool RepsEnabled => hub.Cfg.ArmoryEnabled && hub.Cfg.ArmoryRepsEnabled;

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        switch (type)
        {
            case LodgeMsg.ArmoryPut: await Put(me, m); return true;
            case LodgeMsg.ArmoryDelete: await Delete(me, m); return true;
            case LodgeMsg.ArmoryList: await List(me); return true;
            case LodgeMsg.ArmoryGet: await Get(me, m); return true;
            case LodgeMsg.ArmoryCrafters: await Crafters(me, m); return true;
            case LodgeMsg.ArmoryStanding: await Standing(me, m); return true;
        }
        return false;
    }

    static string Identity(Member me) => ProfileRules.Key(me.Name);

    Task Saved(Member me, bool ok, string key, string why = null, bool stored = false)
    {
        var o = new JsonObject { ["t"] = LodgeMsg.ArmorySaved, ["ok"] = ok, ["stored"] = stored };
        if (key != null) o["key"] = key;
        if (why != null) o["why"] = why;
        return hub.Send(me, o);
    }

    async Task Put(Member me, JsonObject m)
    {
        var key = J.Str(m, "key");
        if (!Enabled) { await Saved(me, false, null, "disabled"); return; }
        if (!me.Personal || !Roles.Can(me.Role, Perm.ArmoryShare)) { await Saved(me, false, null, "guest"); return; }
        if (!Armory.ValidKey(key)) { await Saved(me, false, null, "invalid"); return; }
        if (!me.Budget.ArmoryPut.TryTake()) { me.CountDrop(hub.Clock); await Saved(me, false, key, "rate"); return; }
        var clean = Armory.Clean(J.Obj(m, "char"), out var why, RepsEnabled);
        if (clean == null) { await Saved(me, false, key, why ?? "invalid"); return; }
        ArmoryStore.PutResult r; bool saved = true;
        lock (Store.Gate)
        {
            r = Store.Put(Identity(me), me.Name, key, clean);
            if (r == ArmoryStore.PutResult.Stored) saved = Store.SaveIfDirty(TimeSpan.FromSeconds(10));
        }
        if (r == ArmoryStore.PutResult.Full) { await Saved(me, false, key, "full"); return; }
        if (r == ArmoryStore.PutResult.Invalid) { await Saved(me, false, key, "invalid"); return; }
        if (!saved) { await Saved(me, false, key, "save"); return; }
        if (r == ArmoryStore.PutResult.Stored) hub.Log.LogInformation("{Name} shared a character with the armory", me.Name);
        await Saved(me, true, key, null, r == ArmoryStore.PutResult.Stored);
    }

    async Task Delete(Member me, JsonObject m)
    {
        if (!me.Personal || !Roles.Can(me.Role, Perm.ArmoryShare)) { await hub.Send(me, new JsonObject { ["t"] = LodgeMsg.ArmoryDeleted, ["ok"] = false, ["why"] = "guest" }); return; }
        bool all = J.Bool(m, "all");
        var key = J.Str(m, "key");
        if (!all && !Armory.ValidKey(key)) { await hub.Send(me, new JsonObject { ["t"] = LodgeMsg.ArmoryDeleted, ["ok"] = false, ["why"] = "invalid" }); return; }
        if (!me.Budget.Control.TryTake()) { me.CountDrop(hub.Clock); await hub.Send(me, new JsonObject { ["t"] = LodgeMsg.ArmoryDeleted, ["ok"] = false, ["why"] = "rate" }); return; }
        int n;
        lock (Store.Gate) n = Store.Delete(Identity(me), all ? null : key);   // always your own: the identity comes from the connection, never from the frame
        if (n > 0) hub.Log.LogInformation("{Name} took {Count} character(s) out of the armory", me.Name, n);
        var o = new JsonObject { ["t"] = LodgeMsg.ArmoryDeleted, ["ok"] = true, ["removed"] = n };
        if (!all) o["key"] = key;
        await hub.Send(me, o);
    }

    // a login shows as the member's display name (never anything from the game); a member without one shows the login name
    string Shown(string identity, string login)
    {
        var dn = hub.Profiles.Peek(identity)?.DisplayName;
        return string.IsNullOrEmpty(dn) ? (string.IsNullOrEmpty(login) ? identity : login) : dn;
    }

    bool Allowed(Member me) => me.Personal && Roles.Can(me.Role, Perm.ArmoryRead);

    async Task List(Member me)
    {
        if (!Enabled) { await hub.Error(me, "The Armory is switched off on this lodge", "disabled"); return; }
        if (!Allowed(me)) { await hub.Error(me, "The Armory is for lodge members", "guest"); return; }
        if (!me.Budget.Page.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return; }
        string json;
        lock (Store.Gate) json = new JsonObject { ["t"] = LodgeMsg.ArmoryChars, ["chars"] = Store.List(Shown, Identity(me)) }.ToJsonString();
        await hub.SendRaw(me, Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);
    }

    async Task Get(Member me, JsonObject m)
    {
        if (!Enabled) { await hub.Error(me, "The Armory is switched off on this lodge", "disabled"); return; }
        if (!Allowed(me)) { await hub.Error(me, "The Armory is for lodge members", "guest"); return; }
        if (!me.Budget.Page.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return; }
        var owner = J.Str(m, "owner"); var key = J.Str(m, "key");
        if (owner == null || owner.Length > 64 || !Armory.ValidKey(key)) { await hub.Error(me, "No such character", "notfound"); return; }
        string json = null;
        lock (Store.Gate)
        {
            var c = Store.Get(owner, key);
            if (c != null) json = new JsonObject { ["t"] = LodgeMsg.ArmoryChar, ["owner"] = owner, ["by"] = Shown(owner, Store.NameOf(owner)), ["key"] = key, ["char"] = (RepsEnabled ? c : Armory.WithoutReps(c)).DeepClone() }.ToJsonString();
        }
        if (json == null) { await hub.Error(me, "That character is not in the armory (any more)", "notfound"); return; }
        await hub.SendRaw(me, Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);
    }

    // armory2: who in the lodge can craft the recipe(s) - { spell } or { spells: [..] } -> armory.crafters { spells, who: [{spell, owner, by, key, name, class, level, profession, skill, max, partial, mine?}] }
    async Task Crafters(Member me, JsonObject m)
    {
        if (!Enabled) { await hub.Error(me, "The Armory is switched off on this lodge", "disabled"); return; }
        if (!Allowed(me)) { await hub.Error(me, "The Armory is for lodge members", "guest"); return; }
        if (!me.Budget.Page.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return; }
        var spells = new SortedSet<int>();
        void Add(JsonNode n) { if (spells.Count < Armory.MaxCrafterSpells && n is JsonValue v && v.TryGetValue<double>(out var d) && d >= 1 && d <= int.MaxValue && d == Math.Floor(d)) spells.Add((int)d); }
        if (m["spells"] is JsonArray sa) foreach (var n in sa) Add(n);
        Add(m["spell"]);
        if (spells.Count == 0) { await hub.Error(me, "No such recipe", "invalid"); return; }
        string json;
        lock (Store.Gate) json = new JsonObject { ["t"] = LodgeMsg.ArmoryCrafters, ["spells"] = new JsonArray(spells.Select(s => (JsonNode)JsonValue.Create(s)).ToArray()), ["who"] = Store.Crafters(spells, Shown, Identity(me)) }.ToJsonString();
        await hub.SendRaw(me, Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);
    }

    // reputations (2.27): where the lodge's shared characters stand with one faction - { faction } -> armory.standing { faction, who: [{faction, owner, by, key, name, class, level, s, v, min?, max?, at, mine?}] }
    // Only characters whose member switched reputation sharing on carry reputations at all; nothing else about them is in the answer.
    async Task Standing(Member me, JsonObject m)
    {
        if (!Enabled) { await hub.Error(me, "The Armory is switched off on this lodge", "disabled"); return; }
        if (!RepsEnabled) { await hub.Error(me, "Reputations are switched off on this lodge", "noreps"); return; }
        if (!Allowed(me)) { await hub.Error(me, "The Armory is for lodge members", "guest"); return; }
        if (!me.Budget.Page.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return; }
        int faction = m["faction"] is JsonValue fv && fv.TryGetValue<double>(out var d) && d >= 1 && d <= Armory.MaxFactionId && d == Math.Floor(d) ? (int)d : 0;
        if (faction == 0) { await hub.Error(me, "No such faction", "invalid"); return; }
        string json;
        lock (Store.Gate) json = new JsonObject { ["t"] = LodgeMsg.ArmoryStanding, ["faction"] = faction, ["who"] = Store.Standings(faction, Shown, Identity(me)) }.ToJsonString();
        await hub.SendRaw(me, Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);
    }

    // a member who left or was removed: their characters go (also called by the housekeeping sweep)
    public int RemoveMember(string login)
    {
        int n;
        lock (Store.Gate) n = Store.Delete(ProfileRules.Key(login), null);
        if (n > 0) hub.Log.LogInformation("{Count} armory character(s) of a member who left were removed", n);
        return n;
    }

    // housekeeping (every 15 s): a change that was not written at once reaches the disk; characters of people who are no longer members are dropped
    public void Tick()
    {
        var names = hub.Auth.PersonalNames.Select(ProfileRules.Key).ToHashSet(StringComparer.Ordinal);
        lock (Store.Gate)
        {
            if (names.Count > 0) Store.Prune(names.Contains);   // an unreadable codes file must never wipe the armory
            if (Store.Dirty) Store.Save();
        }
    }

    public void Flush()
    {
        lock (Store.Gate) if (Store.Dirty) Store.Save();
    }
}
