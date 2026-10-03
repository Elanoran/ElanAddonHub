using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Lodge;

// Text channels: messages, replies, edits, deletes, typing. History per channel in data/history/<channel>.jsonl
public class ChatModule : IModule
{
    const int Keep = 200;       // per channel, on disk and in memory
    const int SendOnJoin = 100; // per channel, in the welcome

    readonly LodgeHub hub;
    readonly string dir;
    readonly ConcurrentDictionary<string, List<JsonObject>> history = new();

    public ChatModule(LodgeHub hub)
    {
        this.hub = hub;
        dir = Path.Combine(hub.Cfg.DataDir, "history");
        Directory.CreateDirectory(dir);
        // 1.x kept one history: it becomes the default channel's
        var old = Path.Combine(hub.Cfg.DataDir, "history.jsonl");
        var target = Path.Combine(dir, hub.Channels.DefaultText.Id + ".jsonl");
        if (File.Exists(old) && !File.Exists(target)) File.Move(old, target);
    }

    List<JsonObject> List(string channel) => history.GetOrAdd(channel, ch =>
    {
        var l = new List<JsonObject>();
        var file = Path.Combine(dir, ch + ".jsonl");
        if (!File.Exists(file)) return l;
        foreach (var line in File.ReadAllLines(file).Where(x => x.Length > 0).TakeLast(Keep))
            try
            {
                var m = JsonNode.Parse(line)!.AsObject();
                m["channel"] = ch;
                l.Add(m);
            }
            catch { }
        return l;
    });

    public JsonArray HistoryFor(string channel)
    {
        var l = List(channel);
        lock (l) return new JsonArray(l.TakeLast(SendOnJoin).Select(m => (JsonNode)m.DeepClone()).ToArray());
    }

    void Persist(string channel, List<JsonObject> l)
    {
        try { File.WriteAllLines(Path.Combine(dir, channel + ".jsonl"), l.Select(m => m.ToJsonString())); } catch { }
    }

    Func<Member, bool> Viewers(Channel ch) => m => ch.VisibleTo(m.Role);

    (Channel, List<JsonObject>, JsonObject) FindMessage(string id)
    {
        foreach (var ch in hub.Channels.All.Where(c => c.Type == "text"))
        {
            var l = List(ch.Id);
            lock (l)
            {
                var m = l.FirstOrDefault(x => (string)x["id"] == id);
                if (m != null) return (ch, l, m);
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
                if (ch.VisibleTo(me.Role))
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

        // max 8 messages per 10 seconds
        var now = DateTime.UtcNow;
        while (me.Recent.Count > 0 && now - me.Recent.Peek() > TimeSpan.FromSeconds(10)) me.Recent.Dequeue();
        if (me.Recent.Count >= 8) { await hub.Error(me, "Slow down a little"); return; }
        me.Recent.Enqueue(now);

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
            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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

        var l = List(ch.Id);
        lock (l)
        {
            l.Add(msg);
            if (l.Count > Keep) l.RemoveRange(0, l.Count - Keep);
            try { File.AppendAllText(Path.Combine(dir, ch.Id + ".jsonl"), msg.ToJsonString() + "\n"); } catch { }
        }
        await hub.Broadcast(msg, Viewers(ch));
    }

    async Task Edit(Member me, JsonObject m)
    {
        var (ch, l, msg) = FindMessage((string)m["id"]);
        if (msg == null) return;
        if (!LodgeHub.Same((string)msg["from"], me.Name)) { await hub.Error(me, "You can only edit your own messages"); return; }
        var text = ((string)m["text"] ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > 2000) text = text[..2000];
        lock (l) { msg["text"] = text; msg["edited"] = true; Persist(ch.Id, l); }
        await hub.Broadcast(new JsonObject { ["t"] = "edited", ["channel"] = ch.Id, ["id"] = (string)msg["id"], ["text"] = text }, Viewers(ch));
    }

    async Task Delete(Member me, JsonObject m)
    {
        var (ch, l, msg) = FindMessage((string)m["id"]);
        if (msg == null) return;
        if (!LodgeHub.Same((string)msg["from"], me.Name) && !Roles.Can(me.Role, Perm.Moderate))
        {
            await hub.Error(me, "Only officers can delete other people's messages");
            return;
        }
        lock (l) { l.Remove(msg); Persist(ch.Id, l); }
        hub.Log.LogInformation("{Name} deleted a message from {From} in {Channel}", me.Name, (string)msg["from"], ch.Id);
        await hub.Broadcast(new JsonObject { ["t"] = "deleted", ["channel"] = ch.Id, ["id"] = (string)msg["id"] }, Viewers(ch));
    }

    public void DropChannel(string id)
    {
        history.TryRemove(id, out _);
        // keep the file (renamed) in case it was removed by mistake
        var f = Path.Combine(dir, id + ".jsonl");
        if (File.Exists(f)) File.Move(f, f + $".removed-{DateTime.UtcNow:yyyyMMddHHmmss}");
    }
}
