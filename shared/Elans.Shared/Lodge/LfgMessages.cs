using System.Collections.Generic;

namespace Elans.Shared
{
    // "Looking for group" messages (server/PROTOCOL.md, 2.9 additions): the field names live only here. The classes only map fields;
    // every rule (roles, limits, who may close) stays in the Lodge server (Lfg.cs, ChatLfg.cs).

    // client -> server: sign up with a role (tank | healer | dps | maybe)
    public sealed class LfgJoinMsg
    {
        public const string Type = LodgeMsg.LfgJoin;
        public string Id;      // the card's message id
        public string Role;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("id", Id); w.Set("role", Role); }
        public static LfgJoinMsg Read(ILodgeFields f) => new LfgJoinMsg { Id = f.Str("id"), Role = f.Str("role") };
    }

    // client -> server: sign up as maybe
    public sealed class LfgMaybeMsg : LodgeIdMsg<LfgMaybeMsg> { public const string Type = LodgeMsg.LfgMaybe; protected override string MsgType => Type; }

    // client -> server: take your sign-up back
    public sealed class LfgLeaveMsg : LodgeIdMsg<LfgLeaveMsg> { public const string Type = LodgeMsg.LfgLeave; protected override string MsgType => Type; }

    // client -> server: the organiser (or an officer) closes the group - it is set
    public sealed class LfgCloseMsg : LodgeIdMsg<LfgCloseMsg> { public const string Type = LodgeMsg.LfgClose; protected override string MsgType => Type; }

    // client -> server: the organiser (or an officer) cancels the group - it won't happen
    public sealed class LfgCancelMsg : LodgeIdMsg<LfgCancelMsg> { public const string Type = LodgeMsg.LfgCancel; protected override string MsgType => Type; }

    // server -> client: a card changed; the whole current card state arrives, plus the new plain-text fallback
    public sealed class LfgUpdateMsg
    {
        public const string Type = LodgeMsg.Lfg;
        public string Channel;
        public string Id;      // the card's message id
        public LfgCard Card;   // null when the update carries no card object
        public string Text;    // the plain-text fallback (what older clients, history and search show)

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("channel", Channel); w.Set("id", Id);
            if (Card != null) Card.Write(w.Child("lfg"));
            w.Set("text", Text);
        }

        public static LfgUpdateMsg Read(ILodgeFields f)
        {
            var card = f.Child("lfg");
            return new LfgUpdateMsg { Channel = f.Str("channel"), Id = f.Str("id"), Card = card == null ? null : LfgCard.Read(card), Text = f.Str("text") };
        }
    }

    // A sign-up card's state: the "lfg" object of an lfg chat message and of the lfg update.
    //   { title, start (unix ms), now, note, status: open|closed|cancelled, by, slots: { tank, healer, dps }, signups: [ { n, r } ] }
    public sealed class LfgCard
    {
        public string Title;
        public long Start;
        public bool Now;
        public string Note;
        public string Status;  // null when missing (readers treat that as "open")
        public string By;
        public int Tank, Healer, Dps;   // slots per role
        public List<LfgSignup> Signups = new List<LfgSignup>();

        public int SlotsOf(string role) => role == "tank" ? Tank : role == "healer" ? Healer : role == "dps" ? Dps : 0;

        // a missing string stays missing (not written as null), so a card goes out exactly as it is stored
        public void Write(ILodgeWriter w)
        {
            if (Title != null) w.Set("title", Title);
            w.Set("start", Start);
            w.Set("now", Now);
            if (Note != null) w.Set("note", Note);
            if (Status != null) w.Set("status", Status);
            if (By != null) w.Set("by", By);
            var slots = w.Child("slots");
            slots.Set("tank", Tank); slots.Set("healer", Healer); slots.Set("dps", Dps);
            var list = w.List("signups");
            foreach (var s in Signups) { var e = list.Add(); e.Set("n", s.Name); e.Set("r", s.Role); }
        }

        public static LfgCard Read(ILodgeFields f)
        {
            var slots = f.Child("slots");
            var card = new LfgCard
            {
                Title = f.Str("title"), Start = f.Long("start"), Now = f.Bool("now"), Note = f.Str("note"), Status = f.Str("status"), By = f.Str("by"),
                Tank = slots?.Int("tank") ?? 0, Healer = slots?.Int("healer") ?? 0, Dps = slots?.Int("dps") ?? 0,
            };
            foreach (var s in f.List("signups")) card.Signups.Add(new LfgSignup { Name = s.Str("n"), Role = s.Str("r") });
            return card;
        }
    }

    public sealed class LfgSignup
    {
        public string Name;    // "n"
        public string Role;    // "r": tank | healer | dps | maybe
    }
}
