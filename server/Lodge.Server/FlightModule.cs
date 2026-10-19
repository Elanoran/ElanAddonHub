using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// Flight-time sharing (2.13). Client to server: flights.share { legs: { key: {t,n} }, names?: { id: name } } (up to 500 legs,
// members and up, one per 10 minutes per identity) and flights.get {} (merged table, Page budget).
// Server to client: flights.shared { ok, accepted, rejected, why? } and flights.data { at, legs, names }.
// Guests may read but not contribute. Nothing the clients send is ever logged - only counts.
public sealed class FlightModule : IModule
{
    readonly LodgeHub hub;
    public readonly FlightStore Store;
    public readonly FlightRouteStore Routes;       // learned flight paths (FlightRoutes.cs), feature "flightroutes"

    public FlightModule(LodgeHub hub)
    {
        this.hub = hub;
        Store = new FlightStore(hub.Cfg.DataDir, hub.Log, hub.Clock);
        Routes = new FlightRouteStore(hub.Cfg.DataDir, hub.Log, hub.Clock);
    }

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        switch (type)
        {
            case LodgeMsg.FlightsShare: await Share(me, m); return true;
            case LodgeMsg.FlightsGet: await Get(me); return true;
            case LodgeMsg.FlightsRoutesShare: await ShareRoutes(me, m); return true;
            case LodgeMsg.FlightsRoutesGet: await GetRoutes(me); return true;
        }
        return false;
    }

    static string Identity(Member me) => ProfileRules.Key(me.Name);
    static string Identity(string login) => ProfileRules.Key(login);

    Task Reply(Member me, bool ok, int accepted, int rejected, string why = null)
    {
        var o = new JsonObject { ["t"] = LodgeMsg.FlightsShared, ["ok"] = ok, ["accepted"] = accepted, ["rejected"] = rejected };
        if (why != null) o["why"] = why;
        return hub.Send(me, o);
    }

    async Task Share(Member me, JsonObject m)
    {
        if (!me.Personal || !Roles.Can(me.Role, Perm.FlightShare)) { await Reply(me, false, 0, 0, "guest"); return; }
        if (m["legs"] is not JsonObject legs || legs.Count == 0) { await Reply(me, false, 0, 0, "empty"); return; }
        if (!me.Budget.FlightShare.TryTake()) { me.CountDrop(hub.Clock); await Reply(me, false, 0, 0, "rate"); return; }
        FlightStore.ShareResult r; bool saved = true;
        lock (Store.Gate)
        {
            r = Store.Share(Identity(me), legs, m["names"] as JsonObject);
            if (r.Changed) { Store.Prune(); saved = Store.Save(); }
        }
        if (!saved) { await Reply(me, false, 0, 0, "save"); return; }
        if (r.Changed) hub.Log.LogInformation("{Name} shared flight times ({Accepted} stored, {Rejected} refused)", me.Name, r.Accepted, r.Rejected);
        await Reply(me, true, r.Accepted + r.Unchanged, r.Rejected);
    }

    async Task Get(Member me)
    {
        if (!me.Budget.Page.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return; }
        string json;
        lock (Store.Gate) json = Store.MergedJson();
        await hub.SendRaw(me, Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);
    }

    // learned flight paths. flights.routes.share { routes: { "<from>><to>": { m, p, t, n } | { m, g: 1 } } } (member and up, at most 100 per message,
    // one per 10 minutes per identity) -> flights.routes.shared { ok, accepted, rejected, why? }; flights.routes.get {} -> flights.routes.data (Page budget).
    Task ReplyRoutes(Member me, bool ok, int accepted, int rejected, string why = null)
    {
        var o = new JsonObject { ["t"] = LodgeMsg.FlightsRoutesShared, ["ok"] = ok, ["accepted"] = accepted, ["rejected"] = rejected };
        if (why != null) o["why"] = why;
        return hub.Send(me, o);
    }

    async Task ShareRoutes(Member me, JsonObject m)
    {
        if (!me.Personal || !Roles.Can(me.Role, Perm.FlightShare)) { await ReplyRoutes(me, false, 0, 0, "guest"); return; }
        if (m["routes"] is not JsonObject routes || routes.Count == 0) { await ReplyRoutes(me, false, 0, 0, "empty"); return; }
        FlightRouteStore.ShareResult r; bool saved = true, slot;
        lock (Routes.Gate)
        {
            slot = Routes.TakeSlot(Identity(me));
            r = slot ? Routes.Share(Identity(me), routes) : null;
            if (r != null && r.Changed) { Routes.Prune(); saved = Routes.Save(); }
        }
        if (!slot) { me.CountDrop(hub.Clock); await ReplyRoutes(me, false, 0, 0, "rate"); return; }
        if (!saved) { await ReplyRoutes(me, false, 0, 0, "save"); return; }
        if (r.Changed) hub.Log.LogInformation("{Name} shared learned flight routes ({Accepted} stored, {Rejected} refused)", me.Name, r.Accepted, r.Rejected);
        await ReplyRoutes(me, true, r.Accepted + r.Unchanged, r.Rejected);
    }

    async Task GetRoutes(Member me)
    {
        if (!me.Budget.Page.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return; }
        string json;
        lock (Routes.Gate) json = Routes.MergedJson();
        await hub.SendRaw(me, Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);
    }

    // 2.22: a member who left / was removed: their flight times go (and their learned routes)
    public int Purge(string login)
    {
        int n;
        lock (Store.Gate)
        {
            n = Store.RemoveIdentity(Identity(login));
            if (n > 0) Store.Save();
        }
        lock (Routes.Gate)
        {
            int k = Routes.RemoveIdentity(Identity(login));
            if (k > 0) Routes.Save();
            n += k;
        }
        return n;
    }

    // housekeeping (every 15 s): forget contributions after 120 days
    public void Tick()
    {
        lock (Store.Gate) if (Store.Prune() > 0) Store.Save();
        lock (Routes.Gate) if (Routes.Prune() > 0) Routes.Save();
    }
}
