using System.Collections.Generic;

namespace Elans.Shared
{
    // The Armory (server/PROTOCOL.md, 2.16 / 2.19 armory2 / 2.27 reputations): the field names of the envelopes live only here. The classes only
    // map fields; every rule stays where it was. The character sheet itself ("char": gear, talents, professions, pets, reputations) is a
    // free-form object passed through as it is (ILodgeFields.RawChild / ILodgeWriter.SetRawChild): the Outpost builds it (ArmoryShare.Build),
    // the server rebuilds it field by field from the raw JSON (Armory.Clean) and serves the cleaned copy, and the Characters page reads it with
    // its own checks. The server also still validates armory.crafters' spells and armory.standing's faction from the raw JSON (a number must be
    // a whole JSON number there), and the Outpost reads the spells of an armory.crafters answer strictly from the raw frame.

    // client -> server: share one of your characters (answered with armory.saved)
    public sealed class ArmoryPutMsg
    {
        public const string Type = LodgeMsg.ArmoryPut;
        public string Key;             // "<realm>-<name>"
        public object Char;            // the character sheet in this side's own JSON model (see above)

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("key", Key); if (Char != null) w.SetRawChild("char", Char); }
        public static ArmoryPutMsg Read(ILodgeFields f) => new ArmoryPutMsg { Key = f.Str("key"), Char = f.RawChild("char") };
    }

    // server -> client: the answer to armory.put { ok, stored, key?, why? }
    public sealed class ArmorySavedMsg
    {
        public const string Type = LodgeMsg.ArmorySaved;
        public bool Ok;
        public bool Stored;            // false with ok: the Lodge already had exactly this
        public string Key;             // null = not sent (refused before the key was looked at)
        public string Why;             // disabled | guest | invalid | rate | empty | size | full | save; null = not sent

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("ok", Ok); w.Set("stored", Stored);
            if (Key != null) w.Set("key", Key);
            if (Why != null) w.Set("why", Why);
        }

        public static ArmorySavedMsg Read(ILodgeFields f) => new ArmorySavedMsg { Ok = f.Bool("ok"), Stored = f.Bool("stored"), Key = f.Str("key"), Why = f.Str("why") };
    }

    // client -> server: take one of your characters out ({ key }), or all of them ({ all: true })
    public sealed class ArmoryDeleteMsg
    {
        public const string Type = LodgeMsg.ArmoryDelete;
        public string Key;             // null = not sent
        public bool All;               // written only when true

        public void Write(ILodgeWriter w) { w.Set("t", Type); if (All) w.Set("all", true); if (Key != null) w.Set("key", Key); }
        public static ArmoryDeleteMsg Read(ILodgeFields f) => new ArmoryDeleteMsg { Key = f.Str("key"), All = f.Bool("all") };
    }

    // server -> client: the answer to armory.delete { ok, removed, key? } or { ok: false, why }
    public sealed class ArmoryDeletedMsg
    {
        public const string Type = LodgeMsg.ArmoryDeleted;
        public bool Ok;
        public int? Removed;           // null = not sent (a refusal)
        public string Key;             // null = not sent (all, or a refusal); reading: "" when sent but not a string (the Outpost asks "was a key sent")
        public string Why;             // guest | invalid | rate; null = not sent
        public bool All;               // read by the Outpost; the server never sends it (written only when true)

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("ok", Ok);
            if (Removed is int n) w.Set("removed", n);
            if (Key != null) w.Set("key", Key);
            if (Why != null) w.Set("why", Why);
            if (All) w.Set("all", true);
        }

        public static ArmoryDeletedMsg Read(ILodgeFields f) => new ArmoryDeletedMsg
        {
            Ok = f.Bool("ok"), Removed = f.Has("removed") ? f.Int("removed") : (int?)null, Key = f.Has("key") ? (f.Str("key") ?? "") : null, Why = f.Str("why"), All = f.Bool("all"),
        };
    }

    // client -> server: the Lodge's shared characters (answered with armory.chars)
    public sealed class ArmoryListMsg : LodgeEmptyMsg<ArmoryListMsg> { public const string Type = LodgeMsg.ArmoryList; protected override string MsgType => Type; }

    // server -> client: every shared character as a one-line summary
    public sealed class ArmoryCharsMsg
    {
        public const string Type = LodgeMsg.ArmoryChars;
        public List<ArmorySummary> Chars = new List<ArmorySummary>();

        public void Write(ILodgeWriter w) { w.Set("t", Type); var l = w.List("chars"); foreach (var c in Chars) c.Write(l.Add()); }
        public static ArmoryCharsMsg Read(ILodgeFields f) { var m = new ArmoryCharsMsg(); foreach (var c in f.List("chars")) m.Chars.Add(ArmorySummary.Read(c)); return m; }
    }

    // A line of armory.chars: what the stored sheet has of { name, realm, class, classText, race, raceFile, sex, faction, guild, level, ilvl, at },
    // professions: [{ name, skill, max }], then owner (the member's identity), by (their display name), key, up (when it was shared), mine? (true).
    // A field that is null here is not written.
    public sealed class ArmorySummary
    {
        public string Name, Realm, Class, ClassText, Race, RaceFile, Faction, Guild;
        public int? Sex, Level;
        public double? Ilvl;           // reading: strict (null unless a JSON number)
        public long? At;
        public List<ArmoryProfession> Professions = new List<ArmoryProfession>();
        public string Owner, By, Key;
        public long Up;
        public bool Mine;              // written only when true

        public void Write(ILodgeWriter w)
        {
            void S(string k, string v) { if (v != null) w.Set(k, v); }
            S("name", Name); S("realm", Realm); S("class", Class); S("classText", ClassText); S("race", Race); S("raceFile", RaceFile);
            if (Sex is int sex) w.Set("sex", sex);
            S("faction", Faction); S("guild", Guild);
            if (Level is int lv) w.Set("level", lv);
            if (Ilvl is double il) w.Set("ilvl", il);
            if (At is long at) w.Set("at", at);
            var l = w.List("professions"); foreach (var p in Professions) p.Write(l.Add());
            w.Set("owner", Owner); w.Set("by", By); w.Set("key", Key); w.Set("up", Up);
            if (Mine) w.Set("mine", true);
        }

        public static ArmorySummary Read(ILodgeFields f)
        {
            var s = new ArmorySummary
            {
                Name = f.Str("name"), Realm = f.Str("realm"), Class = f.Str("class"), ClassText = f.Str("classText"), Race = f.Str("race"), RaceFile = f.Str("raceFile"),
                Faction = f.Str("faction"), Guild = f.Str("guild"), Sex = f.Has("sex") ? f.Int("sex") : (int?)null, Level = f.Has("level") ? f.Int("level") : (int?)null,
                Ilvl = f.Num("ilvl"), At = f.Has("at") ? f.Long("at") : (long?)null,
                Owner = f.Str("owner"), By = f.Str("by"), Key = f.Str("key"), Up = f.Long("up"), Mine = f.Bool("mine"),
            };
            foreach (var p in f.List("professions")) s.Professions.Add(ArmoryProfession.Read(p));
            return s;
        }
    }

    // a profession on a summary { name, skill, max }
    public sealed class ArmoryProfession
    {
        public string Name;
        public int Skill, Max;

        public void Write(ILodgeWriter w) { w.Set("name", Name); w.Set("skill", Skill); w.Set("max", Max); }
        public static ArmoryProfession Read(ILodgeFields f) => new ArmoryProfession { Name = f.Str("name"), Skill = f.Int("skill"), Max = f.Int("max") };
    }

    // client -> server: one shared character (answered with armory.char)
    public sealed class ArmoryGetMsg
    {
        public const string Type = LodgeMsg.ArmoryGet;
        public string Owner, Key;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("owner", Owner); w.Set("key", Key); }
        public static ArmoryGetMsg Read(ILodgeFields f) => new ArmoryGetMsg { Owner = f.Str("owner"), Key = f.Str("key") };
    }

    // server -> client: one shared character { owner, by, key, char }
    public sealed class ArmoryCharMsg
    {
        public const string Type = LodgeMsg.ArmoryChar;
        public string Owner, By, Key;
        public object Char;            // the cleaned sheet, passed through as it is (see above)

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("owner", Owner); w.Set("by", By); w.Set("key", Key); if (Char != null) w.SetRawChild("char", Char); }
        public static ArmoryCharMsg Read(ILodgeFields f) => new ArmoryCharMsg { Owner = f.Str("owner"), By = f.Str("by"), Key = f.Str("key"), Char = f.RawChild("char") };
    }

    // client -> server: (armory2) who in the Lodge can craft these recipes { spells: [id...] } (the server also takes a single { spell })
    public sealed class ArmoryCraftersGetMsg
    {
        public const string Type = LodgeMsg.ArmoryCrafters;
        public List<long> Spells = new List<long>();
        public long? Spell;            // null = not sent

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type);
            var l = w.List("spells"); foreach (var s in Spells) l.Add(s);
            if (Spell is long one) w.Set("spell", one);
        }

        public static ArmoryCraftersGetMsg Read(ILodgeFields f) => new ArmoryCraftersGetMsg { Spells = new List<long>(f.Longs("spells")), Spell = f.Has("spell") ? f.Long("spell") : (long?)null };
    }

    // server -> client: the answer { spells (the ones asked, sorted), who: [crafter] }
    public sealed class ArmoryCraftersMsg
    {
        public const string Type = LodgeMsg.ArmoryCrafters;
        public List<long> Spells = new List<long>();
        public List<ArmoryCrafter> Who = new List<ArmoryCrafter>();

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type);
            var s = w.List("spells"); foreach (var x in Spells) s.Add(x);
            var l = w.List("who"); foreach (var c in Who) c.Write(l.Add());
        }

        public static ArmoryCraftersMsg Read(ILodgeFields f)
        {
            var m = new ArmoryCraftersMsg { Spells = new List<long>(f.Longs("spells")) };
            foreach (var c in f.List("who")) m.Who.Add(ArmoryCrafter.Read(c));
            return m;
        }
    }

    // a row of armory.crafters { spell, profession, skill, max, name, class, level, partial, owner, by, key, mine? (true) }
    public sealed class ArmoryCrafter
    {
        public int Spell, Skill, Max, Level;
        public string Profession, Name, Class, Owner, By, Key;
        public bool Partial;           // the profession's recipe list was not read in full
        public bool Mine;              // written only when true

        public void Write(ILodgeWriter w)
        {
            w.Set("spell", Spell); w.Set("profession", Profession); w.Set("skill", Skill); w.Set("max", Max);
            w.Set("name", Name); w.Set("class", Class); w.Set("level", Level); w.Set("partial", Partial);
            w.Set("owner", Owner); w.Set("by", By); w.Set("key", Key);
            if (Mine) w.Set("mine", true);
        }

        public static ArmoryCrafter Read(ILodgeFields f) => new ArmoryCrafter
        {
            Spell = f.Int("spell"), Profession = f.Str("profession"), Skill = f.Int("skill"), Max = f.Int("max"), Name = f.Str("name"), Class = f.Str("class"),
            Level = f.Int("level"), Partial = f.Bool("partial"), Owner = f.Str("owner"), By = f.Str("by"), Key = f.Str("key"), Mine = f.Bool("mine"),
        };
    }

    // client -> server: (reputations) where the Lodge's shared characters stand with a faction { faction }
    public sealed class ArmoryStandingGetMsg
    {
        public const string Type = LodgeMsg.ArmoryStanding;
        public int Faction;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("faction", Faction); }
        public static ArmoryStandingGetMsg Read(ILodgeFields f) => new ArmoryStandingGetMsg { Faction = f.Int("faction") };
    }

    // server -> client: the answer { faction, who: [standing] }
    public sealed class ArmoryStandingMsg
    {
        public const string Type = LodgeMsg.ArmoryStanding;
        public int Faction;
        public List<ArmoryStanding> Who = new List<ArmoryStanding>();

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("faction", Faction); var l = w.List("who"); foreach (var s in Who) s.Write(l.Add()); }

        public static ArmoryStandingMsg Read(ILodgeFields f)
        {
            var m = new ArmoryStandingMsg { Faction = f.Int("faction") };
            foreach (var s in f.List("who")) m.Who.Add(ArmoryStanding.Read(s));
            return m;
        }
    }

    // a row of armory.standing { faction, s (1 Hated .. 8 Exalted), v, name, class, level, at, min?, max?, owner, by, key, mine? (true) }
    public sealed class ArmoryStanding
    {
        public int Faction, S, Level;
        public long V;
        public long? Min, Max;         // null = not sent
        public long At;
        public string Name, Class, Owner, By, Key;
        public bool Mine;              // written only when true

        public void Write(ILodgeWriter w)
        {
            w.Set("faction", Faction); w.Set("s", S); w.Set("v", V); w.Set("name", Name); w.Set("class", Class); w.Set("level", Level); w.Set("at", At);
            if (Min is long mn) w.Set("min", mn);
            if (Max is long mx) w.Set("max", mx);
            w.Set("owner", Owner); w.Set("by", By); w.Set("key", Key);
            if (Mine) w.Set("mine", true);
        }

        public static ArmoryStanding Read(ILodgeFields f) => new ArmoryStanding
        {
            Faction = f.Int("faction"), S = f.Int("s"), V = f.Long("v"), Name = f.Str("name"), Class = f.Str("class"), Level = f.Int("level"), At = f.Long("at"),
            Min = f.Has("min") ? f.Long("min") : (long?)null, Max = f.Has("max") ? f.Long("max") : (long?)null,
            Owner = f.Str("owner"), By = f.Str("by"), Key = f.Str("key"), Mine = f.Bool("mine"),
        };
    }
}
