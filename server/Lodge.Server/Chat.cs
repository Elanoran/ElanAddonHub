using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Lodge;

// Text channels: messages, replies, edits, deletes, typing. History per channel: see HistoryStore.
public class ChatModule : IModule
{
    const int SendOnJoin = 100; // per channel, in the welcome
    static readonly TimeSpan TypingEvery = TimeSpan.FromSeconds(2);

    readonly LodgeHub hub;
    readonly string dir;
    readonly ConcurrentDictionary<string, HistoryStore> stores = new();

    public ChatModule(LodgeHub hub)
    {
        this.hub = hub;
        dir = Path.Combine(hub.Cfg.DataDir, "history");
        Directory.CreateDirectory(dir);
        // 1.x kept one history: it becomes the default channel's
        var old = Path.Combine(hub.Cfg.DataDir, "history.jsonl");
        var target = HistoryStore.FileOf(dir, hub.Channels.DefaultText.Id);
        if (File.Exists(old) && !File.Exists(target)) File.Move(old, target);
    }

    HistoryStore Store(string channel) => stores.GetOrAdd(channel, ch => new HistoryStore(dir, ch, hub.Cfg.HistoryMaxBytes, hub.Log));

    public JsonArray HistoryFor(string channel)
    {
        var s = Store(channel);
        lock (s) return new JsonArray(s.Messages.TakeLast(SendOnJoin).Select(m => (JsonNode)m.DeepClone()).ToArray());
    }

    Func<Member, bool> Viewers(Channel ch) => m => ch.VisibleTo(m.Role);

    (Channel, HistoryStore, JsonObject) FindMessage(string id)
    {
        if (string.IsNullOrEmpty(id)) return (null, null, null);
        foreach (var ch in hub.Channels.All.Where(c => c.Type == "text"))
        {
            var s = Store(ch.Id);
            lock (s)
            {
                var m = s.Messages.FirstOrDefault(x => (string)x["id"] == id);
                if (m != null) return (ch, s, m);
            }
        }
        return (null, null, null);
    }

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        switch (type)
        {
            case "msg": await Message(me, m); return true;
            case "edit": await Edit(me, m); return true;
            case "delete": await Delete(me, m); return true;
            case "typing":
                var ch = hub.Channels.Get((string)m["channel"]) ?? hub.Channels.DefaultText;
                if (!ch.VisibleTo(me.Role)) return true;
                // coalesced: at most one "typing" per person and channel every 2 s
                var now = hub.Clock.UtcNow;
                if (me.LastTyping.TryGetValue(ch.Id, out var last) && now - last < TypingEvery) return true;
                me.LastTyping[ch.Id] = now;
                await hub.Broadcast(new JsonObject { ["t"] = "typing", ["id"] = me.Id, ["channel"] = ch.Id },
                    x => x.Id != me.Id && ch.VisibleTo(x.Role));
                return true;
        }
        return false;
    }

    async Task Message(Member me, JsonObject m)
    {
        var ch = hub.Channels.Get((string)m["channel"]) ?? hub.Channels.DefaultText; // old hubs send no channel
        if (ch.Type != "text" || !ch.VisibleTo(me.Role)) { await hub.Error(me, "You can't write in that channel"); return; }

        // 8 messages per 10 s, per person - kept across reconnects (guests: per IP)
        if (!me.Budget.Chat.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little"); return; }

        var text = ((string)m["text"] ?? "").Trim();
        if (text.Length > 2000) text = text[..2000];
        JsonObject file = null;
        var fileId = (string)m["file"]?["id"];
        if (fileId != null && Roles.Can(me.Role, Perm.ShareFiles))
        {
            var meta = hub.Files.Get(fileId);
            if (meta != null) file = new JsonObject { ["id"] = meta.Id, ["name"] = meta.Name, ["size"] = meta.Size, ["mime"] = meta.Mime };
        }
        if (text.Length == 0 && file == null) return;

        var msg = new JsonObject
        {
            ["t"] = "msg", ["channel"] = ch.Id,
            ["id"] = Guid.NewGuid().ToString("N")[..12],
            ["at"] = new DateTimeOffset(hub.Clock.UtcNow).ToUnixTimeMilliseconds(),
            ["from"] = me.Name, ["fromId"] = me.Id, ["text"] = text,
        };
        if (file != null) msg["file"] = file;
        var replyId = (string)m["replyTo"];
        if (replyId != null)
        {
            var (_, _, orig) = FindMessage(replyId);
            if (orig != null)
            {
                var snippet = (string)orig["text"] ?? "";
                if (snippet.Length == 0 && orig["file"] != null) snippet = "[" + (string)orig["file"]!["name"] + "]";
                msg["replyTo"] = new JsonObject { ["id"] = replyId, ["from"] = (string)orig["from"], ["text"] = snippet.Length > 90 ? snippet[..90] + "..." : snippet };
            }
        }

        var s = Store(ch.Id);
        bool saved;
        lock (s) saved = s.Append(msg);
        await hub.Broadcast(msg, Viewers(ch));
        if (!saved) await hub.Error(me, "The server couldn't save that message - it was delivered but may be gone after a restart");
    }

    async Task Edit(Member me, JsonObject m)
    {
        var (ch, s, msg) = FindMessage((string)m["id"]);
        if (msg == null) return;
        if (!Names.Same((string)msg["from"], me.Name)) { await hub.Error(me, "You can only edit your own messages"); return; }
        var text = ((string)m["text"] ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > 2000) text = text[..2000];
        bool saved;
        lock (s) { msg["text"] = text; msg["edited"] = true; saved = s.Compact(); }
        await hub.Broadcast(new JsonObject { ["t"] = "edited", ["channel"] = ch.Id, ["id"] = (string)msg["id"], ["text"] = text }, Viewers(ch));
        if (!saved) await hub.Error(me, "The server couldn't save the edit");
    }

    async Task Delete(Member me, JsonObject m)
    {
        var (ch, s, msg) = FindMessage((string)m["id"]);
        if (msg == null) return;
        if (!Names.Same((string)msg["from"], me.Name) && !Roles.Can(me.Role, Perm.Moderate))
        {
            await hub.Error(me, "Only officers can delete other people's messages");
            return;
        }
        bool saved;
        lock (s) { s.Messages.Remove(msg); saved = s.Compact(); }
        hub.Log.LogInformation("{Name} deleted a message from {From} in {Channel}", me.Name, Names.ForLog((string)msg["from"]), ch.Id);
        await hub.Broadcast(new JsonObject { ["t"] = "deleted", ["channel"] = ch.Id, ["id"] = (string)msg["id"] }, Viewers(ch));
        if (!saved) await hub.Error(me, "The server couldn't save the delete");
    }

    public void DropChannel(string id)
    {
        stores.TryRemove(id, out _);
        // keep the file (renamed) in case it was removed by mistake
        var f = HistoryStore.FileOf(dir, id);
        if (File.Exists(f)) File.Move(f, f + $".removed-{DateTime.UtcNow:yyyyMMddHHmmss}");
    }
}
