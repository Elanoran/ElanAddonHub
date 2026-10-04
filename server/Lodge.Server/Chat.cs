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
    public readonly PinStore Pins;

    // the fixed reaction set (canonical ids; anything else is ignored). 2.4: ids instead of emoji.
    public static readonly string[] ReactionSet = { "ready", "notready", "lol", "love", "fight", "loot", "wipe", "epic" };
    // a representative emoji per id: sent as "emoji" next to the id so 2.14 hubs still show something
    public static readonly string[] ReactionEmoji = { "✅", "❌", "😂", "❤️", "⚔️", "💰", "💀", "💎" };
    public const int MaxReactors = 30;      // distinct people per reaction on one message
    public const int SnippetMax = 80;

    public static string EmojiOf(string id) => ReactionEmoji[Array.IndexOf(ReactionSet, id)];

    // canonical id, or null when it isn't whitelisted. Legacy emoji from 2.14 hubs and old stored history map to ids
    // (a missing U+FE0F is accepted); the thumbs up of 2.3 becomes "ready".
    public static string CanonReaction(string e)
    {
        if (string.IsNullOrEmpty(e) || e.Length > 12) return null;
        if (Array.IndexOf(ReactionSet, e) >= 0) return e;
        switch (e.Replace("️", ""))
        {
            case "✅": case "👍": return "ready";
            case "❌": return "notready";
            case "😂": return "lol";
            case "❤": return "love";
            case "⚔": return "fight";
        }
        return null;
    }

    // rewrites legacy emoji keys of one record's "reactions" to ids (merged, deduped, capped); true when anything changed. Idempotent.
    public static bool MigrateReactions(JsonObject msg)
    {
        if (msg["reactions"] is not JsonObject old) return false;
        var merged = new List<KeyValuePair<string, List<string>>>();
        bool changed = false;
        foreach (var kv in old)
        {
            var id = CanonReaction(kv.Key);
            if (id == null) { changed = true; continue; }          // not in the set: dropped
            if (id != kv.Key) changed = true;
            var names = (kv.Value as JsonArray)?.Select(x => (string)x).Where(x => !string.IsNullOrEmpty(x)).ToList() ?? new List<string>();
            var slot = merged.FirstOrDefault(x => x.Key == id);
            if (slot.Value == null) { slot = new(id, new List<string>()); merged.Add(slot); }
            else changed = true;
            foreach (var n in names)
                if (!slot.Value.Any(x => Names.Same(x, n)) && slot.Value.Count < MaxReactors) slot.Value.Add(n);
        }
        if (!changed) return false;
        msg.Remove("reactions");
        var o = new JsonObject();
        foreach (var kv in merged) if (kv.Value.Count > 0) o[kv.Key] = new JsonArray(kv.Value.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
        if (o.Count > 0) msg["reactions"] = o;
        return true;
    }

    public ChatModule(LodgeHub hub)
    {
        this.hub = hub;
        Pins = new PinStore(hub.Cfg.DataDir, hub.Log);
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
            case "react": await React(me, m); return true;
            case "pin": await Pin(me, m, true); return true;
            case "unpin": await Pin(me, m, false); return true;
            case "typing":
                var ch = hub.Channels.Get((string)m["channel"]) ?? hub.Channels.DefaultText;
                if (!ch.VisibleTo(me.Role) || me.Invisible) return true; // invisible members never show as typing
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
        var replyId = m["replyTo"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : null;
        if (!string.IsNullOrEmpty(replyId))
        {
            // the quoted message must exist in THIS channel's history, otherwise the reply is sent without a quote
            var rstore = Store(ch.Id);
            JsonObject orig;
            lock (rstore) orig = rstore.Messages.FirstOrDefault(x => (string)x["id"] == replyId);
            if (orig != null)
            {
                var snippet = Names.Snippet((string)orig["text"], SnippetMax);
                if (snippet.Length == 0 && orig["file"] != null) snippet = Names.Snippet("[" + (string)orig["file"]!["name"] + "]", SnippetMax);
                var by = (string)orig["from"];
                // from/text: the 2.x fields; by/snippet: the 2.3 names for the same values
                msg["replyTo"] = new JsonObject { ["id"] = replyId, ["from"] = by, ["text"] = snippet, ["by"] = by, ["snippet"] = snippet };
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
        var pinned = Pins.UpdateText(ch.Id, (string)msg["id"], text);
        if (pinned != null) await hub.Broadcast(new JsonObject { ["t"] = "pin", ["channel"] = ch.Id, ["pin"] = pinned }, Viewers(ch));
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
        if (Pins.Unpin(ch.Id, (string)msg["id"], out _))
            await hub.Broadcast(new JsonObject { ["t"] = "unpin", ["channel"] = ch.Id, ["id"] = (string)msg["id"] }, Viewers(ch));
        if (!saved) await hub.Error(me, "The server couldn't save the delete");
    }

    // toggles the sender's reaction; the whole list for that reaction is broadcast (idempotent for clients)
    async Task React(Member me, JsonObject m)
    {
        var emoji = CanonReaction((string)m["emoji"]);
        if (emoji == null) return; // not in the fixed set: ignored (already paid for by the control budget)
        var (ch, s, msg) = FindMessage((string)m["id"]);
        if (msg == null || !ch.VisibleTo(me.Role)) return;
        bool on, full = false, saved = true;
        JsonArray users;
        lock (s)
        {
            var all = msg["reactions"] as JsonObject ?? new JsonObject();
            var list = all[emoji] as JsonArray ?? new JsonArray();
            var mine = list.FirstOrDefault(x => Names.Same((string)x, me.Name));
            if (mine != null) { list.Remove(mine); on = false; }
            else if (list.Count >= MaxReactors) { on = false; full = true; }
            else { list.Add(me.Name); on = true; }
            if (!full)
            {
                all.Remove(emoji);
                if (list.Count > 0) all[emoji] = list;
                if (all.Count > 0) msg["reactions"] = all; else msg.Remove("reactions");
                saved = s.Compact();
            }
            users = new JsonArray(list.Select(x => (JsonNode)JsonValue.Create((string)x)).ToArray());
        }
        if (full) { await hub.Error(me, "That reaction is full"); return; }
        await hub.Broadcast(new JsonObject { ["t"] = "react", ["channel"] = ch.Id, ["id"] = (string)msg["id"], ["reaction"] = emoji, ["emoji"] = EmojiOf(emoji), ["users"] = users, ["by"] = me.Name, ["on"] = on }, Viewers(ch));
        if (!saved) await hub.Error(me, "The server couldn't save that reaction");
    }

    async Task Pin(Member me, JsonObject m, bool pin)
    {
        if (!Roles.Can(me.Role, Perm.PinMessages)) { await hub.Error(me, "Only officers can pin messages"); return; }
        var id = (string)m["id"];
        if (string.IsNullOrEmpty(id)) return;
        var now = new DateTimeOffset(hub.Clock.UtcNow).ToUnixTimeMilliseconds();
        if (pin)
        {
            var (ch, _, msg) = FindMessage(id);
            if (msg == null || !ch.VisibleTo(me.Role)) return;
            var rec = new JsonObject
            {
                ["id"] = id, ["text"] = PinStore.Clip((string)msg["text"]), ["by"] = (string)msg["from"],
                ["at"] = (long?)msg["at"] ?? now, ["pinnedBy"] = me.Name, ["pinnedAt"] = now,
            };
            if (msg["file"] != null) rec["file"] = msg["file"]!.DeepClone();
            switch (Pins.Pin(ch.Id, rec))
            {
                case PinStore.Result.Full: await hub.Error(me, $"This channel already has {PinStore.MaxPerChannel} pinned messages - unpin one first"); return;
                case PinStore.Result.Already: return;
                case PinStore.Result.SaveFailed: await hub.Error(me, "The server couldn't save the pin - it may be gone after a restart"); break;
            }
            await hub.Broadcast(new JsonObject { ["t"] = "pin", ["channel"] = ch.Id, ["pin"] = rec }, Viewers(ch));
        }
        else
        {
            // the pin may outlive its message, so look it up by pin id in the channels this person can see
            var want = (string)m["channel"];
            var ch = hub.Channels.All.FirstOrDefault(c => c.Type == "text" && c.VisibleTo(me.Role) && Pins.Has(c.Id, id) && (want == null || c.Id == want));
            if (ch == null) return;
            if (Pins.Unpin(ch.Id, id, out var saved))
            {
                await hub.Broadcast(new JsonObject { ["t"] = "unpin", ["channel"] = ch.Id, ["id"] = id }, Viewers(ch));
                if (!saved) await hub.Error(me, "The server couldn't save the change");
            }
        }
    }

    public void DropChannel(string id)
    {
        stores.TryRemove(id, out _);
        Pins.DropChannel(id);
        // keep the file (renamed) in case it was removed by mistake
        var f = HistoryStore.FileOf(dir, id);
        if (File.Exists(f)) File.Move(f, f + $".removed-{DateTime.UtcNow:yyyyMMddHHmmss}");
    }
}
