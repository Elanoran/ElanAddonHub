using System.Collections.Generic;

namespace Elans.Shared
{
    // Chat messages (server/PROTOCOL.md: WebSocket, 2.3, 2.9 and 2.11 additions): the field names live only here. The classes only map
    // fields; every rule stays in the Lodge server (Chat.cs, ChatLfg.cs, ChatCalendar.cs, Pins.cs): which channels you may write in, the
    // chat budget, the text cleaning (Names.MessageText: bidi overrides / isolates and control characters out, blank-line runs collapsed),
    // the 2000-character cut, @everyone / @here defanged below officer, the reply quote (looked up in the same channel, Names.Snippet, 80
    // characters), who may edit / delete / pin, the reaction set and its 30-person cap, the pin text clip (300) and the 25 pins per channel.
    // The Outpost cleans what it shows again (SafeFile.Text / SafeFile.Name) - an older or hostile Lodge may send anything.
    // The stored message IS the msg frame (data/history/<channel>.jsonl holds it as broadcast): ChatMessage is its one view, for the live
    // frame, the welcome's history and history:page. Edits, reactions, sign-ups, purges change the stored object in place on the server.
    // The server still validates the lfg request of a msg from the raw JSON (Lfg.Create: its strict whole-number counts and start time are
    // the validation); LfgRequest is what the Outpost sends.

    // ---------------------------------------------------------------- client -> server

    // a new message: { channel, kind?, text, replyTo?, file?: { id }, lfg? } - kind "lfg" with an lfg request makes a sign-up card
    // (the text is then replaced by the card's plain-text fallback). channel null = the default text channel (older clients send none).
    public sealed class ChatSendMsg
    {
        public const string Type = LodgeMsg.Msg;
        public string Channel;         // written as JSON null when null
        public string Kind;            // null = an ordinary message (not sent)
        public string Text;
        public string ReplyTo;         // the quoted message's id; null = not sent
        public string FileId;          // an uploaded shared file to attach; null = not sent
        public LfgRequest Lfg;         // written when set; read: the lenient view
        public object LfgRaw;          // read side: the lfg object as received, a raw JsonObject (Lfg.Create validates it)

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("channel", Channel);
            if (Kind != null) w.Set("kind", Kind);
            w.Set("text", Text);
            if (ReplyTo != null) w.Set("replyTo", ReplyTo);
            if (FileId != null) w.Child("file").Set("id", FileId);
            if (Lfg != null) Lfg.Write(w.Child("lfg"));
        }

        public static ChatSendMsg Read(ILodgeFields f)
        {
            var lfg = f.Child("lfg");
            return new ChatSendMsg
            {
                Channel = f.Str("channel"), Kind = f.Str("kind"), Text = f.Str("text"), ReplyTo = f.Str("replyTo"), FileId = f.Child("file")?.Str("id"),
                Lfg = lfg == null ? null : LfgRequest.Read(lfg), LfgRaw = f.RawChild("lfg"),
            };
        }
    }

    // the sign-up card a msg of kind "lfg" asks for: { title, now, note, slots: { tank, healer, dps }, start? (unix ms; not sent with now) }
    public sealed class LfgRequest
    {
        public string Title, Note;
        public bool Now;
        public int Tank, Healer, Dps;
        public long? Start;

        public void Write(ILodgeWriter w)
        {
            w.Set("title", Title); w.Set("now", Now); w.Set("note", Note);
            var s = w.Child("slots"); s.Set("tank", Tank); s.Set("healer", Healer); s.Set("dps", Dps);
            if (Start is long st) w.Set("start", st);
        }

        public static LfgRequest Read(ILodgeFields f)
        {
            var s = f.Child("slots");
            return new LfgRequest
            {
                Title = f.Str("title"), Now = f.Bool("now"), Note = f.Str("note"),
                Tank = s?.Int("tank") ?? 0, Healer = s?.Int("healer") ?? 0, Dps = s?.Int("dps") ?? 0, Start = f.LongOrNull("start"),
            };
        }
    }

    // change your own message's text: { id, text }
    public sealed class EditMsg
    {
        public const string Type = LodgeMsg.Edit;
        public string Id, Text;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("id", Id); w.Set("text", Text); }
        public static EditMsg Read(ILodgeFields f) => new EditMsg { Id = f.Str("id"), Text = f.Str("text") };
    }

    // delete a message (your own, or anyone's with Perm.Moderate): { id }
    public sealed class DeleteMsg : LodgeIdMsg<DeleteMsg> { public const string Type = LodgeMsg.Delete; protected override string MsgType => Type; }

    // toggle your reaction: { id, emoji } - emoji is a reaction id of the fixed set (legacy emoji are mapped by the server)
    public sealed class ReactMsg
    {
        public const string Type = LodgeMsg.React;
        public string Id, Emoji;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("id", Id); w.Set("emoji", Emoji); }
        public static ReactMsg Read(ILodgeFields f) => new ReactMsg { Id = f.Str("id"), Emoji = f.Str("emoji") };
    }

    // pin a message (Perm.PinMessages): { id }
    public sealed class PinMsg : LodgeIdMsg<PinMsg> { public const string Type = LodgeMsg.Pin; protected override string MsgType => Type; }

    // unpin: { id, channel } - the pin may outlive its message; channel null = any channel you can see that has it
    public sealed class UnpinMsg
    {
        public const string Type = LodgeMsg.Unpin;
        public string Id, Channel;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("id", Id); w.Set("channel", Channel); }
        public static UnpinMsg Read(ILodgeFields f) => new UnpinMsg { Id = f.Str("id"), Channel = f.Str("channel") };
    }

    // ---------------------------------------------------------------- server -> client

    // a message as stored and broadcast (also every entry of the welcome's history and of history:page):
    //   { t: "msg", channel, id, at (unix ms), from, fromId?, text, file?, kind?, lfg?, event?, replyTo?, edited?, reactions? }
    // fromId is gone once the author left the Lodge (2.22); kind "lfg" carries the sign-up card (lfg), kind "event" a calendar card's
    // snapshot (event); edited: true after an edit; reactions: { <reaction id>: [ names ] } in the order they were first used.
    public sealed class ChatMessage
    {
        public const string Type = LodgeMsg.Msg;
        public string Channel, Id, From, Text, Kind;
        public long At;
        public int? FromId;            // null = not sent
        public ChatFile File;          // null = no attachment
        public LfgCard Lfg;            // kind "lfg": the card (null when the message has no lfg object)
        public CalCard Event;          // kind "event": the snapshot (null when it has no event object)
        public ChatReply ReplyTo;      // null = not a reply (or the quoted message was not found)
        public bool Edited;            // written only when true
        public List<ChatReaction> Reactions = new List<ChatReaction>();   // written only when there are any

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("channel", Channel); w.Set("id", Id); w.Set("at", At); w.Set("from", From);
            if (FromId is int fid) w.Set("fromId", fid);
            w.Set("text", Text);
            if (File != null) File.Write(w.Child("file"));
            if (Kind != null) w.Set("kind", Kind);
            if (Lfg != null) Lfg.Write(w.Child("lfg"));
            if (Event != null) Event.Write(w.Child("event"));
            if (ReplyTo != null) ReplyTo.Write(w.Child("replyTo"));
            if (Edited) w.Set("edited", true);
            if (Reactions.Count > 0)
            {
                var all = w.Child("reactions");
                foreach (var r in Reactions)
                {
                    var l = all.List(r.Id);
                    foreach (var n in r.Users) l.Add(n);
                }
            }
        }

        public static ChatMessage Read(ILodgeFields f)
        {
            var file = f.Child("file"); var lfg = f.Child("lfg"); var ev = f.Child("event"); var reply = f.Child("replyTo");
            var m = new ChatMessage
            {
                Channel = f.Str("channel"), Id = f.Str("id"), At = f.Long("at"), From = f.Str("from"), FromId = f.IntOrNull("fromId"), Text = f.Str("text"),
                Kind = f.Str("kind"), Edited = f.Bool("edited"),
                File = file == null ? null : ChatFile.Read(file), Lfg = lfg == null ? null : LfgCard.Read(lfg),
                Event = ev == null ? null : CalCard.Read(ev), ReplyTo = reply == null ? null : ChatReply.Read(reply),
            };
            var reacts = f.Child("reactions");
            if (reacts != null)
                foreach (var id in reacts.Keys()) m.Reactions.Add(new ChatReaction { Id = id, Users = Copy(reacts.Strs(id)) });
            return m;
        }

        // the list an adapter returned (already a fresh List<string> on every side), or a copy of it
        internal static List<string> Copy(IReadOnlyList<string> l) => l as List<string> ?? new List<string>(l);
    }

    // an attachment: { id, name, size, mime } (the server cleans the name with Names.FileName; the Outpost again with SafeFile.Name)
    public sealed class ChatFile
    {
        public string Id, Name, Mime;
        public long Size;

        public void Write(ILodgeWriter w) { w.Set("id", Id); w.Set("name", Name); w.Set("size", Size); w.Set("mime", Mime); }
        public static ChatFile Read(ILodgeFields f) => new ChatFile { Id = f.Str("id"), Name = f.Str("name"), Size = f.Long("size"), Mime = f.Str("mime") };
    }

    // the quote of a reply: { id, from, text, by, snippet } - from / text are the 2.x names, by / snippet the 2.3 names of the same values
    public sealed class ChatReply
    {
        public string Id, From, Text, By, Snippet;

        public void Write(ILodgeWriter w) { w.Set("id", Id); w.Set("from", From); w.Set("text", Text); w.Set("by", By); w.Set("snippet", Snippet); }
        public static ChatReply Read(ILodgeFields f) =>
            new ChatReply { Id = f.Str("id"), From = f.Str("from"), Text = f.Str("text"), By = f.Str("by"), Snippet = f.Str("snippet") };
    }

    // one reaction of a message: its id and who reacted (the string entries of its array; anything else is skipped)
    public sealed class ChatReaction
    {
        public string Id;
        public List<string> Users = new List<string>();
    }

    // a message's text changed: { channel, id, text, card? } - card: true when an event card follows its event (not shown as "edited")
    public sealed class EditedMsg
    {
        public const string Type = LodgeMsg.Edited;
        public string Channel, Id, Text;
        public bool Card;              // written only when true

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("channel", Channel); w.Set("id", Id); w.Set("text", Text); if (Card) w.Set("card", true); }
        public static EditedMsg Read(ILodgeFields f) => new EditedMsg { Channel = f.Str("channel"), Id = f.Str("id"), Text = f.Str("text"), Card = f.Bool("card") };
    }

    // a message was deleted: { channel, id }
    public sealed class DeletedMsg
    {
        public const string Type = LodgeMsg.Deleted;
        public string Channel, Id;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("channel", Channel); w.Set("id", Id); }
        public static DeletedMsg Read(ILodgeFields f) => new DeletedMsg { Channel = f.Str("channel"), Id = f.Str("id") };
    }

    // a reaction changed: { channel, id, reaction, emoji, users, by, on } - the whole list of that reaction (idempotent); emoji is a
    // representative emoji for 2.14 clients, who read it instead of the id; by / on: who toggled it and which way
    public sealed class ReactionMsg
    {
        public const string Type = LodgeMsg.React;
        public string Channel, Id, Reaction, Emoji, By;
        public List<string> Users = new List<string>();
        public bool On;

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("channel", Channel); w.Set("id", Id); w.Set("reaction", Reaction); w.Set("emoji", Emoji);
            var l = w.List("users");
            foreach (var u in Users) l.Add(u);
            w.Set("by", By); w.Set("on", On);
        }

        public static ReactionMsg Read(ILodgeFields f) => new ReactionMsg
        {
            Channel = f.Str("channel"), Id = f.Str("id"), Reaction = f.Str("reaction"), Emoji = f.Str("emoji"), Users = ChatMessage.Copy(f.Strs("users")),
            By = f.Str("by"), On = f.Bool("on"),
        };
    }

    // a message was pinned (or a pinned message's text changed): { channel, pin }
    public sealed class PinnedMsg
    {
        public const string Type = LodgeMsg.Pin;
        public string Channel;
        public ChatPin Pin;            // null when the frame has no pin object

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("channel", Channel); if (Pin != null) Pin.Write(w.Child("pin")); }
        public static PinnedMsg Read(ILodgeFields f) { var p = f.Child("pin"); return new PinnedMsg { Channel = f.Str("channel"), Pin = p == null ? null : ChatPin.Read(p) }; }
    }

    // a pin (also every entry of the welcome's pins): its own copy of the message, so it outlives the bounded history
    //   { id, text (clipped to 300), by (the author), at, pinnedBy, pinnedAt, file? }
    public sealed class ChatPin
    {
        public string Id, Text, By, PinnedBy;
        public long At, PinnedAt;
        public ChatFile File;          // null = not sent

        public void Write(ILodgeWriter w)
        {
            w.Set("id", Id); w.Set("text", Text); w.Set("by", By); w.Set("at", At); w.Set("pinnedBy", PinnedBy); w.Set("pinnedAt", PinnedAt);
            if (File != null) File.Write(w.Child("file"));
        }

        public static ChatPin Read(ILodgeFields f)
        {
            var file = f.Child("file");
            return new ChatPin
            {
                Id = f.Str("id"), Text = f.Str("text"), By = f.Str("by"), At = f.Long("at"), PinnedBy = f.Str("pinnedBy"), PinnedAt = f.Long("pinnedAt"),
                File = file == null ? null : ChatFile.Read(file),
            };
        }
    }

    // a message was unpinned (or deleted while pinned): { channel, id }
    public sealed class UnpinnedMsg
    {
        public const string Type = LodgeMsg.Unpin;
        public string Channel, Id;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("channel", Channel); w.Set("id", Id); }
        public static UnpinnedMsg Read(ILodgeFields f) => new UnpinnedMsg { Channel = f.Str("channel"), Id = f.Str("id") };
    }

    // ---------------------------------------------------------------- older messages and search (2.9; ChatHistory.cs)
    // Only channels the asker's rank may see are ever looked at (an invisible channel answers like a missing one), the Page and Search
    // budgets, the page size (1-100), the search query's cleaning (Names.Text, 64 characters, at least 2) and its 50 hits stay in the
    // server. The server still reads history:before's beforeAt from the raw JSON: only a whole JSON number counts there (TryGetValue<long>).

    // client -> server: the messages just older than `before` (a message id); beforeAt (its unix ms) is used when that message is gone;
    // limit null = not sent (the server's 100)
    public sealed class HistoryBeforeMsg
    {
        public const string Type = LodgeMsg.HistoryBefore;
        public string Channel, Before;
        public long BeforeAt;
        public int? Limit;

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("channel", Channel); w.Set("before", Before); w.Set("beforeAt", BeforeAt);
            if (Limit is int l) w.Set("limit", l);
        }

        // limit: a number (cut to a whole one, as the server's J.Int read it), null when missing or not a number
        public static HistoryBeforeMsg Read(ILodgeFields f) => new HistoryBeforeMsg
        {
            Channel = f.Str("channel"), Before = f.Str("before"), BeforeAt = f.Long("beforeAt"),
            Limit = f.Num("limit") is double d ? (int)System.Math.Clamp(d, int.MinValue, int.MaxValue) : (int?)null,
        };
    }

    // server -> client: the answer, oldest first: { channel, before, messages: [msg], hasMore }
    public sealed class HistoryPageMsg
    {
        public const string Type = LodgeMsg.HistoryPage;
        public string Channel, Before;
        public bool HasMore;
        // the stored messages as they are (JsonArray on the server, List<object> in the Outpost - see ILodgeFields.RawList): the server
        // passes its records through unchanged. Null: Items is written instead.
        public object Messages;
        public List<ChatMessage> Items = new List<ChatMessage>();   // read side: the typed view of every object in messages

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("channel", Channel); w.Set("before", Before);
            if (Messages != null) w.SetRawList("messages", Messages);
            else { var l = w.List("messages"); foreach (var m in Items) m.Write(l.Add()); }
            w.Set("hasMore", HasMore);
        }

        public static HistoryPageMsg Read(ILodgeFields f)
        {
            var p = new HistoryPageMsg { Channel = f.Str("channel"), Before = f.Str("before"), HasMore = f.Bool("hasMore"), Messages = f.RawList("messages") };
            foreach (var m in f.List("messages")) p.Items.Add(ChatMessage.Read(m));
            return p;
        }
    }

    // client -> server: search the text channels I may see (channel null = all of them, not sent)
    public sealed class SearchMsg
    {
        public const string Type = LodgeMsg.Search;
        public string Q, Channel;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("q", Q); if (Channel != null) w.Set("channel", Channel); }
        public static SearchMsg Read(ILodgeFields f) => new SearchMsg { Q = f.Str("q"), Channel = f.Str("channel") };
    }

    // server -> client: { q (as the server cleaned it), hits: [hit] newest first, more (there were more than were sent) }
    public sealed class SearchResultMsg
    {
        public const string Type = LodgeMsg.SearchResult;
        public string Q;
        public List<SearchHit> Hits = new List<SearchHit>();
        public bool More;

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("q", Q);
            var l = w.List("hits");
            foreach (var h in Hits) h.Write(l.Add());
            w.Set("more", More);
        }

        public static SearchResultMsg Read(ILodgeFields f)
        {
            var r = new SearchResultMsg { Q = f.Str("q"), More = f.Bool("more") };
            foreach (var h in f.List("hits")) r.Hits.Add(SearchHit.Read(h));
            return r;
        }
    }

    // one hit: { channel, id, from, at, snippet } - snippet: about 120 characters around the match, whitespace collapsed, "…" where cut
    public sealed class SearchHit
    {
        public string Channel, Id, From, Snippet;
        public long At;

        public void Write(ILodgeWriter w) { w.Set("channel", Channel); w.Set("id", Id); w.Set("from", From); w.Set("at", At); w.Set("snippet", Snippet); }
        public static SearchHit Read(ILodgeFields f) =>
            new SearchHit { Channel = f.Str("channel"), Id = f.Str("id"), From = f.Str("from"), At = f.Long("at"), Snippet = f.Str("snippet") };
    }
}
