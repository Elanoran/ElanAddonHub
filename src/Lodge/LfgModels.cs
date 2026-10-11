using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // "Looking for group" sign-up cards (Lodge server 2.9). A card is a chat message with kind "lfg" and a structured payload; the server keeps
    // the rules (one place per person, role limits, closed cards can't change) - this only shows the state it sends and asks for changes.

    // a few classic dungeons and raids for the title field's suggestions (a title can be anything)
    public static class LfgCatalog
    {
        public static readonly string[] Instances =
        {
            "Ragefire Chasm", "The Deadmines", "Wailing Caverns", "Shadowfang Keep", "Blackfathom Deeps", "The Stockade", "Gnomeregan", "Razorfen Kraul",
            "Scarlet Monastery", "Razorfen Downs", "Uldaman", "Zul'Farrak", "Maraudon", "Temple of Atal'Hakkar", "Blackrock Depths", "Lower Blackrock Spire",
            "Upper Blackrock Spire", "Dire Maul", "Stratholme", "Scholomance", "Molten Core", "Onyxia's Lair", "Blackwing Lair", "Zul'Gurub",
            "Ruins of Ahn'Qiraj", "Temple of Ahn'Qiraj", "Naxxramas",
        };

        // typed text -> up to `max` instances: starts-with first, then contains (case-insensitive); nothing typed = the first few
        public static List<string> Suggest(string typed, int max = 6)
        {
            typed = (typed ?? "").Trim();
            if (typed.Length == 0) return Instances.Take(max).ToList();
            var starts = Instances.Where(i => i.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase));
            var has = Instances.Where(i => i.IndexOf(typed, StringComparison.CurrentCultureIgnoreCase) >= 0);
            var list = starts.Concat(has).Distinct().Take(max).ToList();
            return list.Count == 1 && string.Equals(list[0], typed, StringComparison.CurrentCultureIgnoreCase) ? new List<string>() : list;
        }
    }

    // one entry of a role row: a person who signed up, or an open place
    public class LfgSlotVM : Bindable
    {
        public string Name { get; }
        public bool Filled => Name != null;
        public MemberVM Member { get; }     // live member (class colour, avatar) or a stand-in for someone who went offline
        public string Shown => Name == null ? "" : DisplayNames.Shown(Name);
        public Brush NameBrush => Member != null && !Member.Offline ? Member.ClassBrushOrText : Avatar.Res("Brush.Text.Primary");
        public Visibility FilledVisibility => Filled ? Visibility.Visible : Visibility.Collapsed;
        public Visibility OpenVisibility => Filled ? Visibility.Collapsed : Visibility.Visible;
        public bool IsMe { get; }
        public LfgSlotVM(string name, MemberVM member, bool isMe) { Name = name; Member = member; IsMe = isMe; }

        public static MemberVM StandIn(string name)
        {
            var m = new MemberVM { Id = 0, Name = name, Offline = true };
            var d = DisplayNames.Shown(name);
            m.SetDisplayName(d == name ? "" : d);
            var known = MessageVM.AvatarLookup?.Invoke(name);
            if (known != null) m.SetAvatar(known[0], known[1]);
            return m;
        }
    }

    public class LfgRoleVM : Bindable
    {
        public const int ShownOpen = 5;   // open places drawn at most; the rest is "+N open"
        public string Id { get; }
        public string Label { get; }
        public int Total { get; private set; }
        public int Taken { get; private set; }
        public ObservableCollection<LfgSlotVM> Slots { get; } = new ObservableCollection<LfgSlotVM>();
        public int MoreOpen { get; private set; }
        public string CountText => Taken + "/" + Total;
        public string MoreText => MoreOpen > 0 ? "+" + MoreOpen + " open" : "";
        public Visibility MoreVisibility => MoreOpen > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RowVisibility => Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        public bool Full => Taken >= Total;
        public LfgRoleVM(string id, string label) { Id = id; Label = label; }

        public void Set(int total, IEnumerable<LfgSlotVM> filled, bool showOpen)
        {
            Total = total;
            var list = filled.ToList();
            Taken = list.Count;
            int open = Math.Max(0, total - list.Count);
            int draw = showOpen ? Math.Min(open, ShownOpen) : 0;
            MoreOpen = showOpen ? open - draw : 0;
            Slots.Clear();
            foreach (var f in list) Slots.Add(f);
            for (int i = 0; i < draw; i++) Slots.Add(new LfgSlotVM(null, null, false));
            Raise(nameof(Total)); Raise(nameof(Taken)); Raise(nameof(CountText)); Raise(nameof(MoreText)); Raise(nameof(MoreVisibility)); Raise(nameof(RowVisibility)); Raise(nameof(Full));
        }
    }

    public class LfgVM : Bindable
    {
        public static Func<DateTime> Clock = () => DateTime.UtcNow;   // tests drive time
        public static Func<string, MemberVM> MemberOf = n => MessageVM.AuthorLookup?.Invoke(n);
        public static Func<string> MyName = () => null;
        public static Func<bool> IAmOfficer = () => false;

        public MessageVM Msg { get; set; }
        public string Title { get; private set; } = "";
        public string Note { get; private set; } = "";
        public string Status { get; private set; } = "open";
        public string By { get; private set; }
        public bool IsNow { get; private set; }
        public DateTime StartUtc { get; private set; }
        public LfgRoleVM Tank { get; } = new LfgRoleVM("tank", "Tank");
        public LfgRoleVM Healer { get; } = new LfgRoleVM("healer", "Healer");
        public LfgRoleVM Dps { get; } = new LfgRoleVM("dps", "DPS");
        public LfgRoleVM Maybe { get; } = new LfgRoleVM("maybe", "Maybe");
        public IEnumerable<LfgRoleVM> Roles => new[] { Tank, Healer, Dps };
        public string MyRole { get; private set; }           // tank | healer | dps | maybe | null

        public bool IsOpen => Status == "open";
        public string ByShown => By == null ? "" : DisplayNames.Shown(By);
        public bool IAmAuthor => By != null && string.Equals(By, MyName(), StringComparison.OrdinalIgnoreCase);
        public bool CanEnd => IsOpen && (IAmAuthor || IAmOfficer());

        public void Apply(Dictionary<string, object> d)
        {
            if (d == null) return;
            Title = SafeFile.Text(d.Str("title") ?? "");
            Note = SafeFile.Text(d.Str("note") ?? "");
            Status = d.Str("status") ?? "open";
            By = d.Str("by");
            IsNow = d.Bool("now");
            StartUtc = DateTimeOffset.FromUnixTimeMilliseconds(d.Long("start")).UtcDateTime;
            var slots = d.Child("slots");
            var entries = d.List("signups").OfType<Dictionary<string, object>>().Select(x => (name: x.Str("n"), role: x.Str("r"))).Where(x => !string.IsNullOrEmpty(x.name)).ToList();
            var me = MyName();
            MyRole = entries.Where(x => me != null && string.Equals(x.name, me, StringComparison.OrdinalIgnoreCase)).Select(x => x.role).FirstOrDefault();
            LfgSlotVM Slot(string n) => new LfgSlotVM(n, MemberOf(n) ?? LfgSlotVM.StandIn(n), me != null && string.Equals(n, me, StringComparison.OrdinalIgnoreCase));
            foreach (var r in Roles)
                r.Set(slots?.Int(r.Id) ?? 0, entries.Where(x => x.role == r.Id).Select(x => Slot(x.name)), IsOpen);
            Maybe.Set(entries.Count(x => x.role == "maybe"), entries.Where(x => x.role == "maybe").Select(x => Slot(x.name)), false);
            RaiseAll();
        }

        // "Today 20:00" / "Tomorrow 20:00" / "Sat 12 Oct 20:00", in the viewer's local time; "Now" for a group that forms right away
        public string TimeText
        {
            get
            {
                if (IsNow) return "Now";
                var local = StartUtc.ToLocalTime();
                var today = Clock().ToLocalTime().Date;
                if (local.Date == today) return "Today " + local.ToString("HH:mm", CultureInfo.CurrentCulture);
                if (local.Date == today.AddDays(1)) return "Tomorrow " + local.ToString("HH:mm", CultureInfo.CurrentCulture);
                return local.ToString("ddd d MMM HH:mm", CalendarRules.English);
            }
        }

        // "in 25 min" / "in 2 h 5 min" / "started 10 min ago"; empty for "now" cards and finished ones
        public string Countdown => CountdownFor(StartUtc, Clock(), IsNow, IsOpen);

        public static string CountdownFor(DateTime startUtc, DateTime nowUtc, bool isNow, bool open)
        {
            if (isNow || !open) return "";
            var d = startUtc - nowUtc;
            if (d.TotalSeconds > 0)
            {
                if (d.TotalMinutes < 1) return "in less than a minute";
                if (d.TotalMinutes < 60) return "in " + (int)Math.Ceiling(d.TotalMinutes) + " min";
                if (d.TotalHours < 24)
                {
                    int h = (int)d.TotalHours, m = (int)Math.Round(d.TotalMinutes - h * 60);
                    if (m == 60) { h++; m = 0; }
                    return "in " + h + " h" + (m > 0 ? " " + m + " min" : "");
                }
                int days = (int)Math.Round(d.TotalDays);
                return "in " + days + (days == 1 ? " day" : " days");
            }
            var ago = -d;
            if (ago.TotalMinutes < 1) return "starting now";
            if (ago.TotalMinutes < 120) return "started " + (int)ago.TotalMinutes + " min ago";
            return "started";
        }

        public string WhenLine => Countdown.Length > 0 ? TimeText + "  ·  " + Countdown : TimeText;
        public string StatusText => Status == "cancelled" ? "Cancelled" : Status == "closed" ? "Closed" : "Open";
        public string StatusTip => Status == "cancelled" ? "The organiser cancelled this group" : Status == "closed" ? "The group is set - no more changes" : "Sign up with the buttons below";
        public Brush StatusFill => Avatar.Res(Status == "cancelled" ? "Brush.Danger.Tint" : Status == "closed" ? "Brush.Overlay.Chip" : "Brush.Success.Tint");
        public Brush StatusBrush => Avatar.Res(Status == "cancelled" ? "Brush.Danger" : Status == "closed" ? "Brush.Text.Secondary" : "Brush.Success");
        public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility MaybeVisibility => Maybe.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string MaybeNames => string.Join(", ", Maybe.Slots.Select(s => s.Shown));

        // buttons
        public Visibility ActionsVisibility => IsOpen ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TankVisibility => IsOpen && Tank.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility HealerVisibility => IsOpen && Healer.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DpsVisibility => IsOpen && Dps.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        public bool TankEnabled => MyRole == "tank" ? false : !Tank.Full;
        public bool HealerEnabled => MyRole == "healer" ? false : !Healer.Full;
        public bool DpsEnabled => MyRole == "dps" ? false : !Dps.Full;
        public bool MaybeEnabled => MyRole != "maybe";
        public Visibility LeaveVisibility => MyRole != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility EndVisibility => CanEnd ? Visibility.Visible : Visibility.Collapsed;
        public string MyLine => !IsOpen ? "" : MyRole == null ? "" : MyRole == "maybe" ? "You are a maybe" : "You are in as " + (MyRole == "tank" ? "Tank" : MyRole == "healer" ? "Healer" : "DPS");
        public Visibility MyLineVisibility => MyLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public double CardOpacity => IsOpen ? 1 : 0.7;

        public string Summary => Title + " - " + TimeText + " - " + string.Join(", ", Roles.Where(r => r.Total > 0).Select(r => r.Label + " " + r.CountText));

        // the countdown moves on without any frame: the view's timer asks for a refresh
        string lastWhen = "";
        public bool RefreshClock()
        {
            var w = WhenLine;
            if (w == lastWhen) return false;
            lastWhen = w;
            Raise(nameof(Countdown)); Raise(nameof(WhenLine)); Raise(nameof(TimeText));
            return true;
        }

        public void RaiseAll()
        {
            lastWhen = WhenLine;
            foreach (var n in new[] { nameof(Title), nameof(Note), nameof(Status), nameof(By), nameof(ByShown), nameof(IsOpen), nameof(IsNow), nameof(MyRole), nameof(TimeText), nameof(Countdown), nameof(WhenLine),
                nameof(StatusText), nameof(StatusTip), nameof(StatusFill), nameof(StatusBrush), nameof(NoteVisibility), nameof(MaybeVisibility), nameof(MaybeNames), nameof(ActionsVisibility),
                nameof(TankVisibility), nameof(HealerVisibility), nameof(DpsVisibility), nameof(TankEnabled), nameof(HealerEnabled), nameof(DpsEnabled), nameof(MaybeEnabled), nameof(LeaveVisibility),
                nameof(EndVisibility), nameof(MyLine), nameof(MyLineVisibility), nameof(CardOpacity), nameof(Summary), nameof(CanEnd) }) Raise(n);
        }
    }
}
