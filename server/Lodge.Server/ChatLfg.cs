using System.Text.Json.Nodes;

namespace Lodge;

// Sign-up card actions (see Lfg for the rules). Every change happens under the store lock, so two people taking the
// last slot at the same moment can't both get it. Join/leave/close/cancel are "control" messages: they spend the
// per-identity Control budget in the hub's receive loop; creating a card is an ordinary "msg" and spends the Chat budget.
public partial class ChatModule
{
    // finds the card; null when it doesn't exist or the channel is not visible to `me` (same answer for both)
    (Channel, HistoryStore, JsonObject) FindCard(Member me, JsonObject m)
    {
        var (ch, s, msg) = FindMessage(J.Str(m, "id"));
        if (msg == null || !ch.VisibleTo(me.Role)) return (null, null, null);
        return (ch, s, msg);
    }

    async Task LfgJoin(Member me, JsonObject m, string role)
    {
        var (ch, s, msg) = FindCard(me, m);
        if (msg == null || !Lfg.Is(msg)) return;
        Lfg.Result r;
        lock (s) r = Lfg.Join((JsonObject)msg["lfg"], me.Name, role);
        await Finish(me, ch, s, msg, r, "That role is full");
    }

    async Task LfgLeave(Member me, JsonObject m)
    {
        var (ch, s, msg) = FindCard(me, m);
        if (msg == null || !Lfg.Is(msg)) return;
        Lfg.Result r;
        lock (s) r = Lfg.Leave((JsonObject)msg["lfg"], me.Name);
        await Finish(me, ch, s, msg, r, null);
    }

    // the author or an officer may close (the group is set) or cancel (it won't happen) a card
    async Task LfgEnd(Member me, JsonObject m, string status)
    {
        var (ch, s, msg) = FindCard(me, m);
        if (msg == null || !Lfg.Is(msg)) return;
        if (!IsAuthor(me, msg) && !Roles.Can(me.Role, Perm.Moderate)) { await hub.Error(me, "Only the organiser or an officer can do that", "lfg"); return; }
        Lfg.Result r;
        lock (s) r = Lfg.SetStatus((JsonObject)msg["lfg"], status);
        await Finish(me, ch, s, msg, r, null);
    }

    async Task Finish(Member me, Channel ch, HistoryStore s, JsonObject msg, Lfg.Result r, string fullText)
    {
        switch (r)
        {
            case Lfg.Result.Closed: await hub.Error(me, "That sign-up is closed", "lfg"); return;
            case Lfg.Result.Full: await hub.Error(me, fullText ?? "No room left", "lfg-full"); return;
            case Lfg.Result.Denied: await hub.Error(me, "Pick Tank, Healer, DPS or Maybe", "lfg"); return;
            case Lfg.Result.Unchanged: return;
        }
        JsonObject lfg; string text;
        lock (s)
        {
            var live = (JsonObject)msg["lfg"];
            text = Lfg.Fallback(live);
            msg["text"] = text; // older clients and search see the current state after a reload
            lfg = live.DeepClone().AsObject();
            s.MarkDirty();
        }
        QueueFlush(s);
        await hub.Broadcast(new JsonObject { ["t"] = "lfg", ["channel"] = ch.Id, ["id"] = (string)msg["id"], ["lfg"] = lfg, ["text"] = text }, Viewers(ch));
    }
}
