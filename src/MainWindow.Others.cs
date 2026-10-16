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
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
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
            CurseForgeDirect.IsOn = () => !settings.CfDirectOff;
            CurseForgeDirect.LetCfOn = () => !settings.CfLetInstallOff;
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
                if (flavor == null) { addOnsDir = null; others.Clear(); RememberLayout(null, 0); UpdateOthersHeader(); return; }
                addOnsDir = WowLocator.AddOnsDir(settings.WowRoot, flavor);
                var dir = addOnsDir; var own = OwnFolders().ToList();
                int iface = 0;
                CfInstance cf = null;
                var entries = await Task.Run(() =>
                {
                    var firstGuess = AddonScanner.ClientInterface(dir, own, new List<TocInfo>());
                    var list = AddonScanner.Scan(dir, firstGuess, own, out iface, AddonSuggest.CachedIndex());
                    cf = CurseForgeLocal.Load(dir);                 // CurseForge-managed addons are regrouped by CurseForge's own folder lists
                    return CurseForgeLocal.Apply(list, cf);
                });
                clientInterface = iface;
                cfInst = cf;
                WatchCfFile();
                var keys = entries.Select(e => e.Key).ToList();
                foreach (var gone in others.Where(c => !keys.Contains(c.Entry.Key)).ToList()) others.Remove(gone);
                foreach (var e in entries)
                {
                    var card = others.FirstOrDefault(c => c.Entry.Key == e.Key);
                    var checkedAt = cf?.LastRefresh ?? DateTime.MinValue;
                    if (card == null)
                    {
                        // keep alphabetical order
                        var at = others.TakeWhile(c => string.Compare(c.Entry.Title, e.Title, StringComparison.OrdinalIgnoreCase) < 0).Count();
                        var nc = new ThirdPartyCard(e) { CfChecked = checkedAt };
                        nc.Refresh();
                        others.Insert(at, nc);
                        nc.LoadLogo(dir);
                    }
                    else if (!card.IsBusy)
                    {
                        card.CfChecked = checkedAt; card.SetEntry(e); card.LoadLogo(dir);
                        // "CurseForge installs it on its next check" and it has: the note becomes a done note (which clears by itself)
                        if (card.Fallback == OpenInCfButton && e.Cf != null && !e.Cf.UpdateAvailable) DoneNote(card, InstalledByCfText(e.Cf, WowWatch.Shared.Running), WowWatch.Shared.Running);
                    }
                }
                RememberLayout(null, others.Count);
            }
            catch (Exception e) { Util.Log("addon scan failed: " + e); }
            finally { scanning = false; othersLoaded = true; UpdateOthersHeader(); }
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
                    if (c.Entry.IsDev || c.IsBusy || c.Entry.Cf != null) continue;   // CurseForge-managed: never GitHub / WoWInterface
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
                var open = others.Where(c => c.State == TpState.Local && c.Source != "Self" && !c.Entry.IsDev && string.IsNullOrEmpty(c.Link.Github) && string.IsNullOrEmpty(c.Link.Wowi)).ToList();
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
            UpdateCardsSkeleton();
            // until the first scan has finished the section is there with the remembered count and skeleton rows; then the real count
            int shown = others.Count > 0 || othersLoaded || !othersExpected ? others.Count : OtherSlots;
            LinkExactButton.Visibility = others.Any(x => x.ShowSuggestion && x.Suggested.Exact && x.Suggested.FlavorFits) ? Visibility.Visible : Visibility.Collapsed;
            OtherPanel.Visibility = others.Count > 0 || OthersSkeletonWanted ? Visibility.Visible : Visibility.Collapsed;
            var updates = others.Count(c => c.State == TpState.UpdateAvailable);
            OtherTitle.Text = $"Other addons  ·  {shown}" + (updates > 0 ? $"  ·  {updates} update{(updates == 1 ? "" : "s")}" : "");
            RefreshRailBadges();
            UpdateAllButton.Visibility = updates > 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchBox.Visibility = shown > 8 ? Visibility.Visible : Visibility.Collapsed;
            var linked = others.Count(c => c.State != TpState.Local && c.State != TpState.DevCopy);
            var info = $"Found in {addOnsDir}.\nUpdates come from GitHub releases and WoWInterface (no accounts or keys)."
                + (linked == 0 ? "\nOpen an addon and paste a GitHub or WoWInterface link to keep it updated." : "");
            OtherInfo.ToolTip = Tip(info);
            AutomationProperties.SetName(OtherInfo, "About the other addons list");
            var cfOn = cfInst != null;
            var vis = cfOn ? Visibility.Visible : Visibility.Collapsed;
            CfChip.Visibility = vis; CfCheckButton.Visibility = vis; CfOpenButton.Visibility = vis;
            if (cfOn)
            {
                var n = others.Count(c => c.Entry.Cf != null);
                var stale = CurseForgeLocal.IsStale(cfInst.LastRefresh);
                CfChipText.Text = cfChecking ? "Checking CurseForge..." : cfFlash != null ? cfFlash : GitHubStats.ShortAgo(cfInst.LastRefresh);
                CfChipText.Foreground = (Brush)Application.Current.Resources[stale ? "Gold" : "TextDim"];
                CfChipGlyph.Foreground = CfChipText.Foreground;
                try { CfChipIcon.Source = SourceIcons.CurseForge(); } catch { }
                CfChipIcon.Visibility = CfChipIcon.Source != null ? Visibility.Visible : Visibility.Collapsed;
                var full = (cfStatus != null ? cfStatus + "\n" : "") + $"CurseForge manages {n} addon{(n == 1 ? "" : "s")} here; last checked {CurseForgeLocal.Ago(cfInst.LastRefresh)}"
                    + (stale ? " (old - press the refresh button to check now)" : "") + ".\n" + (settings.CfDirectOff ? "The Outpost asks CurseForge to install updates." : "The Outpost installs their updates from CurseForge's download server (checked, with a backup), or asks CurseForge when the author wants that.");
                CfChip.ToolTip = Tip(full);
                AutomationProperties.SetName(CfChip, "CurseForge status");
                CfChipText.ToolTip = null;
            }
        }

        static ToolTip Tip(string text) => new ToolTip { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 } };

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
            if (!((sender as FrameworkElement)?.DataContext is ThirdPartyCard c)) return;
            if (!c.PillClickable || c.IsBusy) c.Expanded = !c.Expanded;                       // a quiet pill just opens the details
            else if (c.State == TpState.ChooseFile && c.Link.Asset == null) c.Expanded = true; // "Choose file": the picker is in the details
            else await InstallOther(c);
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
            if (AddonScanner.IsForever(clientInterface) && AddonSources.IsClassicAsset(name)) c.Message = "Made for Classic, may not work on WoW Forever.";
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
            if (!Dialog.Confirm(this, "Roll back?", $"Put the previous version of {c.Name} back?\n\nYour settings (WTF folder) are not touched.", "Roll back")) return;
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

        // ---- CurseForge: read its local state, ask it (never the hub) to install, let it refresh its update info
        CfInstance cfInst;
        string cfStatus;
        FileSystemWatcher cfWatcher;
        string cfWatched;
        readonly System.Windows.Threading.DispatcherTimer cfDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        bool? wowWas;
        DateTime cfLastAuto = DateTime.MinValue;

        void WatchCfFile()
        {
            try
            {
                var file = CurseForgeLocal.DataFile;
                if (file == cfWatched || !Directory.Exists(Path.GetDirectoryName(file))) return;
                cfWatched = file;
                cfWatcher?.Dispose();
                cfWatcher = new FileSystemWatcher(Path.GetDirectoryName(file), Path.GetFileName(file)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
                FileSystemEventHandler h = (s, e) => Dispatcher.BeginInvoke(new Action(() => { cfDebounce.Stop(); cfDebounce.Start(); }));
                cfWatcher.Changed += h; cfWatcher.Created += h; cfWatcher.Renamed += (s, e) => h(s, null);
                cfDebounce.Tick -= CfDebounce_Tick; cfDebounce.Tick += CfDebounce_Tick;
            }
            catch (Exception e) { Util.Log("curseforge watcher failed: " + e.Message); }
        }

        async void CfDebounce_Tick(object sender, EventArgs e)
        {
            cfDebounce.Stop();
            await RefreshOthers();
        }

        // Update click on a CurseForge card. Three ways (CurseForgeDirect.RouteFor): the Outpost installs it (Direct); CurseForge installs it
        // by itself because its auto-update is on for the addon (CurseForgeAuto: no install link = no "Select game instance" window); or
        // CurseForge's install link (DeepLink: its window). forceLink = the "Open in CurseForge" fallback button.
        async Task InstallCf(ThirdPartyCard c, bool forceLink = false)
        {
            var cf = c.Entry.Cf;
            if (cf == null || addOnsDir == null || c.IsBusy) return;
            var key = c.Entry.Key; var dir = addOnsDir;
            c.Message = null;
            var plan = CurseForgeDirect.Decide(cf, cfInst, CurseForgeDirect.IsOn());
            var route = forceLink ? CurseForgeDirect.Route.DeepLink : CurseForgeDirect.RouteFor(cf, cfInst, CurseForgeDirect.IsOn(), CurseForgeDirect.LetCfOn());
            if (route == CurseForgeDirect.Route.Direct) { await InstallCfDirect(c, cf, key, dir); return; }
            if (route == CurseForgeDirect.Route.CurseForgeAuto) { await InstallCfAuto(c, cf, key, dir); return; }
            c.SetBusy(true, "CurseForge...");
            string msg; bool pendingRetry = false, done = false;
            var wow = GamePresence.WowRunningNow();
            try
            {
                var res = await CurseForgeLocal.RequestInstall(cf, cf.LatestId, dir, t => Dispatcher.BeginInvoke(new Action(() => c.SetBusyText(t))));
                if (res == CurseForgeLocal.CfInstallResult.Installed)
                {
                    wow = GamePresence.WowRunningNow();
                    msg = InstalledByCfText(cf, wow); done = true;
                }
                else if (res == CurseForgeLocal.CfInstallResult.Started)
                    msg = "CurseForge is waiting for you: choose Forever as the install location and click Install in its window. This card updates by itself when it finishes.";
                else
                    msg = "Something went wrong: CurseForge didn't start the install. Open CurseForge and update it there, or click Retry.";
                pendingRetry = res == CurseForgeLocal.CfInstallResult.NotStarted;
                if (plan.Reason != null && !done) msg = plan.Reason + " " + msg;
            }
            catch (Exception ex) { Util.Log($"curseforge update {key} failed: {ex}"); msg = "Something went wrong: " + ex.Message; }
            c.SetBusy(false);
            await RefreshOthers();
            var card = others.FirstOrDefault(x => x.Entry.Key == key);
            if (card != null)
            {
                if (pendingRetry) card.RetryTip = "CurseForge didn't start the install - open CurseForge and update it there, or click to try again";
                if (done) DoneNote(card, msg, wow);
                else card.Message = msg;
                // once per addon: how to skip CurseForge's window next time (only when its auto-update being off is the reason)
                if (!pendingRetry && CurseForgeDirect.AutoOffHelps(cf, cfInst, CurseForgeDirect.IsOn(), CurseForgeDirect.LetCfOn())) card.OfferHint(cf.Id, CurseForgeDirect.AutoOffHint);
            }
            UpdateOthersHeader();
        }

        static string InstalledByCfText(CfAddon cf, bool wow) =>
            "CurseForge installed " + cf.LatestFileName + ". In game, type /reload." + (wow ? " (WoW is running; if the addon looks unchanged, restart it.)" : "");

        public const string OpenInCfButton = "Open in CurseForge";

        // CurseForge's auto-update is on for this addon: let CurseForge install it by itself (CurseForgeLocal.LetInstall) - no install window
        async Task InstallCfAuto(ThirdPartyCard c, CfAddon cf, string key, string dir)
        {
            c.SetBusy(true, "CurseForge...");
            CurseForgeLocal.CfAutoResult res;
            try { res = await CurseForgeLocal.LetInstall(cf, dir, t => Dispatcher.BeginInvoke(new Action(() => c.SetBusyText(t)))); }
            catch (Exception ex) { Util.Log($"curseforge auto update {key} failed: {ex}"); res = CurseForgeLocal.CfAutoResult.NotStarted; }
            var wow = GamePresence.WowRunningNow();
            c.SetBusy(false);
            await RefreshOthers();
            var card = others.FirstOrDefault(x => x.Entry.Key == key);
            if (card != null) ShowCfAutoResult(card, cf, res, wow);
            UpdateOthersHeader();
        }

        // the card's note for each LetInstall outcome (the selftest checks these texts)
        void ShowCfAutoResult(ThirdPartyCard card, CfAddon cf, CurseForgeLocal.CfAutoResult res, bool wow)
        {
            switch (res)
            {
                case CurseForgeLocal.CfAutoResult.Installed: DoneNote(card, InstalledByCfText(cf, wow), wow); break;
                case CurseForgeLocal.CfAutoResult.LaterWhileRunning: card.SetActionNote(CurseForgeLocal.LaterText(), OpenInCfButton); break;
                case CurseForgeLocal.CfAutoResult.TimedOut: card.SetActionNote(CurseForgeLocal.TimedOutText, OpenInCfButton); break;
                default: card.SetActionNote("CurseForge couldn't be started to install it. Click Open in CurseForge to install it there.", OpenInCfButton); break;
            }
        }

        async void CfFallback_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ThirdPartyCard c) await InstallCf(c, forceLink: true);
        }

        // The direct path (CurseForgeDirect): CurseForge's own latest file from forgecdn, checked, backed up, swapped - no CurseForge window.
        async Task InstallCfDirect(ThirdPartyCard c, CfAddon cf, string key, string dir)
        {
            var running = GamePresence.WowRunningNow();
            c.SetBusy(true, "Updating...");
            string msg;
            var res = await CurseForgeDirect.Update(dir, key, cf, new Progress<double>(p => c.Progress = p));
            if (res.Ok) msg = "Updated to " + cf.LatestFileName + ". In game, type /reload" + (running ? " (WoW is running; if the addon looks unchanged, restart it)." : ".") + " CurseForge picks the new files up on its next scan.";
            else msg = "Something went wrong: " + res.Error;
            c.SetBusy(false);
            await RefreshOthers();
            var card = others.FirstOrDefault(x => x.Entry.Key == key);
            if (card != null) { if (res.Ok) DoneNote(card, msg, running); else card.Message = msg; }
            UpdateOthersHeader();
        }

        // ---- post-install notes clear by themselves (ThirdPartyCard.NoteExpired): a light 3 s sweep runs only while such a note is shown
        System.Windows.Threading.DispatcherTimer noteSweep;

        void DoneNote(ThirdPartyCard card, string msg, bool wowRunning)
        {
            card.SetDoneNote(msg, wowRunning);
            if (noteSweep == null)
            {
                noteSweep = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                noteSweep.Tick += (s, e) => SweepNotes(others, DateTime.UtcNow, WowWatch.Shared.Running, NewestSavedVarsUtc());
            }
            noteSweep.Start();
        }

        // returns how many notes it cleared; stops the sweep when none is left
        int SweepNotes(IEnumerable<ThirdPartyCard> cards, DateTime nowUtc, bool wowNow, DateTime newestSavedVarsUtc)
        {
            int n = 0;
            foreach (var c in cards.Where(x => x.NoteDoneAt > DateTime.MinValue).ToList())
                if (c.NoteExpired(nowUtc, wowNow, newestSavedVarsUtc)) { c.ClearNote(); n++; }
            if (!others.Any(x => x.NoteDoneAt > DateTime.MinValue)) noteSweep?.Stop();
            return n;
        }

        // newest write of a SavedVariables file of this WoW client (the same folders every SavedVariables reader uses: SavedVarsFiles.Dirs);
        // the game writes them on /reload and logout, so a newer one = the new addon files are loaded
        DateTime NewestSavedVarsUtc()
        {
            var newest = DateTime.MinValue;
            try
            {
                if (addOnsDir == null || string.IsNullOrEmpty(settings.WowRoot)) return newest;
                var flavorDir = Path.GetFullPath(Path.Combine(addOnsDir, "..", "..")).TrimEnd('\\') + "\\";
                foreach (var d in SavedVarsFiles.Dirs(settings.WowRoot))
                {
                    if (!Path.GetFullPath(d).StartsWith(flavorDir, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var f in new DirectoryInfo(d).EnumerateFiles("*.lua")) if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
                }
            }
            catch (Exception e) { Util.Log("saved variables stamp: " + e.GetType().Name); }
            return newest;
        }

        // Settings > Addons: is the CurseForge integration healthy? Local files only.
        public CurseForgeLocal.CfStatus CfProbe()
        {
            var f = string.IsNullOrEmpty(settings.WowRoot) ? null : ClientFlavor();
            return CurseForgeLocal.Probe(f == null ? null : WowLocator.AddOnsDir(settings.WowRoot, f));
        }
        public async Task RecheckCurseForge() { await RefreshOthers(); }

        bool cfChecking;
        string cfFlash;                       // "Checked just now" shown on the chip for a few seconds after a check
        System.Windows.Threading.DispatcherTimer cfFlashFade;

        async Task RunCfCheck()
        {
            if (addOnsDir == null || CurseForgeLocal.Busy) return;
            CfCheckButton.IsEnabled = false;
            cfChecking = true; cfFlash = null;
            var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
            CfSpinRot.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
            cfStatus = "Checking CurseForge... (started minimized if it wasn't running)";
            UpdateOthersHeader();
            try { cfStatus = await CurseForgeLocal.CheckNow(addOnsDir); }
            catch (Exception e) { Util.Log("curseforge check failed: " + e.Message); cfStatus = "Couldn't check with CurseForge."; }
            cfChecking = false;
            CfSpinRot.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            CfCheckButton.IsEnabled = true;
            cfFlash = cfStatus != null && cfStatus.Contains("checked just now") ? "Checked just now" : cfStatus;
            if (cfFlashFade != null) cfFlashFade.Stop();
            cfFlashFade = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            cfFlashFade.Tick += (s, ev) => { cfFlashFade.Stop(); cfFlash = null; UpdateOthersHeader(); };
            cfFlashFade.Start();
            await RefreshOthers();
        }

        async void CheckCf_Click(object sender, RoutedEventArgs e) => await RunCfCheck();

        void OpenCf_Click(object sender, RoutedEventArgs e)
        {
            if (!CurseForgeLocal.OpenApp()) cfStatus = "Couldn't open CurseForge.";
            UpdateOthersHeader();
        }

        // optional (off by default): once a day, or when WoW starts, let CurseForge refresh - only when it isn't running
        void MaybeCfAuto()
        {
            if (!settings.CfAutoCheck || cfInst == null) { wowWas = null; return; }
            var wow = WowWatch.Shared.Running;           // the shared 2 s watcher: no process list scan here
            var started = wowWas == false && wow;
            wowWas = wow;
            if (CurseForgeLocal.Busy || scanning || (DateTime.UtcNow - cfLastAuto).TotalHours < 2) return;
            var age = DateTime.UtcNow - cfInst.LastRefresh;
            if ((age.TotalHours > 24 || (started && age.TotalHours > 1)) && !CurseForgeLocal.IsRunning()) { cfLastAuto = DateTime.UtcNow; _ = RunCfCheck(); }
        }

        async Task InstallOther(ThirdPartyCard c)
        {
            if (c.Entry.Cf != null) { await InstallCf(c); return; }
            if (addOnsDir == null || c.Remote == null || c.IsBusy) return;
            var key = c.Entry.Key;
            var running = GamePresence.WowRunningNow();
            c.Message = null;
            c.SetBusy(true, "Updating...");
            string msg; bool done = false;
            try
            {
                var res = await AddonSources.Install(addOnsDir, c.Entry, c.Link, c.Remote, clientInterface, new Progress<double>(p => c.Progress = p));
                msg = "Updated to " + c.Remote.Version + ". In game, type /reload" + (running ? " (WoW is running; if the addon looks unchanged, restart it)." : ".");
                if (res.Warning != null) msg += "\n" + res.Warning;
                else done = true;                       // a warning stays until the next action; a plain "Updated" note clears by itself
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
                if (done) DoneNote(card, msg, running); else card.Message = msg;
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
            sb.AppendLine(AddonScanner.SelfTest());
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
            sb.AppendLine(CurseForgeLocal.SelfTest(addOnsDir));
            sb.AppendLine(await CurseForgeLocal.SelfTestInstall());
            {
                var m = others.FirstOrDefault(c => c.Entry.Cf != null && c.Entry.Cf.Name == "CfMulti");
                var u = others.FirstOrDefault(c => c.Entry.Cf != null && c.Entry.Cf.Name == "CfUpToDate");
                void Ck(string w, bool ok, string info = null) => sb.AppendLine($"curseforge card {w}: {(ok ? "ok" : "FAIL")}{(info == null ? "" : " (" + info + ")")}");
                Ck("grouped by CurseForge's folder list", m != null && string.Join("+", m.Entry.Folders) == "CfMulti+CfMulti_Opts", m == null ? "missing" : string.Join("+", m.Entry.Folders));
                Ck("update available when ids differ", m != null && m.State == TpState.UpdateAvailable && m.Source == "CurseForge" && m.VersionLine.StartsWith("Update available: CfMulti-2.0.zip"), m?.VersionLine);
                Ck("up to date when ids equal", u != null && u.State == TpState.UpToDate && u.VersionLine.StartsWith("Up to date (checked"), u?.VersionLine);
                Ck("not offered GitHub/WoWI link or suggestion", m != null && m.LinkVisibility == Visibility.Collapsed && !m.ShowSuggestion && m.Remote == null);
                Ck("unmanaged addon unaffected", others.Any(c => c.Entry.Key == "Plain" && c.Entry.Cf == null));
                Ck("CurseForge chip shown", CfChip.Visibility == Visibility.Visible && CfCheckButton.Visibility == Visibility.Visible, CfChipText.Text);
                // the direct update (CurseForgeDirect): the fixture's file has no allowModDistribution = the author's choice, the link is kept
                Ck("no distribution permission: card says so and keeps the CurseForge link", m != null && m.ButtonText == "Update via CurseForge" && m.VersionLine.Contains(CurseForgeDirect.AuthorOnlyText), m?.VersionLine);
                var off0 = settings.CfDirectOff;
                try
                {
                    var okCf = new CfAddon { Id = 4242, Name = "CfOk", InstalledId = 1, LatestId = 2, LatestFileName = "CfOk-2.zip", AllowDistribution = true, Folders = new List<string> { "CfOk" },
                        LatestUrl = "https://edge.forgecdn.net/files/1/2/CfOk-2.zip", LatestLength = 10, LatestModules = new List<string> { "CfOk" } };
                    okCf.LatestHashes.Add(new KeyValuePair<string, string>("sha1", "00"));
                    var okCard = new ThirdPartyCard(new AddonEntry { Key = "CfOk", Title = "CfOk", Folders = new List<string> { "CfOk" }, Cf = okCf });
                    settings.CfDirectOff = false; okCard.Refresh();
                    var onText = okCard.ButtonText;
                    settings.CfDirectOff = true; okCard.Refresh();
                    Ck("setting on = Update (direct), off = Update via CurseForge", onText == "Update" && okCard.ButtonText == "Update via CurseForge", onText + " / " + okCard.ButtonText);
                    okCf.MissingDeps.Add(99); settings.CfDirectOff = false; okCard.Refresh();
                    Ck("missing dependency = Update via CurseForge", okCard.ButtonText == "Update via CurseForge");
                }
                finally { settings.CfDirectOff = off0; }
            }
            sb.AppendLine(await CurseForgeLocal.SelfTestLetInstall());
            {
                // 2.72.4: the CurseForge-installs-it-by-itself texts on the card, and post-install notes that clear by themselves
                void Ck(string w, bool ok, string info = null) => sb.AppendLine($"curseforge notes {w}: {(ok ? "ok" : "FAIL")}{(info == null ? "" : " (" + info + ")")}");
                var off0 = settings.CfDirectOff; var let0 = settings.CfLetInstallOff; var sec0 = ThirdPartyCard.NoteClearSec;
                string svFile = null;
                try
                {
                    var autoCf = new CfAddon { Id = 5151, Name = "CfAuto", InstalledId = 1, LatestId = 2, LatestFileName = "CfAuto-2.zip", AllowDistribution = false, AutoInstallPref = true, Folders = new List<string> { "CfAuto" } };
                    ThirdPartyCard New() => new ThirdPartyCard(new AddonEntry { Key = "CfAuto", Title = "CfAuto", Folders = new List<string> { "CfAuto" }, Cf = autoCf });
                    var card = New();
                    settings.CfDirectOff = false; settings.CfLetInstallOff = false; card.Refresh();
                    Ck("auto-update on: the pill says CurseForge installs it by itself", card.PillTip.Contains("installs it by itself") && card.ButtonText == "Update via CurseForge", card.PillTip);
                    autoCf.AutoInstallPref = false; card.Refresh();
                    Ck("auto-update off: the pill says how to skip CurseForge's window", card.PillTip.Contains(CurseForgeDirect.AutoOffHint), card.PillTip);
                    settings.CfLetInstallOff = true; card.Refresh();
                    Ck("setting off: the install link as before, no hint", !card.PillTip.Contains(CurseForgeDirect.AutoOffHint) && !card.PillTip.Contains("by itself"), card.PillTip);
                    settings.CfLetInstallOff = false; autoCf.AutoInstallPref = true;

                    // the card's note for every LetInstall outcome
                    ShowCfAutoResult(card, autoCf, CurseForgeLocal.CfAutoResult.Installed, false);
                    Ck("installed: done note, no button", card.Message == "CurseForge installed CfAuto-2.zip. In game, type /reload." && card.NoteDoneAt > DateTime.MinValue && card.FallbackVisibility == Visibility.Collapsed, card.Message);
                    ShowCfAutoResult(card, autoCf, CurseForgeLocal.CfAutoResult.LaterWhileRunning, false);
                    Ck("running: next check + Open in CurseForge, not a done note", card.Message == CurseForgeLocal.LaterText() && card.Fallback == OpenInCfButton && card.NoteDoneAt == DateTime.MinValue && card.MessageShown, card.Message);
                    ShowCfAutoResult(card, autoCf, CurseForgeLocal.CfAutoResult.TimedOut, false);
                    Ck("timeout: clear message + Open in CurseForge", card.Message == CurseForgeLocal.TimedOutText && card.FallbackVisibility == Visibility.Visible, card.Message);
                    ShowCfAutoResult(card, autoCf, CurseForgeLocal.CfAutoResult.NotStarted, false);
                    Ck("not started: Open in CurseForge", card.Fallback == OpenInCfButton && card.Message.Contains("Open in CurseForge"), card.Message);
                    card.Message = null;
                    Ck("a new note drops the button", card.FallbackVisibility == Visibility.Collapsed && !card.MessageShown);
                    ThirdPartyCard.ResetHintsForTest();
                    card.Message = "CurseForge is waiting for you"; var h1 = card.OfferHint(5151, CurseForgeDirect.AutoOffHint);
                    var h1Shown = card.HintVisibility == Visibility.Visible && card.HintTip.Contains("Select game instance");
                    card.Message = "CurseForge is waiting for you"; var h2 = card.OfferHint(5151, CurseForgeDirect.AutoOffHint);
                    Ck("the auto-update hint is shown once per addon, with its tooltip", h1 && h1Shown && !h2 && card.HintVisibility == Visibility.Collapsed);

                    // done notes: the timeout, WoW exiting, a SavedVariables write after the install; errors and action notes stay
                    ThirdPartyCard.NoteClearSec = 60;
                    var a = New(); a.SetDoneNote("Updated to CfAuto-2.zip. In game, type /reload.", false); var t0 = a.NoteDoneAt;
                    Ck("done note stays before the timeout", SweepNotes(new[] { a }, t0.AddSeconds(30), false, DateTime.MinValue) == 0 && a.MessageShown);
                    Ck("done note clears after the timeout", SweepNotes(new[] { a }, t0.AddSeconds(61), false, DateTime.MinValue) == 1 && !a.MessageShown && a.NoteDoneAt == DateTime.MinValue);
                    for (int i = 0; i < 20 && a.Message != null; i++) await Task.Delay(50);
                    Ck("its text is dropped after the fade", a.Message == null && a.MessageVisibility == Visibility.Collapsed);
                    var b = New(); b.SetDoneNote("CurseForge installed CfAuto-2.zip. In game, type /reload. (WoW is running; if the addon looks unchanged, restart it.)", true);
                    Ck("WoW still running: stays", SweepNotes(new[] { b }, b.NoteDoneAt.AddSeconds(5), true, DateTime.MinValue) == 0);
                    Ck("WoW exited after the install: clears early", SweepNotes(new[] { b }, b.NoteDoneAt.AddSeconds(5), false, DateTime.MinValue) == 1);
                    // a real SavedVariables file of this test WoW folder (the same folders SavedVarsFiles.Dirs gives every reader)
                    var svDir = Path.Combine(Path.GetFullPath(Path.Combine(addOnsDir, "..", "..")), "WTF", "Account", "NOTESTEST", "SavedVariables");
                    Directory.CreateDirectory(svDir);
                    svFile = Path.Combine(svDir, "ZzNotesTest.lua"); File.WriteAllText(svFile, "ZzNotesTest = {}\n");
                    var c2 = New(); c2.SetDoneNote("Updated to CfAuto-2.zip. In game, type /reload.", true);
                    File.SetLastWriteTimeUtc(svFile, c2.NoteDoneAt.AddMinutes(-10));
                    var before = NewestSavedVarsUtc();
                    Ck("SavedVariables older than the install: stays", SweepNotes(new[] { c2 }, c2.NoteDoneAt.AddSeconds(5), true, before) == 0, before.ToString("o"));
                    File.SetLastWriteTimeUtc(svFile, c2.NoteDoneAt.AddSeconds(3));
                    var after = NewestSavedVarsUtc();
                    Ck("SavedVariables written after the install (/reload): clears", after > c2.NoteDoneAt && SweepNotes(new[] { c2 }, c2.NoteDoneAt.AddSeconds(5), true, after) == 1, after.ToString("o"));
                    var err = New(); err.Message = "Something went wrong: test";
                    var act = New(); act.SetActionNote(CurseForgeLocal.TimedOutText, OpenInCfButton);
                    var wait = New(); wait.Message = "CurseForge is waiting for you: choose Forever as the install location and click Install in its window.";
                    Ck("errors and notes that need the user never clear", SweepNotes(new[] { err, act, wait }, DateTime.UtcNow.AddHours(2), false, DateTime.MaxValue) == 0
                        && err.MessageShown && act.MessageShown && wait.MessageShown && act.FallbackVisibility == Visibility.Visible);
                }
                finally
                {
                    settings.CfDirectOff = off0; settings.CfLetInstallOff = let0; ThirdPartyCard.NoteClearSec = sec0; ThirdPartyCard.ResetHintsForTest();
                    try { if (svFile != null) Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(svFile)), true); } catch { }
                }
            }
            sb.AppendLine(await CurseForgeDirect.SelfTest());
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
                var foo = Path.Combine(addOnsDir, "Foo"); Directory.CreateDirectory(foo);   // the other addon the zip would overwrite (not part of the fixture)
                try { AddonSources.InstallZip(addOnsDir, plain.Entry, plain.Link, "2.0", clientInterface, zip, null); } catch (Exception ex) { err1 = ex.Message; }
                finally { try { Directory.Delete(foo, true); } catch { } }
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
            await ShotWait(500);
            Snapshot(Path.Combine(dir, "9-addons.png"));
            AddonsPage.ScrollToVerticalOffset(AddonsPage.VerticalOffset - 300);
            await Task.Delay(300);
            AddonsPage.ScrollToVerticalOffset(AddonsPage.VerticalOffset - 470);
            await ShotWait(400);
            Snapshot(Path.Combine(dir, "10-suggest.png"));
            return sb.ToString();
        }
    }
}
