using System.Collections.Generic;

namespace Elans.Shared
{
    // The welcome (server/PROTOCOL.md: WebSocket, and the additions of every version since): the server's first frame on a connection,
    // sent again whenever what a member may see changes (a new rank, a channel added or removed). It is composed of the classes the other
    // messages already use - LodgeUser (users), LodgeChannelView (channels), ChatMessage (history), ChatPin (pins), CalWelcome (calendar),
    // ProfileView (profile), BadgesStateMsg (badges), LodgeInfoView (lodge) - plus WelcomeDevice and the name directory, which nothing else
    // carries. The classes only map fields; every rule stays in the Lodge server (Hub.cs Welcome): which channels, members and history a
    // rank may see, the masked entry of an invisible member, the permission flags (Roles.Can), the feature list (Hub.FeatureList).
    // The history and pins are the stored records, passed through as they are (raw, like history:page); the Outpost reads them as
    // ChatMessage / ChatPin. Every part may be missing from an older server: the Outpost reads what is there (a 1.x server sends no
    // channels and its history as one list, see OldHistory).
    //   { t, you, name, role, server, maxFileMb, canShareFiles, canModerate, canManage, canPin, features: [..], canManageFiles, filesMaxMb,
    //     canShareClassic, badges: { enabled, locked }, lodge: { name, welcome }, calendar: { now, events }, canInvite, canRetire, canPurge,
    //     device: { id, name } | null, reactions: [..], pins: { <channel>: [pin] }, channels: [channel], users: [user], profile,
    //     names: { <login>: <display name> }, history: { <channel>: [msg] } }
    public sealed class WelcomeMsg
    {
        public const string Type = LodgeMsg.Welcome;
        public int You;                                // the connection's member id (users[].id)
        public string Name, Role, Server;              // Server: the Lodge server's version
        public int MaxFileMb, FilesMaxMb;              // 0 = not sent (the Outpost's defaults apply)
        public bool CanShareFiles = true;              // read: true when missing (servers before 2.14 let everyone attach)
        public bool CanModerate, CanManage, CanPin, CanManageFiles, CanShareClassic, CanInvite, CanRetire, CanPurge;
        public List<string> Features = new List<string>();     // the string entries of features (Hub.FeatureList)
        public List<string> Reactions = new List<string>();    // the reaction ids, in order
        public BadgesStateMsg Badges;                  // null = not sent
        public LodgeInfoView Lodge;                    // null = not sent
        public CalWelcome Calendar;                    // null = not sent
        public WelcomeDevice Device;                   // null: written as JSON null (not signed in with a device token)
        public List<WelcomePins> Pins;                 // per text channel; null = not sent
        public List<LodgeChannelView> Channels = new List<LodgeChannelView>();
        public List<LodgeUser> Users = new List<LodgeUser>();
        public ProfileView Profile;                    // your own profile; null = not sent
        public List<KeyValuePair<string, string>> Names;   // login -> display name (a value that is no string reads as null); null = not sent
        public List<WelcomeHistory> History;           // per text channel; null = not sent as an object
        public List<ChatMessage> OldHistory = new List<ChatMessage>();   // read side: a 1.x server's history, one list for every channel

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("you", You); w.Set("name", Name); w.Set("role", Role); w.Set("server", Server);
            w.Set("maxFileMb", MaxFileMb);
            w.Set("canShareFiles", CanShareFiles); w.Set("canModerate", CanModerate); w.Set("canManage", CanManage); w.Set("canPin", CanPin);
            var fl = w.List("features");
            foreach (var x in Features) fl.Add(x);
            w.Set("canManageFiles", CanManageFiles); w.Set("filesMaxMb", FilesMaxMb); w.Set("canShareClassic", CanShareClassic);
            if (Badges != null) Badges.WriteState(w.Child("badges"));
            if (Lodge != null) Lodge.Write(w.Child("lodge"));
            if (Calendar != null) Calendar.Write(w.Child("calendar"));
            w.Set("canInvite", CanInvite); w.Set("canRetire", CanRetire); w.Set("canPurge", CanPurge);
            if (Device != null) Device.Write(w.Child("device")); else w.Set("device", (string)null);
            var rl = w.List("reactions");
            foreach (var x in Reactions) rl.Add(x);
            if (Pins != null) { var p = w.Child("pins"); foreach (var x in Pins) x.Write(p); }
            var cl = w.List("channels");
            foreach (var c in Channels) c.Write(cl.Add());
            var ul = w.List("users");
            foreach (var u in Users) u.Write(ul.Add());
            if (Profile != null) Profile.Write(w.Child("profile"));
            if (Names != null) { var n = w.Child("names"); foreach (var kv in Names) n.Set(kv.Key, kv.Value); }
            if (History != null) { var h = w.Child("history"); foreach (var x in History) x.Write(h); }
        }

        public static WelcomeMsg Read(ILodgeFields f)
        {
            var badges = f.Child("badges"); var lodge = f.Child("lodge"); var cal = f.Child("calendar"); var dev = f.Child("device");
            var profile = f.Child("profile");
            var m = new WelcomeMsg
            {
                You = f.Int("you"), Name = f.Str("name"), Role = f.Str("role"), Server = f.Str("server"),
                MaxFileMb = f.Int("maxFileMb"), FilesMaxMb = f.Int("filesMaxMb"),
                CanShareFiles = !f.Has("canShareFiles") || f.Bool("canShareFiles"),
                CanModerate = f.Bool("canModerate"), CanManage = f.Bool("canManage"), CanPin = f.Bool("canPin"), CanManageFiles = f.Bool("canManageFiles"),
                CanShareClassic = f.Bool("canShareClassic"), CanInvite = f.Bool("canInvite"), CanRetire = f.Bool("canRetire"), CanPurge = f.Bool("canPurge"),
                Features = ChatMessage.Copy(f.Strs("features")), Reactions = ChatMessage.Copy(f.Strs("reactions")),
                Badges = badges == null ? null : BadgesStateMsg.Read(badges), Lodge = lodge == null ? null : LodgeInfoView.Read(lodge),
                Calendar = cal == null ? null : CalWelcome.Read(cal), Device = dev == null ? null : WelcomeDevice.Read(dev),
                Profile = profile == null ? null : ProfileView.Read(profile),
            };
            foreach (var c in f.List("channels")) m.Channels.Add(LodgeChannelView.Read(c));
            foreach (var u in f.List("users")) m.Users.Add(LodgeUser.Read(u));
            var pins = f.Child("pins");
            if (pins != null) { m.Pins = new List<WelcomePins>(); foreach (var ch in pins.Keys()) m.Pins.Add(WelcomePins.Read(pins, ch)); }
            var names = f.Child("names");
            if (names != null) { m.Names = new List<KeyValuePair<string, string>>(); foreach (var k in names.Keys()) m.Names.Add(new KeyValuePair<string, string>(k, names.Str(k))); }
            var history = f.Child("history");
            if (history != null) { m.History = new List<WelcomeHistory>(); foreach (var ch in history.Keys()) m.History.Add(WelcomeHistory.Read(history, ch)); }
            else foreach (var x in f.List("history")) m.OldHistory.Add(ChatMessage.Read(x));
            return m;
        }
    }

    // welcome.device: the device this connection signed in with (device tokens, 2.10) { id, name }
    public sealed class WelcomeDevice
    {
        public string Id, Name;

        public void Write(ILodgeWriter w) { w.Set("id", Id); w.Set("name", Name); }
        public static WelcomeDevice Read(ILodgeFields f) => new WelcomeDevice { Id = f.Str("id"), Name = f.Str("name") };
    }

    // one text channel's part of welcome.history: its newest stored messages, oldest first
    public sealed class WelcomeHistory
    {
        public string Channel;
        // the stored records as they are (JsonArray on the server, List<object> in the Outpost - see ILodgeFields.RawList), passed through
        // unchanged. Null: Items is written instead.
        public object Messages;
        // read side: the typed view of every object in the list; an entry without a channel key gets this channel (as the Outpost did)
        public List<ChatMessage> Items = new List<ChatMessage>();

        public void Write(ILodgeWriter history)
        {
            if (Messages != null) history.SetRawList(Channel, Messages);
            else { var l = history.List(Channel); foreach (var m in Items) m.Write(l.Add()); }
        }

        public static WelcomeHistory Read(ILodgeFields history, string channel)
        {
            var h = new WelcomeHistory { Channel = channel, Messages = history.RawList(channel) };
            foreach (var x in history.List(channel))
            {
                var m = ChatMessage.Read(x);
                if (!x.Has("channel")) m.Channel = channel;
                h.Items.Add(m);
            }
            return h;
        }
    }

    // one text channel's part of welcome.pins: its pins as the server stores them
    public sealed class WelcomePins
    {
        public string Channel;
        public object Pins;                            // the stored pins as they are (see WelcomeHistory.Messages); null: Items is written
        public List<ChatPin> Items = new List<ChatPin>();  // read side: the typed view of every object in the list

        public void Write(ILodgeWriter pins)
        {
            if (Pins != null) pins.SetRawList(Channel, Pins);
            else { var l = pins.List(Channel); foreach (var p in Items) p.Write(l.Add()); }
        }

        public static WelcomePins Read(ILodgeFields pins, string channel)
        {
            var p = new WelcomePins { Channel = channel, Pins = pins.RawList(channel) };
            foreach (var x in pins.List(channel)) p.Items.Add(ChatPin.Read(x));
            return p;
        }
    }
}
