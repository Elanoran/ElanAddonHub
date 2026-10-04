using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // The "Other addons" section: every other addon found in Interface\AddOns, with keyless updates.
    public partial class MainWindow
    {
        readonly ObservableCollection<ThirdPartyCard> others = new ObservableCollection<ThirdPartyCard>();
        ICollectionView othersView;
        string addOnsDir;
        int clientInterface;
        bool scanning, checkingOthers;

        void InitOthers()
        {
            OtherCards.ItemsSource = others;
            othersView = CollectionViewSource.GetDefaultView(others);
            othersView.Filter = o =>
            {
                var q = SearchBox.Text?.Trim().ToLowerInvariant();
                return string.IsNullOrEmpty(q) || ((ThirdPartyCard)o).SearchText.Contains(q);
            };
        }

        string ClientFlavor()
        {
            var f = manifest?.Addons?.Select(a => a.Flavor).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            if (f != null && WowLocator.HasFlavor(settings.WowRoot, f)) return f;
            foreach (var c in new[] { "_classic_beta_", "_classic_era_", "_classic_", "_retail_" })
                if (WowLocator.HasFlavor(settings.WowRoot, c)) return c;
            return null;
        }

        IEnumerable<string> OwnFolders() =>
            (manifest?.Addons?.SelectMany(a => a.Folders ?? new List<string>()) ?? Enumerable.Empty<string>())
            .Concat(new[] { "ElansHunterHelper", "ElansHub" }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // scan Interface\AddOns and update the cards (cheap: local files only)
        async Task RefreshOthers()
        {
            if (scanning) return;
            scanning = true;
            try
            {
                var flavor = settings.WowRoot == null ? null : ClientFlavor();
                if (flavor == null) { addOnsDir = null; others.Clear(); UpdateOthersHeader(); return; }
                addOnsDir = WowLocator.AddOnsDir(settings.WowRoot, flavor);
                var dir = addOnsDir; var own = OwnFolders().ToList();
                int iface = 0;
                var entries = await Task.Run(() =>
                {
                    var firstGuess = AddonScanner.ClientInterface(dir, own, new List<TocInfo>());
                    return AddonScanner.Scan(dir, firstGuess, own, out iface);
                });
                clientInterface = iface;
                var keys = entries.Select(e => e.Key).ToList();
                foreach (var gone in others.Where(c => !keys.Contains(c.Entry.Key)).ToList()) others.Remove(gone);
                foreach (var e in entries)
                {
                    var card = others.FirstOrDefault(c => c.Entry.Key == e.Key);
                    if (card == null)
                    {
                        // keep alphabetical order
                        var at = others.TakeWhile(c => string.Compare(c.Entry.Title, e.Title, StringComparison.OrdinalIgnoreCase) < 0).Count();
                        others.Insert(at, new ThirdPartyCard(e));
                    }
                    else if (!card.IsBusy) card.SetEntry(e);
                }
            }
            catch (Exception e) { Util.Log("addon scan failed: " + e); }
            finally { scanning = false; UpdateOthersHeader(); }
        }

        // ask GitHub / WoWInterface (cached, keyless) about every linked addon
        async Task CheckOthers()
        {
            if (checkingOthers) return;
            checkingOthers = true;
            try
            {
                foreach (var c in others.ToList())
                {
                    if (c.Entry.IsDev || c.IsBusy) continue;
                    var r = await AddonSources.Lookup(c.Entry, c.Link, clientInterface);
                    if (!c.IsBusy) c.SetRemote(r, clientInterface);
                    UpdateOthersHeader();
                }
            }
            catch (Exception e) { Util.Log("addon check failed: " + e.Message); }
            finally { checkingOthers = false; UpdateOthersHeader(); }
        }

        // background: match unlinked addons against the WoWInterface filelist (offline / failure = no suggestions)
        bool suggesting;
        async Task SuggestOthers()
        {
            if (suggesting) return;
            suggesting = true;
            try
            {
                var open = others.Where(c => c.State == TpState.Local && !c.Entry.IsDev && string.IsNullOrEmpty(c.Link.Github) && string.IsNullOrEmpty(c.Link.Wowi)).ToList();
                if (open.Count == 0) return;
                var ix = await AddonSuggest.LoadIndex();
                if (ix == null) return;
                int iface = clientInterface;
                var results = await Task.Run(() => open.Select(c => AddonSuggest.Suggest(c.Entry, ix, iface, id => AddonSuggest.IsRejected(c.Entry.Key, id))).ToList());
                for (int i = 0; i < open.Count; i++) { open[i].Suggested = results[i]; open[i].Refresh(); }
            }
            catch (Exception e) { Util.Log("suggest failed: " + e.Message); }
            finally { suggesting = false; UpdateOthersHeader(); }
        }

        async Task AcceptSuggestion(ThirdPartyCard c)
        {
            var s = c.Suggested;
            if (s == null) return;
            c.Link.Wowi = s.File.Id; c.Link.Github = null; c.Link.Asset = null; c.Link.InstalledRemote = null;
            c.Suggested = null;
            AddonSources.SaveLinks();
            c.Message = "Checking...";
            var r = await AddonSources.Lookup(c.Entry, c.Link, clientInterface);
            c.SetRemote(r, clientInterface);
            c.Message = r == null ? "Linked, but couldn't reach WoWInterface right now." : null;
            UpdateOthersHeader();
        }

        async void UseSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ThirdPartyCard c) await AcceptSuggestion(c);
        }

        void RejectSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is ThirdPartyCard c) || c.Suggested == null) return;
            AddonSuggest.Reject(c.Entry.Key, c.Suggested.File.Id);
            c.Suggested = null;
            c.Refresh();
            _ = SuggestOthers();   // maybe the next best candidate
        }

        async void LinkExact_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in others.Where(x => x.ShowSuggestion && x.Suggested.Exact && x.Suggested.FlavorFits).ToList()) await AcceptSuggestion(c);
        }

        void UpdateOthersHeader()
        {
            LinkExactButton.Visibility = others.Any(x => x.ShowSuggestion && x.Suggested.Exact && x.Suggested.FlavorFits) ? Visibility.Visible : Visibility.Collapsed;
            OtherPanel.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var updates = others.Count(c => c.State == TpState.UpdateAvailable);
            OtherTitle.Text = $"OTHER ADDONS  ·  {others.Count}" + (updates > 0 ? $"  ·  {updates} update{(updates == 1 ? "" : "s")}" : "");
            UpdateAllButton.Visibility = updates > 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchBox.Visibility = others.Count > 8 ? Visibility.Visible : Visibility.Collapsed;
            var linked = others.Count(c => c.State != TpState.Local && c.State != TpState.DevCopy);
            OtherHint.Text = $"Found in {addOnsDir}. Updates come from GitHub releases and WoWInterface (no accounts or keys). "
                + (linked == 0 ? "Open an addon and paste a GitHub or WoWInterface link to keep it updated." : "");
        }

        void Search_Changed(object sender, TextChangedEventArgs e) => othersView?.Refresh();

        void OtherCard_Toggle(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ThirdPartyCard c) c.Expanded = !c.Expanded;
        }

        void OpenAddonFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is ThirdPartyCard c) || addOnsDir == null) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + Path.Combine(addOnsDir, c.Entry.Key) + "\""); } catch { }
        }

        async void OtherAction_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ThirdPartyCard c) await InstallOther(c);
        }

        async void UpdateAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in others.Where(x => x.State == TpState.UpdateAvailable).ToList()) await InstallOther(c);
        }

        async void Choice_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is ThirdPartyCard c) || !(((ComboBox)sender).SelectedItem is string name)) return;
            c.Link.Asset = name;
            AddonSources.SaveLinks();
            var r = await AddonSources.Lookup(c.Entry, c.Link, clientInterface);
            c.SetRemote(r, clientInterface);
            UpdateOthersHeader();
        }

        async void LinkSource_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is ThirdPartyCard c)) return;
            var text = c.LinkInput;
            var gh = AddonScanner.ParseGithub(text);
            var wid = AddonScanner.ParseWowiId(text);
            if (gh == null && wid == null)
            {
                c.Message = "That isn't a GitHub repository or WoWInterface addon link. (CurseForge and Wago can't be updated from here - they need accounts.)";
                return;
            }
            if (gh != null) { c.Link.Github = gh; c.Link.Wowi = null; } else { c.Link.Wowi = wid; c.Link.Github = null; }
            c.Link.Asset = null; c.Link.InstalledRemote = null;
            AddonSources.SaveLinks();
            c.Message = "Checking...";
            var r = await AddonSources.Lookup(c.Entry, c.Link, clientInterface);
            c.SetRemote(r, clientInterface);
            c.Message = r == null ? "Linked, but couldn't find releases there right now (offline, rate limited, or no release)." : null;
            UpdateOthersHeader();
        }

        void Unlink_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is ThirdPartyCard c)) return;
            c.Link.Github = null; c.Link.Wowi = null; c.Link.Asset = null; c.Link.InstalledRemote = null;
            AddonSources.SaveLinks();
            c.SetRemote(null, clientInterface);
            c.Message = null;
            UpdateOthersHeader();
        }

        async void Rollback_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is ThirdPartyCard c) || addOnsDir == null) return;
            var ok = MessageBox.Show(this, $"Put the previous version of {c.Name} back?\n\nYour settings (WTF folder) are not touched.",
                "Roll back", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (ok != MessageBoxResult.Yes) return;
            try
            {
                await Task.Run(() => AddonSources.Rollback(addOnsDir, c.Entry, c.Link));
                c.Message = "Rolled back. In game, type /reload.";
            }
            catch (Exception ex) { c.Message = "Roll back failed: " + ex.Message; }
            c.Remote = null;
            await RefreshOthers();
            c.Refresh();
            _ = CheckOthers();
        }

        async Task InstallOther(ThirdPartyCard c)
        {
            if (addOnsDir == null || c.Remote == null || c.IsBusy) return;
            var key = c.Entry.Key;
            var running = GamePresence.WowRunningNow();
            c.Message = null;
            c.SetBusy(true, "Updating...");
            string msg;
            try
            {
                var res = await AddonSources.Install(addOnsDir, c.Entry, c.Link, c.Remote, clientInterface, new Progress<double>(p => c.Progress = p));
                msg = "Updated to " + c.Remote.Version + ". In game, type /reload" + (running ? " (WoW is running; if the addon looks unchanged, restart it)." : ".");
                if (res.Warning != null) msg += "\n" + res.Warning;
                c.Remote = null;
            }
            catch (Exception ex)
            {
                Util.Log($"update {key} failed: {ex}");
                msg = "Something went wrong: " + ex.Message;
            }
            c.SetBusy(false);
            await RefreshOthers();
            var card = others.FirstOrDefault(x => x.Entry.Key == key);
            if (card != null)
            {
                card.Message = msg;
                var r = await AddonSources.Lookup(card.Entry, card.Link, clientInterface);
                card.SetRemote(r, clientInterface);
            }
            UpdateOthersHeader();
        }

        // ---- self-test: fake WoW folder, synthetic zip install + rollback, one real GitHub / WoWInterface lookup
        async Task<string> ThirdPartyTest(string dir)
        {
            var sb = new StringBuilder();
            await RefreshOthers();
            await CheckOthers();
            sb.AppendLine(AddonSuggest.SelfTest());
            await SuggestOthers();
            var realIx = await AddonSuggest.LoadIndex();
            if (realIx == null) sb.AppendLine("real filelist: unavailable");
            else
            {
                sb.AppendLine("real filelist: " + realIx.ByDir.Count + " folders indexed");
                foreach (var probe in new[] { "Details", "Bartender4", "Questie", "WeakAuras", "Atlas", "ElvUI", "pfUI", "Plater" })
                {
                    var ent = new AddonEntry { Key = probe, Title = probe, Folders = new List<string> { probe } };
                    var sg = AddonSuggest.Suggest(ent, realIx, 16001);
                    sb.AppendLine($"  probe {probe}: " + (sg == null ? "none" : $"{sg.Text} id={sg.File.Id} score={sg.Score:0} [{sg.Hint}]"));
                }
            }
            foreach (var c in others) if (c.Suggested != null) sb.AppendLine($"  card {c.Entry.Key}: {c.SuggestText} [{c.Suggested.Hint}]");
            sb.AppendLine($"addons dir={addOnsDir} interface={clientInterface} cards={others.Count}");
            foreach (var c in others)
                sb.AppendLine($"  [{c.Entry.Key}] title='{c.Entry.Title}' ver={c.Entry.Version} author={c.Entry.Author} folders={string.Join("+", c.Entry.Folders)} dev={c.Entry.IsDev} src={c.Source} state={c.State} remote={c.Remote?.Version} asset={c.Remote?.Asset} choices={c.Remote?.Choices.Count}");

            // synthetic update of "Plain": backup, swap, WTF untouched, roll back
            var plain = others.FirstOrDefault(c => c.Entry.Key == "Plain");
            if (plain != null && addOnsDir != null)
            {
                var wowRoot = Path.GetFullPath(Path.Combine(addOnsDir, "..", ".."));
                var marker = Path.Combine(wowRoot, "WTF", "marker.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(marker)); File.WriteAllText(marker, "keep");
                var zip = Path.Combine(dir, "Plain-2.0.zip");
                if (File.Exists(zip)) File.Delete(zip);
                using (var za = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                {
                    void Add(string name, string text) { var en = za.CreateEntry(name); using (var w = new StreamWriter(en.Open())) w.Write(text); }
                    Add("Plain/Plain.toc", "## Interface: 16001\n## Title: Plain Addon\n## Version: 2.0\n## Author: Me\nPlain.lua\n");
                    Add("Plain/Plain.lua", "-- v2\n");
                    Add("Foo/Foo.toc", "## Interface: 16001\n## Title: x\n");
                }
                // Foo belongs to a different addon: must be refused
                string err1 = null;
                try { AddonSources.InstallZip(addOnsDir, plain.Entry, plain.Link, "2.0", clientInterface, zip, null); } catch (Exception ex) { err1 = ex.Message; }
                sb.AppendLine("zip touching another addon: " + (err1 ?? "NOT REFUSED"));
                File.Delete(zip);
                using (var za = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                {
                    void Add(string name, string text) { var en = za.CreateEntry(name); using (var w = new StreamWriter(en.Open())) w.Write(text); }
                    Add("Plain/Plain.toc", "## Interface: 11507\n## Title: Plain Addon\n## Version: 2.0\n## Author: Me\nPlain.lua\n");
                    Add("Plain/Plain.lua", "-- v2\n");
                }
                var res = AddonSources.InstallZip(addOnsDir, plain.Entry, plain.Link, "2.0", clientInterface, zip, null);
                await RefreshOthers();
                var after = others.First(c => c.Entry.Key == "Plain");
                sb.AppendLine($"install: version now {after.Entry.Version}, backups={AddonSources.Backups("Plain").Count}, warning={res.Warning}, wtf marker kept={File.Exists(marker)}, leftovers={Directory.GetDirectories(addOnsDir, ".elanshub-*").Length}");
                AddonSources.Rollback(addOnsDir, after.Entry, after.Link);
                await RefreshOthers();
                sb.AppendLine($"rollback: version now {others.First(c => c.Entry.Key == "Plain").Entry.Version}, backups={AddonSources.Backups("Plain").Count}");
                // dev copy
                var dev = others.FirstOrDefault(c => c.Entry.IsDev);
                string err3 = null;
                File.Delete(zip);
                using (var za = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                {
                    var en = za.CreateEntry("DevAddon/DevAddon.toc"); using (var w = new StreamWriter(en.Open())) w.Write("## Interface: 16001\n");
                }
                if (dev != null) try { AddonSources.InstallZip(addOnsDir, dev.Entry, dev.Link, "9", clientInterface, zip, null); } catch (Exception ex) { err3 = ex.Message; }
                sb.AppendLine("dev copy: " + (err3 ?? "NOT REFUSED"));
                // zip slip
                File.Delete(zip);
                using (var za = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                {
                    var en = za.CreateEntry("Plain/../../evil.toc"); using (var w = new StreamWriter(en.Open())) w.Write("x");
                    var en2 = za.CreateEntry("Plain/Plain.toc"); using (var w = new StreamWriter(en2.Open())) w.Write("## Interface: 16001\n");
                }
                string err2 = null;
                try { AddonSources.InstallZip(addOnsDir, plain.Entry, plain.Link, "9", clientInterface, zip, null); } catch (Exception ex) { err2 = ex.Message; }
                sb.AppendLine("zip slip: " + (err2 ?? "NOT REFUSED"));
            }
            // pictures: expand a few cards, scroll to the section
            foreach (var c in others.Where(c => c.State == TpState.UpdateAvailable || c.Entry.Folders.Count > 1).Take(3)) c.Expanded = true;
            ShowTab("addons");
            await Task.Delay(300);
            AddonsPage.ScrollToEnd();
            await Task.Delay(500);
            Snapshot(Path.Combine(dir, "9-addons.png"));
            AddonsPage.ScrollToVerticalOffset(AddonsPage.VerticalOffset - 300);
            await Task.Delay(300);
            AddonsPage.ScrollToVerticalOffset(AddonsPage.VerticalOffset - 470);
            await Task.Delay(400);
            Snapshot(Path.Combine(dir, "10-suggest.png"));
            return sb.ToString();
        }
    }
}
