using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// The chat card of a calendar event: an ordinary message with kind = "event" and a small snapshot in "event" (the Outpost draws
// the live event from its calendar by id and uses the snapshot only when it has no copy). "text" is the plain-text fallback
// older clients, search, pins and reply quotes show; it follows the event (fill, cancelled) through a normal "edited" frame.
public partial class ChatModule
{
    public static JsonObject EventSnapshot(JsonObject ev) => LodgeFieldsJson.Build(EventCard(ev).Write);

    static CalCard EventCard(JsonObject ev) => new CalCard
    {
        Id = J.Str(ev, "id"), Title = J.Str(ev, "title"), Start = Cal.Start(ev), End = Cal.End(ev),
        By = J.Str(ev, "by"), Status = J.Str(ev, "status") ?? "open", Weekly = ev["rep"] is JsonObject,
    };

    // returns the message id, or null when it could not be stored
    public async Task<string> PostEventCard(Member me, Channel ch, JsonObject ev)
    {
        var card = new ChatMessage
        {
            Channel = ch.Id, Id = Guid.NewGuid().ToString("N")[..12], At = new DateTimeOffset(hub.Clock.UtcNow).ToUnixTimeMilliseconds(),
            From = me.Name, FromId = me.Id, Text = Cal.Fallback(ev), Kind = Cal.Kind, Event = EventCard(ev),
        };
        var msg = LodgeFieldsJson.Build(card.Write);
        var s = Store(ch.Id);
        lock (s) s.Append(msg);
        await hub.Broadcast(msg, Viewers(ch));
        return card.Id;
    }

    // the event changed: keep its card's snapshot and fallback text in step
    public async Task RefreshEventCard(JsonObject ev)
    {
        var chId = J.Str(ev, "channel"); var id = J.Str(ev, "msg");
        if (chId == null || id == null) return;
        var ch = hub.Channels.Get(chId);
        if (ch == null || ch.Type != "text") return;
        var s = Store(ch.Id);
        string text;
        lock (s)
        {
            var msg = s.Messages.FirstOrDefault(x => (string)x["id"] == id);
            if (msg == null || (string)msg["kind"] != Cal.Kind) return;
            text = Cal.Fallback(ev);
            msg["text"] = text;
            msg["event"] = EventSnapshot(ev);
            s.MarkDirty();
        }
        QueueFlush(s);
        await hub.Broadcast(LodgeFieldsJson.Build(new EditedMsg { Channel = ch.Id, Id = id, Text = text, Card = true }.Write), Viewers(ch));
    }
}
