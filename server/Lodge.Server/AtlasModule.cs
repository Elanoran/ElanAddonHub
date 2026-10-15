using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Lodge;

// Atlas pool (2.15). Client to server: atlas.share { kills, objects, fishing, vendors, quests, finds, spawns, items, seenNpcs, seenObjects, seenQuests (feature atlas2), nodes (feature atlas3), fishspots (feature atlas4), fieldnotes (feature atlas5), entrances (feature atlas6) } (cumulative per-member
// totals of what changed; members and up; at most 3 in a burst, then one per 2 minutes per identity; frame limit 64 KB) and atlas.get { since, epoch }
// (everybody, guests included; Page budget). Server to client: atlas.shared { ok, accepted, unchanged, rejected, rev, epoch, why? } and
// atlas.data (see AtlasStore.Get). Feature off (LODGE_ATLAS_ENABLED=0): not advertised, share answers why "disabled", get answers an error.
// Nothing the clients send is ever logged - only counts.
public sealed class AtlasModule : IModule
{
    readonly LodgeHub hub;
    public readonly AtlasStore Store;

    public AtlasModule(LodgeHub hub)
    {
        this.hub = hub;
        Store = new AtlasStore(hub.Cfg.DataDir, hub.Log, hub.Clock);
    }

    public bool Enabled => hub.Cfg.AtlasEnabled;

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        switch (type)
        {
            case "atlas.share": await Share(me, m); return true;
            case "atlas.get": await Get(me, m); return true;
        }
        return false;
    }

    static string Identity(Member me) => ProfileRules.Key(me.Name);

    Task Reply(Member me, bool ok, AtlasStore.ShareResult r, string why = null)
    {
        var o = new JsonObject
        {
            ["t"] = "atlas.shared", ["ok"] = ok, ["accepted"] = r?.Accepted ?? 0, ["unchanged"] = r?.Unchanged ?? 0, ["rejected"] = r?.Rejected ?? 0,
            ["rev"] = Store.Rev, ["epoch"] = Store.Epoch,
        };
        if (why != null) o["why"] = why;
        return hub.Send(me, o);
    }

    async Task Share(Member me, JsonObject m)
    {
        if (!Enabled) { await Reply(me, false, null, "disabled"); return; }
        if (!me.Personal || !Roles.Can(me.Role, Perm.AtlasShare)) { await Reply(me, false, null, "guest"); return; }
        if (!AtlasStore.HasContent(m)) { await Reply(me, false, null, "empty"); return; }
        if (!me.Budget.AtlasShare.TryTake()) { me.CountDrop(hub.Clock); await Reply(me, false, null, "rate"); return; }
        AtlasStore.ShareResult r; bool saved = true;
        lock (Store.Gate)
        {
            r = Store.Share(Identity(me), me.Name, m);
            if (r.Changed) saved = Store.SaveIfDirty(TimeSpan.FromSeconds(30));
        }
        if (r.Full) { hub.Log.LogWarning("Atlas: a share from a member was refused - the pool is at its size cap"); await Reply(me, false, r, "full"); return; }
        if (!saved) { await Reply(me, false, r, "save"); return; }
        if (r.Changed) hub.Log.LogInformation("{Name} shared atlas observations ({Accepted} stored, {Rejected} refused)", me.Name, r.Accepted, r.Rejected);
        await Reply(me, true, r);
    }

    // 2.22: everything one member put into the pool is removed (member left / was removed / the owner reverts them). Saved at once.
    public AtlasStore.PurgeResult Purge(string login)
    {
        AtlasStore.PurgeResult r;
        lock (Store.Gate)
        {
            r = Store.Purge(ProfileRules.Key(login));
            if (Store.Dirty) Store.Save();
        }
        if (r.Total > 0) hub.Log.LogInformation("Atlas: removed {Count} contribution(s) of one member", r.Total);
        return r;
    }

    // a find shows the member's display name (never anything from the game); a member without one shows the login name
    string Shown(string login)
    {
        var dn = hub.Profiles.Peek(login)?.DisplayName;
        return string.IsNullOrEmpty(dn) ? login : dn;
    }

    async Task Get(Member me, JsonObject m)
    {
        if (!Enabled) { await hub.Error(me, "The Atlas is switched off on this lodge", "disabled"); return; }
        if (!me.Budget.Page.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return; }
        long since = 0;
        if (m["since"] is JsonValue sv && sv.TryGetValue<long>(out var s) && s > 0) since = s;
        var epoch = J.Str(m, "epoch");
        string json;
        lock (Store.Gate) json = Store.Get(since, epoch, Shown, guest: !me.Personal).ToJsonString();
        await hub.SendRaw(me, Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);
    }

    // housekeeping (every 15 s): a change that was not written at once reaches the disk
    public void Tick()
    {
        lock (Store.Gate) if (Store.Dirty) Store.Save();
    }

    public void Flush()
    {
        lock (Store.Gate) if (Store.Dirty) Store.Save();
    }
}
