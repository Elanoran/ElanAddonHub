using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // The class page's "Spells by level" card: the tabs of the class' spellbook as chips, the filters (search, level range, only new spells, talent spells) and the spells grouped under
    // "Level N" headers. The filters live in this card (they are not part of the page's id); the groups are worked out by ClassSpellBook.Groups, in memory.
    public partial class AtlasPage
    {
        ClassSpellBook classBook;
        ClassSpellFilter classFilter = new ClassSpellFilter { Pet = false };
        StackPanel classGroups;
        TextBlock classCount;
        Button classClear;
        readonly Dictionary<string, FrameworkElement> classCtl = new Dictionary<string, FrameworkElement>();
        readonly List<AtlasRow> classShown = new List<AtlasRow>();
        List<LevelGroup> classLast = new List<LevelGroup>();
        readonly DispatcherTimer classDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        bool classWired, classSuppress;
        public const int ClassMaxRows = 400;

        public ClassSpellFilter ClassFilterForTest => classFilter;
        public IReadOnlyList<AtlasRow> ClassRowsForTest => classShown;
        public IReadOnlyList<LevelGroup> ClassGroupsForTest => classLast;
        public string ClassCountTextForTest => classCount?.Text ?? "";
        public FrameworkElement ClassControlForTest(string key) => classCtl.TryGetValue(key, out var c) ? c : null;
        public IEnumerable<string> ClassHeadersForTest => classGroups == null ? new string[0] : classGroups.Children.OfType<TextBlock>().Where(t => t.Tag as string == "level").Select(t => t.Text);
        public void ClassRefreshForTest() => ClassRefresh();

        FrameworkElement ClassSpellsCard(ClassSpellBook book)
        {
            classBook = book; classCtl.Clear(); classShown.Clear(); classLast = new List<LevelGroup>();
            classFilter = new ClassSpellFilter { Pet = false };
            if (!classWired) { classWired = true; classDebounce.Tick += (s, e) => { classDebounce.Stop(); ClassRefresh(); }; }
            classSuppress = true;
            var sp = new StackPanel();
            var head = new DockPanel();
            classCount = Tb("", "Text.Caption", Res("Brush.Text.Tertiary")); classCount.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(classCount, Dock.Right); head.Children.Add(classCount);
            head.Children.Add(Tb("Spells by level", "Text.Section"));
            sp.Children.Add(head);
            var note = Tb("What your game says each " + book.ClassName + " spell is learned at. Higher ranks of a spell are their own rows; the cost and cooldown are the client's.", "Text.Secondary", null, 2);
            note.TextWrapping = TextWrapping.Wrap; note.MaxWidth = ReadWidth; note.HorizontalAlignment = HorizontalAlignment.Left; sp.Children.Add(note);

            // the tabs
            var tabWrap = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            sp.Children.Add(tabWrap);
            void Chips()
            {
                tabWrap.Children.Clear();
                void Chip(string key, string text, int count, int skill, bool on)
                {
                    var b = new Button { Style = (Style)FindResource(on ? "Button.S" : "GhostButton"), Height = 28, Margin = new Thickness(0, 0, 8, 8), Tag = key, ToolTip = "Show " + text };
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    var t = Tb(text, "Text.Secondary", on ? Res("Brush.Text.Primary") : null); if (on) t.FontWeight = FontWeights.SemiBold; row.Children.Add(t);
                    var n = Tb(count.ToString("N0", Lodge.CalendarRules.English), "Text.Caption", Res("Brush.Text.Tertiary")); n.Margin = new Thickness(4, 0, 0, 0); row.Children.Add(n);
                    b.Content = row;
                    System.Windows.Automation.AutomationProperties.SetName(b, text);
                    b.Click += (s, e) => { classFilter.SkillId = skill; Chips(); ClassRefresh(); };
                    classCtl[key] = b;
                    tabWrap.Children.Add(b);
                }
                Chip("tab:0", "All", book.Entries.Count(e => !e.Pet && !e.Talent), 0, classFilter.SkillId == 0);
                foreach (var t in book.Tabs) Chip("tab:" + t.SkillId, t.Pet ? t.Name + " (pet)" : t.Name, book.Entries.Count(e => e.SkillId == t.SkillId && (classFilter.Talents || !e.Talent)), t.SkillId, classFilter.SkillId == t.SkillId);
            }
            Chips();

            // the filters
            var bar = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var search = new TextBox { Style = (Style)FindResource("Input"), Width = 200, Margin = new Thickness(0, 0, 8, 8), Tag = "Search spells", VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(search, "Search spells");
            search.TextChanged += (s, e) => { if (classSuppress) return; classFilter.Text = search.Text.Trim(); ClassSchedule(); };
            classCtl["search"] = search; bar.Children.Add(search);
            TextBox Lvl(string key, string ph, Action<int> set)
            {
                var t = new TextBox { Style = (Style)FindResource("Input"), Width = 92, Margin = new Thickness(0, 0, 8, 8), Tag = ph, MaxLength = 3, VerticalAlignment = VerticalAlignment.Center };
                System.Windows.Automation.AutomationProperties.SetName(t, ph);
                t.TextChanged += (s, e) => { if (classSuppress) return; set(int.TryParse(t.Text.Trim(), out var n) && n > 0 ? n : 0); ClassSchedule(); };
                classCtl[key] = t; bar.Children.Add(t);
                return t;
            }
            Lvl("min", "Min level", v => classFilter.MinLevel = v);
            Lvl("max", "Max level", v => classFilter.MaxLevel = v);
            CheckBox Sw(string key, string text, string tip, Action<bool> set)
            {
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 8), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
                var cb = new CheckBox { Style = (Style)FindResource("Switch"), VerticalAlignment = VerticalAlignment.Center, Tag = key };
                System.Windows.Automation.AutomationProperties.SetName(cb, text);
                cb.Click += (s, e) => { if (classSuppress) return; set(cb.IsChecked == true); Chips(); ClassRefresh(); };
                var t = Tb("  " + text, "Text.Secondary"); t.VerticalAlignment = VerticalAlignment.Center;
                cell.Children.Add(cb); cell.Children.Add(t); bar.Children.Add(cell);
                classCtl[key] = cb;
                return cb;
            }
            Sw("new", "Only new spells", "Hide the higher ranks: every spell once, at the level its first rank is learned", v => classFilter.NewOnly = v);
            Sw("talents", "Talent spells", "Also list the spells a talent teaches (they are learned with a talent point, not at a level)", v => classFilter.Talents = v);
            classClear = GhostButton("Clear filters", () =>
            {
                classSuppress = true;
                classFilter = new ClassSpellFilter { Pet = false };
                ((TextBox)classCtl["search"]).Text = ""; ((TextBox)classCtl["min"]).Text = ""; ((TextBox)classCtl["max"]).Text = "";
                ((CheckBox)classCtl["new"]).IsChecked = false; ((CheckBox)classCtl["talents"]).IsChecked = false;
                classSuppress = false;
                Chips(); ClassRefresh();
            }, "Back to every tab and every level");
            classClear.Margin = new Thickness(0, 0, 8, 8); classClear.Visibility = Visibility.Collapsed; classCtl["clear"] = classClear; bar.Children.Add(classClear);
            sp.Children.Add(bar);

            classGroups = new StackPanel { Margin = new Thickness(-8, 0, -8, 0) };
            sp.Children.Add(classGroups);
            classSuppress = false;
            ClassRefresh();
            return Card(sp);
        }

        void ClassSchedule() { classDebounce.Stop(); classDebounce.Start(); }

        bool ClassFiltered => classFilter.SkillId != 0 || classFilter.NewOnly || classFilter.Talents || classFilter.MinLevel > 0 || classFilter.MaxLevel > 0 || !string.IsNullOrEmpty(classFilter.Text);

        void ClassRefresh()
        {
            if (classBook == null || classGroups == null) return;
            var groups = classBook.Groups(classFilter);
            classLast = groups;
            classGroups.Children.Clear(); classShown.Clear();
            int total = groups.Sum(g => g.Entries.Count), shown = 0;
            var tpl = (DataTemplate)FindResource("AtlasRowTemplate");
            foreach (var g in groups)
            {
                if (shown >= ClassMaxRows) break;
                var take = g.Entries.Take(ClassMaxRows - shown).ToList();
                var h = Tb(g.Title + "  ", "Text.RowTitle", g.Level > 0 ? null : Res("Brush.Text.Secondary"), classGroups.Children.Count == 0 ? 12 : 16);
                h.Tag = "level"; h.Margin = new Thickness(8, h.Margin.Top, 8, 0);
                h.Text = g.Title;
                classGroups.Children.Add(h);
                var rows = take.Select(ClassSpellBook.RowOf).ToList();
                classShown.AddRange(rows);
                classGroups.Children.Add(new ItemsControl { ItemsSource = rows, ItemTemplate = tpl, Focusable = false });
                shown += take.Count;
            }
            if (total == 0)
            {
                var e = Tb(classBook.Total == 0 ? "The game data lists no spells for this class." : "No spell matches these filters.", "Text.Secondary", null, 12);
                e.Margin = new Thickness(8, 12, 8, 0); classGroups.Children.Add(e);
            }
            else if (total > shown)
            {
                var more = Tb((total - shown).ToString("N0", Lodge.CalendarRules.English) + " more spells are not drawn: narrow the list with a tab, a level range or the search.", "Text.Caption", Res("Brush.Text.Tertiary"), 8);
                more.Margin = new Thickness(8, 8, 8, 0); classGroups.Children.Add(more);
            }
            int levels = groups.Count(g => g.Level > 0);
            classCount.Text = AtlasFormat.Count(total, "spell", "spells") + (levels > 0 ? " at " + AtlasFormat.Count(levels, "level", "levels") : "");
            if (classClear != null) classClear.Visibility = ClassFiltered ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
