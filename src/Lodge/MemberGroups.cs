using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    public enum MemberGroup { InGame, Online, Away, Invisible, Hidden }

    // Which group of the left panel a person belongs to. One function, so the rules (and the "invisible never leaks" rule) live in one place:
    //  - an invisible member is only listed - in the dim "Invisible" group - for me myself and for a viewer who may see invisible members
    //    (the owner: the server only sends them to the owner anyway). For everybody else they are not listed at all.
    //  - away / busy (Do not disturb; the idle timer sets "away" too) = Away, whatever else they do
    //  - WoW running = In game, otherwise Online
    public static class MemberGrouping
    {
        public static MemberGroup Of(MemberVM m, bool viewerSeesInvisible)
        {
            if (m == null || m.Offline) return MemberGroup.Hidden;
            if (m.Invisible) return m.IsMe || viewerSeesInvisible ? MemberGroup.Invisible : MemberGroup.Hidden;
            if (m.Status == "away" || m.Status == "busy") return MemberGroup.Away;
            return m.Playing ? MemberGroup.InGame : MemberGroup.Online;
        }

        public static string Key(MemberGroup g) => g == MemberGroup.InGame ? "ingame" : g.ToString().ToLowerInvariant();
        public static string Title(MemberGroup g) => g == MemberGroup.InGame ? "In game" : g.ToString();
    }

    // one collapsible group header + its people
    public class MemberGroupVM : Bindable
    {
        public MemberGroup Group { get; }
        public string Key => MemberGrouping.Key(Group);
        public string Title => MemberGrouping.Title(Group);
        public ObservableCollection<MemberVM> Members { get; } = new ObservableCollection<MemberVM>();
        public int Count => Members.Count;
        public bool InGame => Group == MemberGroup.InGame;
        public double ListOpacity => Group == MemberGroup.Invisible ? 0.6 : 1;
        bool expanded = true;
        public bool Expanded { get => expanded; set { if (Set(ref expanded, value)) { Raise(nameof(Tip)); Toggled?.Invoke(this); } } }
        public string Tip => expanded ? "Click to collapse" : "Click to expand";
        public event Action<MemberGroupVM> Toggled;
        public MemberGroupVM(MemberGroup g) { Group = g; Members.CollectionChanged += (s, e) => Raise(nameof(Count)); }
    }

    // The grouped online list: keeps the groups in step with the session's members. People in a voice room are not listed here
    // (they show under their room, as before). Groups with nobody in them are not shown. Same objects stay (no flicker).
    public class MemberGroups
    {
        public ObservableCollection<MemberGroupVM> Groups { get; } = new ObservableCollection<MemberGroupVM>();
        readonly ObservableCollection<MemberVM> source;
        readonly Func<bool> seesInvisible;
        readonly Settings settings;
        readonly Dictionary<MemberGroup, MemberGroupVM> byGroup = new Dictionary<MemberGroup, MemberGroupVM>();
        readonly HashSet<MemberVM> watched = new HashSet<MemberVM>();
        bool busy;
        static readonly HashSet<string> Relevant = new HashSet<string>
        {
            nameof(MemberVM.Status), nameof(MemberVM.Invisible), nameof(MemberVM.Room), nameof(MemberVM.Playing),
            nameof(MemberVM.Display), nameof(MemberVM.CharName), nameof(MemberVM.Offline),
        };

        public MemberGroups(ObservableCollection<MemberVM> source, Func<bool> seesInvisible, Settings settings)
        {
            this.source = source; this.seesInvisible = seesInvisible; this.settings = settings;
            foreach (MemberGroup g in new[] { MemberGroup.InGame, MemberGroup.Online, MemberGroup.Away, MemberGroup.Invisible })
            {
                var vm = new MemberGroupVM(g) { Expanded = !CollapsedKeys().Contains(MemberGrouping.Key(g)) };
                vm.Toggled += OnToggled;
                byGroup[g] = vm;
            }
            source.CollectionChanged += (s, e) => Recompute();
            Recompute();
        }

        HashSet<string> CollapsedKeys() =>
            new HashSet<string>((settings?.LodgeGroupsCollapsed ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));

        void OnToggled(MemberGroupVM g)
        {
            if (settings == null) return;
            settings.LodgeGroupsCollapsed = string.Join(",", byGroup.Values.Where(x => !x.Expanded).Select(x => x.Key));
            try { SettingsStore.Save(settings); } catch { }
        }

        void Watch(MemberVM m)
        {
            if (watched.Add(m)) m.PropertyChanged += OnMemberChanged;
        }

        void OnMemberChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != null && Relevant.Contains(e.PropertyName)) Recompute();
        }

        public static IEnumerable<MemberVM> Sorted(IEnumerable<MemberVM> l) =>
            l.OrderBy(m => m.Display ?? "", StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Id);

        public void Recompute()
        {
            if (busy) return;
            busy = true;
            try
            {
                foreach (var m in source) Watch(m);
                foreach (var gone in watched.Where(w => !source.Contains(w)).ToList()) { gone.PropertyChanged -= OnMemberChanged; watched.Remove(gone); }

                bool sees = seesInvisible?.Invoke() ?? false;
                var want = new Dictionary<MemberGroup, List<MemberVM>>();
                foreach (var g in byGroup.Keys) want[g] = new List<MemberVM>();
                foreach (var m in Sorted(source))
                {
                    if (m.Room != null) continue;                       // under their voice room
                    var g = MemberGrouping.Of(m, sees);
                    if (g != MemberGroup.Hidden) want[g].Add(m);
                }
                foreach (var kv in want) Sync(byGroup[kv.Key].Members, kv.Value);

                var wantGroups = byGroup.Values.Where(g => g.Members.Count > 0).OrderBy(g => (int)g.Group).ToList();
                for (int i = Groups.Count - 1; i >= 0; i--) if (!wantGroups.Contains(Groups[i])) Groups.RemoveAt(i);
                for (int i = 0; i < wantGroups.Count; i++)
                {
                    if (i < Groups.Count && ReferenceEquals(Groups[i], wantGroups[i])) continue;
                    var at = Groups.IndexOf(wantGroups[i]);
                    if (at >= 0) Groups.Move(at, i); else Groups.Insert(i, wantGroups[i]);
                }
            }
            finally { busy = false; }
        }

        static void Sync(ObservableCollection<MemberVM> list, List<MemberVM> want)
        {
            for (int i = list.Count - 1; i >= 0; i--) if (!want.Contains(list[i])) list.RemoveAt(i);
            for (int i = 0; i < want.Count; i++)
            {
                if (i < list.Count && ReferenceEquals(list[i], want[i])) continue;
                var at = list.IndexOf(want[i]);
                if (at >= 0) list.Move(at, i); else list.Insert(i, want[i]);
            }
        }

        // test hook: the people listed in a group, by name
        public string[] NamesIn(MemberGroup g) => byGroup[g].Members.Select(m => m.Name).ToArray();
        public MemberGroupVM Of(MemberGroup g) => byGroup[g];
    }
}
