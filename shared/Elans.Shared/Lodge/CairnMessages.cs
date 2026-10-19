using System.Collections.Generic;

namespace Elans.Shared
{
    // The Cairn messages between a Lodge and its Outposts (server/PROTOCOL.md, "The Cairn"): give to get (atlas.sharing, cairn.status), the
    // Guild Master's "Join the Cairn" status (cairn.join.get, cairn.join) and the Cairn operator's notices (cairn.notices.get, cairn.notices,
    // cairn.notice.seen). The classes only map fields: who may see what, the sharing store and the notices' signatures stay where they are (the
    // Lodge server's CairnReceive.cs / CairnNotices.cs; the Outpost checks each notice with CairnFormats.Notice + Es256), and the Guild Master's
    // admin.cairn switch (the address and code rules stay in CairnReceive.SetJoin / CairnSign.TryNormalizeUrl).

    // client -> server: the member's Atlas sharing switch (answered with cairn.status)
    public sealed class AtlasSharingMsg
    {
        public const string Type = LodgeMsg.AtlasSharing;
        public bool On;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("on", On); }
        public static AtlasSharingMsg Read(ILodgeFields f) => new AtlasSharingMsg { On = f.Bool("on") };
    }

    // client -> server: ask whether you receive the Cairn's data (answered with cairn.status)
    public sealed class CairnStatusGetMsg : LodgeEmptyMsg<CairnStatusGetMsg> { public const string Type = LodgeMsg.CairnStatus; protected override string MsgType => Type; }

    // server -> client: whether you receive the Cairn's data { entitled, sharing, why?, text?, day?, seq?, lodge? }. A missing optional field
    // stays missing when written.
    public sealed class CairnStatusMsg
    {
        public const string Type = LodgeMsg.CairnStatus;
        public bool Entitled, Sharing;
        public string Why, Text;       // when not entitled: share | guest | lodge | disabled | not-yet, and the words for it
        public string Day;             // the dataset on hand (when entitled)
        public long? Seq;
        public CairnLodgeStanding Lodge;   // the owner only: the Lodge's own standing at the Cairn

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("entitled", Entitled); w.Set("sharing", Sharing);
            if (Why != null) w.Set("why", Why);
            if (Text != null) w.Set("text", Text);
            if (Day != null) w.Set("day", Day);
            if (Seq is long s) w.Set("seq", s);
            if (Lodge != null) Lodge.Write(w.Child("lodge"));
        }

        public static CairnStatusMsg Read(ILodgeFields f)
        {
            var l = f.Child("lodge");
            return new CairnStatusMsg
            {
                Entitled = f.Bool("entitled"), Sharing = f.Bool("sharing"), Why = f.Str("why"), Text = f.Str("text"), Day = f.Str("day"),
                Seq = f.Has("seq") ? f.Long("seq") : (long?)null, Lodge = l == null ? null : CairnLodgeStanding.Read(l),
            };
        }
    }

    // cairn.status.lodge { standing, serving, receiveUntil?, checked?, why?, problem? }
    public sealed class CairnLodgeStanding
    {
        public string Standing;        // good | new | lapsed | revoked | key | unknown
        public bool Serving;
        public long? ReceiveUntil, Checked;    // unix seconds
        public string Why, Problem;

        public void Write(ILodgeWriter w)
        {
            w.Set("standing", Standing); w.Set("serving", Serving);
            if (ReceiveUntil is long u) w.Set("receiveUntil", u);
            if (Checked is long c) w.Set("checked", c);
            if (Why != null) w.Set("why", Why);
            if (Problem != null) w.Set("problem", Problem);
        }

        public static CairnLodgeStanding Read(ILodgeFields f) => new CairnLodgeStanding
        {
            Standing = f.Str("standing"), Serving = f.Bool("serving"), ReceiveUntil = f.Has("receiveUntil") ? f.Long("receiveUntil") : (long?)null,
            Checked = f.Has("checked") ? f.Long("checked") : (long?)null, Why = f.Str("why"), Problem = f.Str("problem"),
        };
    }

    // client -> server: (officers and the Guild Master) the Lodge's Cairn joining state
    public sealed class CairnJoinGetMsg : LodgeEmptyMsg<CairnJoinGetMsg> { public const string Type = LodgeMsg.CairnJoinGet; protected override string MsgType => Type; }

    // server -> client: the Lodge's Cairn joining state. Every field is always sent; a number that isn't known is JSON null.
    public sealed class CairnJoinMsg
    {
        public const string Type = LodgeMsg.CairnJoin;
        public bool On, Env, Atlas = true, Contributing, Serving, HasCode, Settled, Signed;   // Atlas: a missing "atlas" reads as true
        public string CairnHost, SuggestedUrl, Standing, PublicUrl, State, Reason, Error, Mode, EstDay;
        public long? ReceiveUntil, Admitted, ProbationUntil, LastPost, Checked;
        public int Place, Of;
        public int? BetaOpenSlots;

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("env", Env); w.Set("atlas", Atlas); w.Set("cairnHost", CairnHost); w.Set("contributing", Contributing);
            w.Set("suggestedUrl", SuggestedUrl); w.Set("standing", Standing); w.SetOrNull("receiveUntil", ReceiveUntil); w.Set("serving", Serving);
            w.Set("on", On); w.Set("publicUrl", PublicUrl); w.Set("state", State); w.Set("reason", Reason); w.Set("error", Error);
            w.Set("mode", Mode); w.Set("place", Place); w.Set("of", Of); w.Set("estDay", EstDay); w.SetOrNull("betaOpenSlots", BetaOpenSlots); w.Set("hasCode", HasCode);
            w.SetOrNull("admitted", Admitted); w.SetOrNull("probationUntil", ProbationUntil); w.Set("settled", Settled); w.SetOrNull("lastPost", LastPost);
            w.SetOrNull("checked", Checked); w.Set("signed", Signed);
        }

        public static CairnJoinMsg Read(ILodgeFields f) => new CairnJoinMsg
        {
            On = f.Bool("on"), Env = f.Bool("env"), Atlas = !f.Has("atlas") || f.Bool("atlas"), Contributing = f.Bool("contributing"), Serving = f.Bool("serving"),
            HasCode = f.Bool("hasCode"), Settled = f.Bool("settled"), Signed = f.Bool("signed"),
            CairnHost = f.Str("cairnHost"), SuggestedUrl = f.Str("suggestedUrl"), Standing = f.Str("standing"), PublicUrl = f.Str("publicUrl"), State = f.Str("state"),
            Reason = f.Str("reason"), Error = f.Str("error"), Mode = f.Str("mode"), EstDay = f.Str("estDay"),
            ReceiveUntil = f.LongOrNull("receiveUntil"), Admitted = f.LongOrNull("admitted"), ProbationUntil = f.LongOrNull("probationUntil"),
            LastPost = f.LongOrNull("lastPost"), Checked = f.LongOrNull("checked"),
            Place = f.Int("place"), Of = f.Int("of"), BetaOpenSlots = f.IntOrNull("betaOpenSlots"),
        };
    }

    // client -> server: (Perm.ManageMembers) the Guild Master's "Join the Cairn" switch { on, publicUrl?, code? } (answered with cairn.join to the
    // leaders, or an error with code locked / atlas / address). publicUrl / code only when given: the Lodge keeps what it has otherwise. The
    // invite code goes to the Lodge once and is never sent back (cairn.join only says hasCode).
    public sealed class AdminCairnMsg
    {
        public const string Type = LodgeMsg.AdminCairn;
        public bool On;
        public string PublicUrl, Code;  // null = not sent

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("on", On);
            if (PublicUrl != null) w.Set("publicUrl", PublicUrl);
            if (Code != null) w.Set("code", Code);
        }

        public static AdminCairnMsg Read(ILodgeFields f) => new AdminCairnMsg { On = f.Bool("on"), PublicUrl = f.Str("publicUrl"), Code = f.Str("code") };
    }

    // client -> server: ask for the Cairn's notices
    public sealed class CairnNoticesGetMsg : LodgeEmptyMsg<CairnNoticesGetMsg> { public const string Type = LodgeMsg.CairnNoticesGet; protected override string MsgType => Type; }

    // server -> client: the Cairn's signed notices for you { list: [notice] }
    public sealed class CairnNoticesMsg
    {
        public const string Type = LodgeMsg.CairnNotices;
        public List<CairnNoticeEntry> List = new List<CairnNoticeEntry>();

        public void Write(ILodgeWriter w) { w.Set("t", Type); var l = w.List("list"); foreach (var n in List) n.Write(l.Add()); }
        public static CairnNoticesMsg Read(ILodgeFields f)
        {
            var m = new CairnNoticesMsg();
            foreach (var n in f.List("list")) m.List.Add(CairnNoticeEntry.Read(n));
            return m;
        }
    }

    // one notice as the Cairn signed it: { id, created, expires (unix seconds), level: info|warning|critical, to, text, sig, kid }. Only the
    // fields: the signature (over CairnFormats.Notice) is checked by the reader.
    public sealed class CairnNoticeEntry
    {
        public long Id, Created, Expires;
        public string Level, To, Text, Sig, Kid;

        public void Write(ILodgeWriter w)
        {
            w.Set("id", Id); w.Set("created", Created); w.Set("expires", Expires); w.Set("level", Level); w.Set("to", To); w.Set("text", Text);
            w.Set("sig", Sig); w.Set("kid", Kid);
        }

        public static CairnNoticeEntry Read(ILodgeFields f) => new CairnNoticeEntry
        {
            Id = f.Long("id"), Created = f.Long("created"), Expires = f.Long("expires"), Level = f.Str("level"), To = f.Str("to"), Text = f.Str("text"),
            Sig = f.Str("sig"), Kid = f.Str("kid"),
        };
    }

    // client -> server: notices the user closed (only an officer's or the Guild Master's counts)
    public sealed class CairnNoticeSeenMsg
    {
        public const string Type = LodgeMsg.CairnNoticeSeen;
        public List<long> Ids = new List<long>();     // reading: an entry that isn't a whole number is 0

        public void Write(ILodgeWriter w) { w.Set("t", Type); var l = w.List("ids"); foreach (var id in Ids) l.Add(id); }
        public static CairnNoticeSeenMsg Read(ILodgeFields f) => new CairnNoticeSeenMsg { Ids = new List<long>(f.Longs("ids")) };
    }
}
