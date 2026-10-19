using System.Collections.Generic;

namespace Elans.Shared
{
    // Profile messages (server/PROTOCOL.md, 2.6 / 2.7 additions): the field names live only here. The classes only map fields; every rule
    // (avatars and accents allowed, lengths, display-name uniqueness, who sees what of a profile) stays in the Lodge server (Profiles.cs).
    // The server still validates a profile:set from the raw JSON (a field of the wrong type is refused there, and "left out" differs from
    // null), so ProfileSetMsg.Read is the lenient reading only.

    // A field of profile:set: left out (unchanged), or sent - a null value clears it.
    public struct LodgeOpt<T>
    {
        public bool Sent;
        public T Value;
        public static implicit operator LodgeOpt<T>(T value) => new LodgeOpt<T> { Sent = true, Value = value };
    }

    // client -> server: change your own profile; only the fields that are sent change (the answer is profile:data with your own view)
    public sealed class ProfileSetMsg
    {
        public const string Type = LodgeMsg.ProfileSet;
        public LodgeOpt<string> AvatarId, Accent, About, PlayTimes, DisplayName, Main, ShowChars, VisibleTo;
        public List<string> Hidden;    // characters never shown; null = not sent

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type);
            void S(string key, LodgeOpt<string> o) { if (o.Sent) w.Set(key, o.Value); }
            S("avatarId", AvatarId); S("accent", Accent); S("about", About); S("playTimes", PlayTimes); S("displayName", DisplayName);
            S("main", Main); S("showChars", ShowChars); S("visibleTo", VisibleTo);
            if (Hidden != null) { var l = w.List("hidden"); foreach (var h in Hidden) l.Add(h); }
        }

        public static ProfileSetMsg Read(ILodgeFields f)
        {
            LodgeOpt<string> S(string key) => f.Has(key) ? f.Str(key) : default(LodgeOpt<string>);
            return new ProfileSetMsg
            {
                AvatarId = S("avatarId"), Accent = S("accent"), About = S("about"), PlayTimes = S("playTimes"), DisplayName = S("displayName"),
                Main = S("main"), ShowChars = S("showChars"), VisibleTo = S("visibleTo"),
                Hidden = f.Has("hidden") ? new List<string>(f.Strs("hidden")) : null,
            };
        }
    }

    // client -> server: a member's profile by login name (online or not)
    public sealed class ProfileGetMsg
    {
        public const string Type = LodgeMsg.ProfileGet;
        public string Name;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("name", Name); }
        public static ProfileGetMsg Read(ILodgeFields f) => new ProfileGetMsg { Name = f.Str("name") };
    }

    // client -> server: (Perm.ResetProfiles) a member's avatar, accent and display name back to the default, optionally the texts too
    public sealed class ProfileResetMsg
    {
        public const string Type = LodgeMsg.ProfileReset;
        public string Name;
        public bool ClearText;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("name", Name); w.Set("clearText", ClearText); }
        public static ProfileResetMsg Read(ILodgeFields f) => new ProfileResetMsg { Name = f.Str("name"), ClearText = f.Bool("clearText") };
    }

    // server -> client: a profile (the answer to profile:get / profile:set / profile:reset)
    public sealed class ProfileDataMsg
    {
        public const string Type = LodgeMsg.ProfileData;
        public ProfileView Profile;

        public void Write(ILodgeWriter w) { w.Set("t", Type); if (Profile != null) Profile.Write(w.Child("profile")); }
        public static ProfileDataMsg Read(ILodgeFields f) { var p = f.Child("profile"); return new ProfileDataMsg { Profile = p == null ? null : ProfileView.Read(p) }; }
    }

    // server -> client: a member's avatar / accent / display name changed (to everyone who can see them)
    public sealed class ProfileMsg
    {
        public const string Type = LodgeMsg.Profile;
        public int Id;                 // the member's connection id
        public string Name, AvatarId, Accent;
        public string DisplayName;     // "" = none
        public bool DisplayNameSent;   // reading: the key was there (servers before 2.7 leave it out)

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("id", Id); w.Set("name", Name); w.Set("avatarId", AvatarId); w.Set("accent", Accent); w.Set("displayName", DisplayName); }
        public static ProfileMsg Read(ILodgeFields f) => new ProfileMsg
        {
            Id = f.Int("id"), Name = f.Str("name"), AvatarId = f.Str("avatarId"), Accent = f.Str("accent"), DisplayName = f.Str("displayName"), DisplayNameSent = f.Has("displayName"),
        };
    }

    // What a viewer gets of a profile: profile:data.profile and welcome.profile (your own).
    //   not found: { name, found: false }
    //   found:     { name, found, guest, role, online, restricted, avatarId, accent, displayName, showChars?, visibleTo? (your own),
    //                about, playTimes, game?, chars: [char], main? (these only when not restricted) }
    public sealed class ProfileView
    {
        public string Name;
        public bool Found, Guest, Online, Restricted;
        public string Role, AvatarId, Accent;
        public string DisplayName;     // "" = none
        public bool DisplayNameSent;   // reading: the key was there
        public string ShowChars, VisibleTo;    // your own profile only (null = not sent)
        public string About, PlayTimes, Main;  // Main: null = not sent
        public object Game;            // what they play now: the presence object, passed through as it is (see ILodgeFields.RawChild); null = not sent
        public List<ProfileChar> Chars = new List<ProfileChar>();

        public void Write(ILodgeWriter w)
        {
            w.Set("name", Name); w.Set("found", Found);
            if (!Found) return;
            w.Set("guest", Guest); w.Set("role", Role); w.Set("online", Online); w.Set("restricted", Restricted);
            w.Set("avatarId", AvatarId); w.Set("accent", Accent); w.Set("displayName", DisplayName);
            if (ShowChars != null) w.Set("showChars", ShowChars);
            if (VisibleTo != null) w.Set("visibleTo", VisibleTo);
            if (Restricted) return;
            w.Set("about", About); w.Set("playTimes", PlayTimes);
            if (Game != null) w.SetRawChild("game", Game);
            var l = w.List("chars");
            foreach (var c in Chars) c.Write(l.Add());
            if (Main != null) w.Set("main", Main);
        }

        public static ProfileView Read(ILodgeFields f)
        {
            var v = new ProfileView
            {
                Name = f.Str("name"), Found = f.Bool("found"), Guest = f.Bool("guest"), Role = f.Str("role"), Online = f.Bool("online"), Restricted = f.Bool("restricted"),
                AvatarId = f.Str("avatarId"), Accent = f.Str("accent"), DisplayName = f.Str("displayName"), DisplayNameSent = f.Has("displayName"),
                ShowChars = f.Str("showChars"), VisibleTo = f.Str("visibleTo"), About = f.Str("about"), PlayTimes = f.Str("playTimes"), Main = f.Str("main"),
                Game = f.RawChild("game"),
            };
            foreach (var c in f.List("chars")) v.Chars.Add(ProfileChar.Read(c));
            return v;
        }
    }

    // a character on a profile: { name, class, classFile, race, raceFile, level, main, seen? (left out for an invisible member), hidden? (your own) }
    public sealed class ProfileChar
    {
        public string Name, Class, ClassFile, Race, RaceFile;
        public int Level;
        public bool Main;
        public long? Seen;             // unix seconds
        public bool? Hidden;

        public void Write(ILodgeWriter w)
        {
            w.Set("name", Name); w.Set("class", Class); w.Set("classFile", ClassFile); w.Set("race", Race); w.Set("raceFile", RaceFile);
            w.Set("level", Level); w.Set("main", Main);
            if (Seen is long s) w.Set("seen", s);
            if (Hidden is bool h) w.Set("hidden", h);
        }

        public static ProfileChar Read(ILodgeFields f) => new ProfileChar
        {
            Name = f.Str("name"), Class = f.Str("class"), ClassFile = f.Str("classFile"), Race = f.Str("race"), RaceFile = f.Str("raceFile"),
            Level = f.Int("level"), Main = f.Bool("main"), Seen = f.Has("seen") ? f.Long("seen") : (long?)null, Hidden = f.Has("hidden") ? f.Bool("hidden") : (bool?)null,
        };
    }
}
