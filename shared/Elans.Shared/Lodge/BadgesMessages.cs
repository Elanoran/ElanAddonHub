using System.Collections.Generic;

namespace Elans.Shared
{
    // Badges and the ladder (server/PROTOCOL.md, 2.18 additions): the field names live only here. The classes only map fields; every rule
    // (who can have badges, the definitions table, points, ranks, seasons, the rate limit, the Guild Master's switch) stays in the Lodge
    // server (Badges.cs, BadgesModule.cs). The server reads admin.badges' "on" from the raw JSON: a missing or wrong-typed value means on
    // there, which the lenient reading below can't tell from false.

    // client -> server: your own badges with progress (answered with BadgesMineMsg)
    public sealed class BadgesMineGetMsg : LodgeEmptyMsg<BadgesMineGetMsg> { public const string Type = LodgeMsg.BadgesMine; protected override string MsgType => Type; }

    // client -> server: you looked at your badges (the quiet "new" dot goes); no answer
    public sealed class BadgesSeenMsg : LodgeEmptyMsg<BadgesSeenMsg> { public const string Type = LodgeMsg.BadgesSeen; protected override string MsgType => Type; }

    // client -> server: leave (or rejoin) the ladder (answered with BadgesMineMsg)
    public sealed class BadgesHideMsg
    {
        public const string Type = LodgeMsg.BadgesHide;
        public bool Hide;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("hide", Hide); }
        public static BadgesHideMsg Read(ILodgeFields f) => new BadgesHideMsg { Hide = f.Bool("hide") };
    }

    // client -> server: another member's earned badges by login name (answered with BadgesMemberMsg)
    public sealed class BadgesMemberGetMsg
    {
        public const string Type = LodgeMsg.BadgesMember;
        public string Name;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("name", Name); }
        public static BadgesMemberGetMsg Read(ILodgeFields f) => new BadgesMemberGetMsg { Name = f.Str("name") };
    }

    // client -> server: the ladder of a season ("2026-10") or of all time ("all"); an unknown season means the current one
    public sealed class LadderGetMsg
    {
        public const string Type = LodgeMsg.LadderGet;
        public string Season;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("season", Season); }
        public static LadderGetMsg Read(ILodgeFields f) => new LadderGetMsg { Season = f.Str("season") };
    }

    // client -> server: (Perm.ManageMembers) badges and the ladder on / off for the whole Lodge (answered with a BadgesStateMsg broadcast)
    public sealed class AdminBadgesMsg
    {
        public const string Type = LodgeMsg.AdminBadges;
        public bool On;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("on", On); }
        public static AdminBadgesMsg Read(ILodgeFields f) => new AdminBadgesMsg { On = !f.Has("on") || f.Bool("on") };   // lenient; the server reads it raw (see above)
    }

    // server -> client: the Guild Master's switch { enabled, locked } (to everybody when it flips); welcome.badges is the same without "t"
    public sealed class BadgesStateMsg
    {
        public const string Type = LodgeMsg.BadgesState;
        public bool Enabled;           // the feature is on (the server setting and the Guild Master's switch)
        public bool Locked;            // switched off in the server settings: the Guild Master can't turn it on

        public void Write(ILodgeWriter w) { w.Set("t", Type); WriteState(w); }
        public void WriteState(ILodgeWriter w) { w.Set("enabled", Enabled); w.Set("locked", Locked); }      // welcome.badges
        public static BadgesStateMsg Read(ILodgeFields f) => new BadgesStateMsg { Enabled = f.Bool("enabled"), Locked = f.Bool("locked") };
    }

    // server -> client: your own badges (every badge, with progress) and your standing on the ladder
    public sealed class BadgesMineMsg
    {
        public const string Type = LodgeMsg.BadgesMine;
        public bool Ok = true;
        public string You;             // your login name
        public bool Hide;              // you left the ladder
        public int New;                // earned since you last looked
        public long Points, SeasonPoints;
        public int Rank, SeasonRank;   // 0 = no points yet
        public string Season;          // the current season ("2026-10"; "" before the first run)
        public int Earned, Total;
        public List<BadgeView> Badges = new List<BadgeView>();

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("ok", Ok); w.Set("you", You); w.Set("hide", Hide); w.Set("new", New);
            w.Set("points", Points); w.Set("rank", Rank); w.Set("season", Season); w.Set("seasonPoints", SeasonPoints); w.Set("seasonRank", SeasonRank);
            w.Set("earned", Earned); w.Set("total", Total);
            BadgeView.WriteList(w, "badges", Badges);
        }

        public static BadgesMineMsg Read(ILodgeFields f) => new BadgesMineMsg
        {
            Ok = f.Bool("ok"), You = f.Str("you"), Hide = f.Bool("hide"), New = f.Int("new"), Points = f.Long("points"), Rank = f.Int("rank"),
            Season = f.Str("season"), SeasonPoints = f.Long("seasonPoints"), SeasonRank = f.Int("seasonRank"), Earned = f.Int("earned"), Total = f.Int("total"),
            Badges = BadgeView.ReadList(f, "badges"),
        };
    }

    // server -> client: another member's earned badges (none for an unknown name)
    public sealed class BadgesMemberMsg
    {
        public const string Type = LodgeMsg.BadgesMember;
        public bool Ok = true;
        public string Name;
        public List<BadgeView> Badges = new List<BadgeView>();   // earned ones only (the short form)
        public int Count;              // reading: the count sent (written: Badges.Count)

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("ok", Ok); w.Set("name", Name); BadgeView.WriteList(w, "badges", Badges); w.Set("count", Badges.Count); }
        public static BadgesMemberMsg Read(ILodgeFields f) => new BadgesMemberMsg { Ok = f.Bool("ok"), Name = f.Str("name"), Badges = BadgeView.ReadList(f, "badges"), Count = f.Int("count") };
    }

    // server -> client: the ladder of a season (at most 100 rows)
    public sealed class LadderDataMsg
    {
        public const string Type = LodgeMsg.LadderData;
        public bool Ok = true;
        public string Season;          // the season of the rows ("all" or "2026-10")
        public string Current;         // the current season
        public List<string> Seasons = new List<string>();     // the current one first, then the archived ones
        public List<LadderRow> Rows = new List<LadderRow>();
        public LadderYou You;          // your own place; null (sent as null) when you are not on it

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("ok", Ok); w.Set("season", Season); w.Set("current", Current);
            var s = w.List("seasons"); foreach (var x in Seasons) s.Add(x);
            var r = w.List("rows"); foreach (var x in Rows) x.Write(r.Add());
            if (You == null) w.Set("you", (string)null); else You.Write(w.Child("you"));
        }

        public static LadderDataMsg Read(ILodgeFields f)
        {
            var m = new LadderDataMsg { Ok = f.Bool("ok"), Season = f.Str("season"), Current = f.Str("current"), Seasons = new List<string>(f.Strs("seasons")) };
            foreach (var r in f.List("rows")) m.Rows.Add(LadderRow.Read(r));
            var y = f.Child("you");
            m.You = y == null ? null : LadderYou.Read(y);
            return m;
        }
    }

    // a ladder row { rank, name, points, top: [badge (short form), at most 3], you? (true on your own row), hidden? (true: only you see it) }
    public sealed class LadderRow
    {
        public int Rank;
        public string Name;
        public long Points;
        public List<BadgeView> Top = new List<BadgeView>();
        public bool You, Hidden;       // written only when true (Hidden only on your own row)

        public void Write(ILodgeWriter w)
        {
            w.Set("rank", Rank); w.Set("name", Name); w.Set("points", Points);
            BadgeView.WriteList(w, "top", Top);
            if (You) { w.Set("you", true); if (Hidden) w.Set("hidden", true); }
        }

        public static LadderRow Read(ILodgeFields f) => new LadderRow
        {
            Rank = f.Int("rank"), Name = f.Str("name"), Points = f.Long("points"), Top = BadgeView.ReadList(f, "top"), You = f.Bool("you"), Hidden = f.Bool("hidden"),
        };
    }

    // ladder.data.you { rank, points, hidden }
    public sealed class LadderYou
    {
        public int Rank;
        public long Points;
        public bool Hidden;

        public void Write(ILodgeWriter w) { w.Set("rank", Rank); w.Set("points", Points); w.Set("hidden", Hidden); }
        public static LadderYou Read(ILodgeFields f) => new LadderYou { Rank = f.Int("rank"), Points = f.Long("points"), Hidden = f.Bool("hidden") };
    }

    // A badge. Two forms:
    //   short (an earned badge: badges.member, a ladder row's top): { id, family, tier, name, at }
    //   full (badges.mine):  { id, family, familyName, tier, hidden, earned, name, flavour, how, at? (earned), value?, target? }
    //                        - a hidden badge not earned yet: name "???", flavour "", how "???" and no at / value / target
    // A field that is null here is not written.
    public sealed class BadgeView
    {
        public string Id, Family, Name;
        public int Tier;
        public string FamilyName, Flavour, How;   // the full form only
        public bool? Hidden, Earned;              // the full form only (reading: a missing "earned" is null = an earned badge of the short form)
        public long? At;                          // unix seconds, when earned
        public long? Value, Target;               // progress (the full form, not for a hidden badge not earned yet)

        public void Write(ILodgeWriter w)
        {
            w.Set("id", Id); w.Set("family", Family);
            if (FamilyName != null) w.Set("familyName", FamilyName);
            w.Set("tier", Tier);
            if (Hidden is bool h) w.Set("hidden", h);
            if (Earned is bool e) w.Set("earned", e);
            w.Set("name", Name);
            if (Flavour != null) w.Set("flavour", Flavour);
            if (How != null) w.Set("how", How);
            if (At is long a) w.Set("at", a);
            if (Value is long v) w.Set("value", v);
            if (Target is long t) w.Set("target", t);
        }

        public static BadgeView Read(ILodgeFields f) => new BadgeView
        {
            Id = f.Str("id"), Family = f.Str("family"), FamilyName = f.Str("familyName"), Tier = f.Int("tier"), Name = f.Str("name"),
            Flavour = f.Str("flavour"), How = f.Str("how"),
            Hidden = f.Has("hidden") ? f.Bool("hidden") : (bool?)null, Earned = f.Has("earned") ? f.Bool("earned") : (bool?)null,
            At = f.Has("at") ? f.Long("at") : (long?)null, Value = f.Has("value") ? f.Long("value") : (long?)null, Target = f.Has("target") ? f.Long("target") : (long?)null,
        };

        public static void WriteList(ILodgeWriter w, string key, List<BadgeView> list) { var l = w.List(key); foreach (var b in list) b.Write(l.Add()); }
        public static List<BadgeView> ReadList(ILodgeFields f, string key) { var l = new List<BadgeView>(); foreach (var b in f.List(key)) l.Add(Read(b)); return l; }
    }
}
