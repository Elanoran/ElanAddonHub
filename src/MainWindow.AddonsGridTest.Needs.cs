using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "addonsgrid", part 2 (3.8.2): our addons' order and what they need. The order rule on the real six ids and with new ones,
    // the built-in needs map used only without a manifest "needs" (and "needs" winning over it), then the page itself on a fixture WoW folder
    // with the real six: the order on screen, Hunter Helper's faded Install pill with "Needs Elan's Toolkit" and the inline install (a fake
    // installer, the real install path around it), the pill turning on by itself, the warning before removing what another addon needs, and
    // a bulk install putting the needs first. Pictures (ELANSHUB_SHOTS=1): addons-needs-*.png.
    public partial class MainWindow
    {
        static AddonInfo NeedsFixture(string id, string name, bool required = false, List<string> classes = null, List<string> needs = null) => new AddonInfo
        {
            Id = id, Name = name, Required = required, Classes = classes, Needs = needs, Version = "1.0.0", Flavor = "_classic_beta_", FlavorName = "WoW Forever (test)",
            Folders = new List<string> { id }, Description = "Self-test addon " + name, Changelog = new List<ChangeEntry> { new ChangeEntry { Version = "1.0.0", Text = "First test release" } },
        };

        // the six as the real manifest lists them (Hub required, the Paladin and Priest helpers with a class, no "needs" anywhere)
        static List<AddonInfo> RealSix() => new List<AddonInfo>
        {
            NeedsFixture("ElansHunterHelper", "Elan's Hunter Helper"),
            NeedsFixture("ElansHub", "Elan's Hub", required: true),
            NeedsFixture("ElansPaladinHelper", "Elan's Paladin Helper", classes: new List<string> { "PALADIN" }),
            NeedsFixture("ElansPriestHelper", "Elan's Priest Helper", classes: new List<string> { "PRIEST" }),
            NeedsFixture("ElansBags", "Elan's Bags"),
            NeedsFixture("ElansToolkit", "Elan's Toolkit"),
        };

        delegate void TestCheck(string what, bool cond, string detail = null);

        async Task AddonsNeedsTest(string dir, TestCheck Check)
        {
            string Ids(IEnumerable<AddonInfo> l) => string.Join(",", l.Select(a => a.Id));
            var expected = "ElansHub,ElansToolkit,ElansHunterHelper,ElansPaladinHelper,ElansPriestHelper,ElansBags";

            // ---- the order rule
            var six = RealSix();
            Check("order: the real six -> Hub, Toolkit, the class helpers (Hunter, Paladin, Priest), Bags", Ids(AddonOrder.Sort(six)) == expected, Ids(AddonOrder.Sort(six)));
            Check("order: the groups behind it - Hub required (0), Toolkit needed by the Hunter Helper (1), the helpers class addons (2), Bags the rest (3)",
                AddonOrder.Group(six[1], six) == 0 && AddonOrder.Group(six[5], six) == 1 && AddonOrder.Group(six[0], six) == 2 && AddonOrder.Group(six[2], six) == 2 && AddonOrder.Group(six[4], six) == 3);
            var rev = RealSix(); rev.Reverse();
            Check("order: the same whatever order the manifest has", Ids(AddonOrder.Sort(rev)) == expected, Ids(AddonOrder.Sort(rev)));
            var tkReq = RealSix(); tkReq[5].Required = true;
            Check("order: Toolkit marked required too -> still Hub first, then Toolkit (known ids break the tie)", Ids(AddonOrder.Sort(tkReq)) == expected, Ids(AddonOrder.Sort(tkReq)));
            var more = RealSix();
            more.Insert(0, NeedsFixture("ElansZed", "Elan's Zed"));
            more.Add(NeedsFixture("ElansAardvark", "Elan's Aardvark"));
            more.Add(NeedsFixture("ElansMageHelper", "Elan's Mage Helper", classes: new List<string> { "MAGE" }));
            Check("order: new addons - a class helper among the helpers by name, the others after Bags by name",
                Ids(AddonOrder.Sort(more)) == "ElansHub,ElansToolkit,ElansHunterHelper,ElansMageHelper,ElansPaladinHelper,ElansPriestHelper,ElansBags,ElansAardvark,ElansZed", Ids(AddonOrder.Sort(more)));

            // ---- needs: the built-in map only without a manifest "needs"
            Check("needs: Hunter Helper without \"needs\" -> the built-in map (Elan's Toolkit)", AddonOrder.Needs(six[0]).SequenceEqual(new[] { "ElansToolkit" }), string.Join(",", AddonOrder.Needs(six[0])));
            Check("needs: the others without \"needs\" -> nothing (OptionalDeps are not needs)", six.Skip(1).All(a => AddonOrder.Needs(a).Count == 0));
            Check("needs: a manifest \"needs\": [] wins over the map (needs nothing)", AddonOrder.Needs(NeedsFixture("ElansHunterHelper", "H", needs: new List<string>())).Count == 0);
            Check("needs: a manifest \"needs\" list wins over the map", AddonOrder.Needs(NeedsFixture("ElansHunterHelper", "H", needs: new List<string> { "ElansHub", "elanshub", "", "ElansHunterHelper" })).SequenceEqual(new[] { "ElansHub" }));
            var parsed = Util.FromJson<Manifest>("{\"addons\":[{\"id\":\"ElansHunterHelper\"},{\"id\":\"ElansHunterHelper\",\"needs\":[]},{\"id\":\"ElansBags\",\"needs\":[\"ElansToolkit\"]}]}");
            Check("needs: read from the manifest - absent = null (the map), [] = empty, a list = that list",
                parsed.Addons[0].Needs == null && AddonOrder.Needs(parsed.Addons[0]).SequenceEqual(new[] { "ElansToolkit" }) && parsed.Addons[1].Needs?.Count == 0 && AddonOrder.Needs(parsed.Addons[1]).Count == 0
                && AddonOrder.Needs(parsed.Addons[2]).SequenceEqual(new[] { "ElansToolkit" }));
            var noNeed = RealSix(); noNeed[0].Needs = new List<string>();
            Check("needs: the order follows them - when the manifest says the Hunter Helper needs nothing, Toolkit is one of 'the rest' (known before Bags)",
                Ids(AddonOrder.Sort(noNeed)) == "ElansHub,ElansHunterHelper,ElansPaladinHelper,ElansPriestHelper,ElansToolkit,ElansBags", Ids(AddonOrder.Sort(noNeed)));
            Check("bulk install: the needs come first, each once", string.Join(",", AddonOrder.WithNeedsFirst(new[] { "ElansHunterHelper", "ElansBags", "ElansToolkit" }, six)) == "ElansToolkit,ElansHunterHelper,ElansBags",
                string.Join(",", AddonOrder.WithNeedsFirst(new[] { "ElansHunterHelper", "ElansBags", "ElansToolkit" }, six)));

            // ---- the page with the real six on a fixture WoW folder
            var savedMan = manifest; var savedRoot = settings.WowRoot; var savedHook = Dialog.TestHook;
            var savedSkip = settings.SkipVersions == null ? null : new Dictionary<string, string>(settings.SkipVersions);
            var root = Path.Combine(dir, "needsroot");
            try
            {
                if (Directory.Exists(root)) Util.DeleteDir(root);
                var addOns = WowLocator.AddOnsDir(root, "_classic_beta_");
                Directory.CreateDirectory(addOns);
                void Put(string id, string v) { Directory.CreateDirectory(Path.Combine(addOns, id)); File.WriteAllText(Path.Combine(addOns, id, id + ".toc"), "## Interface: 11507\n## Title: " + id + "\n## Version: " + v + "\n"); }
                Put("ElansHub", "1.0.0");
                var fake = new List<string>();
                InstallerForTest = async (r, a, p, dev) => { await Task.Delay(60); fake.Add(a.Id); Put(a.Id, a.Version); p?.Report(1); };
                manifest = new Manifest { Hub = savedMan?.Hub, Addons = RealSix(), Homes = savedMan?.Homes };
                settings.WowRoot = root;
                RebuildCards();
                foreach (var c in cards) c.SetStats(new AddonStats { Released = DateTime.UtcNow.AddDays(-3), Downloads = 1234, Releases = 7 });
                SetAddonsView(false, false);
                await SetWidth(1300);
                AddonsPage.ScrollToTop();
                await Task.Delay(400); UpdateLayout();
                Check("page: the cards in the rule's order (Hub, Toolkit, Hunter, Paladin, Priest, Bags)", string.Join(",", cards.Select(c => c.Info.Id)) == expected, string.Join(",", cards.Select(c => c.Info.Id)));
                var shown = Enumerable.Range(0, cards.Count).Select(i => (CardVisual(Cards, i)?.DataContext as AddonCard)?.Info.Id).ToList();
                Check("page: on screen in that order too (row by row)", string.Join(",", shown) == expected && PosIn(CardVisual(Cards, 1), AddonsRoot).X > PosIn(CardVisual(Cards, 0), AddonsRoot).X + 300, string.Join(",", shown));

                var hunter = cards.First(c => c.Info.Id == "ElansHunterHelper");
                var toolkit = cards.First(c => c.Info.Id == "ElansToolkit");
                FrameworkElement V(AddonCard c) => CardVisual(Cards, cards.IndexOf(c));
                Button Pill(AddonCard c) => FindDown<Button>(V(c));
                Button Inline(AddonCard c) => FindDown<Button>(V(c), b => b.Name == "NeedsInstall");
                FrameworkElement Note(AddonCard c) => FindDown<StackPanel>(V(c), s => s.Name == "NeedsNote");
                var pill = Pill(hunter); var inl = Inline(hunter);
                Check("blocked: the Hunter Helper's Install pill is there but faded and not clickable or focusable, its tooltip says why",
                    hunter.State == CardState.NotInstalled && hunter.Blocked && !hunter.ButtonEnabled && !hunter.PillClickable && pill != null && pill.IsVisible && pill.Opacity < 0.6 && !pill.Focusable
                    && hunter.PillText == "Install" && hunter.PillTip.Contains("Elan's Toolkit"), pill?.Opacity + " " + hunter.PillTip);
                Check("blocked: the calm notice 'Needs Elan's Toolkit' in the subtitle's place, its tooltip explains",
                    Note(hunter)?.IsVisible == true && AtlasWalkTexts(V(hunter)).Contains("Needs Elan's Toolkit") && !AtlasWalkTexts(V(hunter)).Contains(hunter.Subtitle)
                    && (Note(hunter).ToolTip as string)?.Contains("doesn't load in game without it") == true, string.Join(" | ", AtlasWalkTexts(V(hunter))));
                Check("blocked: the inline 'Install Elan's Toolkit' button is visible, enabled, keyboard-focusable, has a tooltip and sits left of the pill inside the card",
                    inl != null && inl.IsVisible && inl.IsEnabled && inl.Focusable && (inl.Content as string) == "Install Elan's Toolkit" && inl.ToolTip is string
                    && PosIn(inl, AddonsRoot).X + inl.ActualWidth <= PosIn(pill, AddonsRoot).X + 0.5 && PosIn(inl, AddonsRoot).X >= PosIn(V(hunter), AddonsRoot).X, inl?.Content as string);
                Check("blocked: the card keeps its height (the notice replaces the subtitle, the button the counts)", Math.Abs(V(hunter).ActualHeight - (double)FindResource("Size.Card.Addon")) < 0.5, V(hunter).ActualHeight.ToString());
                Check("not blocked: the others have no notice and a full pill (Hub up to date, Toolkit / Paladin / Priest / Bags Install)",
                    cards.Where(c => c != hunter).All(c => !c.Blocked && Note(c)?.IsVisible != true && Inline(c)?.IsVisible != true && Math.Abs(Pill(c).Opacity - 1) < 0.01)
                    && cards[0].State == CardState.UpToDate && toolkit.PillClickable);
                Check("keyboard: the inline button takes focus", inl != null && inl.Focus() && inl.IsKeyboardFocused);
                await ShotWait(300);
                Snapshot(Path.Combine(dir, "addons-needs-order-1300.png"));
                await SetWidth(1700); await Task.Delay(300); UpdateLayout();
                Snapshot(Path.Combine(dir, "addons-needs-order-1700.png"));
                await SetWidth(760); await Task.Delay(300); UpdateLayout();
                Check("760: the blocked card's inline button and faded pill both fit in the card", Inline(hunter)?.IsVisible == true && PosIn(Inline(hunter), AddonsRoot).X + Inline(hunter).ActualWidth <= PosIn(Pill(hunter), AddonsRoot).X + 0.5);
                await SetWidth(1300); await Task.Delay(300); UpdateLayout();

                // ---- the inline install: only the need, through Install; the pill turns on by itself
                Inline(hunter).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var turnedOn = await Until(() => toolkit.State == CardState.UpToDate && !hunter.Blocked && hunter.PillClickable, 5000);
                await Task.Delay(100); UpdateLayout();
                Check("inline install: installs Elan's Toolkit only, through the normal install path (its card says Installed)",
                    fake.SequenceEqual(new[] { "ElansToolkit" }) && toolkit.State == CardState.UpToDate && hunter.State == CardState.NotInstalled, string.Join(",", fake) + " " + toolkit.State);
                Check("inline install: the Hunter Helper's pill then turns on by itself (full, clickable, focusable), the notice and the inline button go",
                    turnedOn && hunter.ButtonEnabled && Math.Abs(Pill(hunter).Opacity - 1) < 0.01 && Pill(hunter).Focusable && Note(hunter)?.IsVisible != true && Inline(hunter)?.IsVisible != true
                    && AtlasWalkTexts(V(hunter)).Contains(hunter.Subtitle), hunter.State + " " + Pill(hunter)?.Opacity);
                Snapshot(Path.Combine(dir, "addons-needs-after-1300.png"));

                // ---- removing what another addon needs: asked first, naming it
                await Install(hunter);
                Check("the Hunter Helper installs once its need is there", hunter.State == CardState.UpToDate && fake.Last() == "ElansHunterHelper", hunter.State.ToString());
                DialogCall asked = null;
                Dialog.TestHook = c => { asked = c; return false; };
                toolkit.Expanded = true; await Task.Delay(700); UpdateLayout();
                var rm = FindDown<Button>(V(toolkit), b => (b.Content as string) == "Remove from WoW...");
                Check("remove: the details have 'Remove from WoW...' (visible, focusable, a tooltip)", rm != null && rm.IsVisible && rm.Focusable && rm.ToolTip is string);
                rm?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => asked != null, 2000);
                Check("remove: removing Elan's Toolkit while the Hunter Helper is installed asks first, with a clear warning naming the Hunter Helper",
                    asked != null && asked.Kind == "danger" && asked.Title == "Remove Elan's Toolkit?" && asked.Text.Contains("Elan's Hunter Helper needs Elan's Toolkit") && asked.Text.Contains("won't load in game")
                    && asked.Confirm == "Remove anyway", asked?.Text);
                Check("remove: 'Keep it' keeps it", Directory.Exists(Path.Combine(addOns, "ElansToolkit")) && toolkit.State == CardState.UpToDate);
                toolkit.Expanded = false;
                Put("ElansPaladinHelper", "1.0.0");
                var paladin = cards.First(c => c.Info.Id == "ElansPaladinHelper");
                paladin.Update(paladin.Info, root);
                asked = null; Dialog.TestHook = c => { asked = c; return true; };
                var gone = await RemoveOurAddon(paladin);
                Check("remove: an addon nothing needs is still asked (no warning), and then goes - kept as a backup for Roll back",
                    gone && asked != null && asked.Kind == "danger" && !asked.Text.Contains("won't load") && asked.Confirm == "Remove" && !Directory.Exists(Path.Combine(addOns, "ElansPaladinHelper"))
                    && paladin.State == CardState.NotInstalled && paladin.RollbackVisibility == Visibility.Visible, asked?.Text);
                asked = null;
                await RemoveOurAddon(toolkit);
                Check("remove anyway: Toolkit goes, and the installed Hunter Helper shows 'Needs Elan's Toolkit' with the inline install again (its pill stays Up to date)",
                    asked?.Confirm == "Remove anyway" && !Directory.Exists(Path.Combine(addOns, "ElansToolkit")) && hunter.State == CardState.UpToDate && !hunter.Blocked
                    && hunter.NeedsVisibility == Visibility.Visible && hunter.NeedsInstallVisibility == Visibility.Visible, hunter.State + " " + hunter.NeedsText);

                // ---- a bulk install (the welcome guide) puts the needs first
                Util.DeleteDir(Path.Combine(addOns, "ElansHunterHelper"));
                foreach (var c in cards) c.Update(c.Info, root);
                fake.Clear();
                await GuideInstall(new List<string> { "ElansHunterHelper" });
                Check("bulk install: picking only the Hunter Helper installs Elan's Toolkit first, then the Hunter Helper", fake.SequenceEqual(new[] { "ElansToolkit", "ElansHunterHelper" })
                    && hunter.State == CardState.UpToDate && toolkit.State == CardState.UpToDate, string.Join(",", fake));
                Check("an install never runs over a missing need (the belt behind the faded pill)", await BlockedInstallRefused(root, addOns, fake));
            }
            catch (Exception e) { Check("addons needs test ran to the end", false, e.ToString()); }
            finally
            {
                InstallerForTest = null; Dialog.TestHook = savedHook;
                manifest = savedMan; settings.WowRoot = savedRoot; settings.SkipVersions = savedSkip;
                SettingsStore.Save(settings);
                foreach (var id in AddonOrder.Known) foreach (var b in Installer.Backups(id)) try { Util.DeleteDir(b.Path); } catch { }
                foreach (var c in cards) c.Expanded = false;
                RebuildCards();
                try { Util.DeleteDir(root); } catch { }
            }
        }

        async Task<bool> BlockedInstallRefused(string root, string addOns, List<string> fake)
        {
            Util.DeleteDir(Path.Combine(addOns, "ElansToolkit"));
            Util.DeleteDir(Path.Combine(addOns, "ElansHunterHelper"));
            var hunter = cards.First(c => c.Info.Id == "ElansHunterHelper");
            hunter.Update(hunter.Info, root);
            fake.Clear();
            await Install(hunter);
            return hunter.Blocked && fake.Count == 0 && hunter.Message != null && hunter.Message.Contains("Elan's Toolkit first");
        }
    }
}
