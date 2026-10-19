using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// Sign-up card actions (see Lfg for the rules). Every change happens under the store lock, so two people taking the
// last slot at the same moment can't both get it. Join/leave/close/cancel are "control" messages: they spend the
// per-identity Control budget in the hub's receive loop; creating a card is an ordinary "msg" and spends the Chat budget.
public partial class ChatModule
{
    // finds the card; null when it doesn't exist or the channel is not visible to `me` (same answer for both)
    (Channel, HistoryStore, JsonObject) FindCard(Member me, string id)
    {
        var (ch, s, msg) = FindMessage(id);
        if (msg == null || !ch.VisibleTo(me.Role)) return (null, null, null);
        return (ch, s, msg);
    }

    // Every read and write of a card's JSON happens under the store lock: JsonObject is not safe to read while another thread sets a property
    // (two people tapping Join at the same moment used to be able to throw "Collection was modified" from Lfg.Is).
    async Task LfgJoin(Member me, string id, string role)
    {
        var (ch, s, msg) = FindCard(me, id);
        if (msg == null) return;
        Lfg.Result r; bool card;
        lock (s) { card = Lfg.Is(msg); r = card ? Lfg.Join((JsonObject)msg["lfg"], me.Name, role) : Lfg.Result.Unchanged; }
        if (!card) return;
        await Finish(me, ch, s, msg, r, "That role is full");
    }

    async Task LfgLeave(Member me, string id)
    {
        var (ch, s, msg) = FindCard(me, id);
        if (msg == null) return;
        Lfg.Result r; bool card;
        lock (s) { card = Lfg.Is(msg); r = card ? Lfg.Leave((JsonObject)msg["lfg"], me.Name) : Lfg.Result.Unchanged; }
        if (!card) return;
        await Finish(me, ch, s, msg, r, null);
    }

    // the author or an officer may close (the group is set) or cancel (it won't happen) a card
    async Task LfgEnd(Member me, string id, string status)
    {
        var (ch, s, msg) = FindCard(me, id);
        if (msg == null) return;
        Lfg.Result r = Lfg.Result.Unchanged; bool card, allowed = true;
        lock (s)
        {
            card = Lfg.Is(msg);
            if (card)
            {
                allowed = IsAuthor(me, msg) || Roles.Can(me.Role, Perm.Moderate);
                if (allowed) r = Lfg.SetStatus((JsonObject)msg["lfg"], status);
            }
        }
        if (!card) return;
        if (!allowed) { await hub.Error(me, "Only the organiser or an officer can do that", "lfg"); return; }
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
        var update = new LfgUpdateMsg { Channel = ch.Id };
        lock (s)
        {
            var live = (JsonObject)msg["lfg"];
            update.Text = Lfg.Fallback(live);
            msg["text"] = update.Text; // older clients and search see the current state after a reload
            update.Id = (string)msg["id"];
            update.Card = LfgCard.Read(new LodgeFieldsJson(live)); // a snapshot, taken under the lock
            s.MarkDirty();
        }
        QueueFlush(s);
        await hub.Broadcast(LodgeFieldsJson.Build(update.Write), Viewers(ch));
    }
}
