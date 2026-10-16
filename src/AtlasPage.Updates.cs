using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // New game data while a page is open (Outpost 2.70). The readers show the previous game build's data at once after a patch and read the new files in the
    // background, so fresh data often lands while somebody reads a page. A page with content is not redrawn under the reader (no scroll jump, nothing moves):
    // the new page is built off the UI thread and compared with the one shown; when nothing visible changed it stays as it is, otherwise the calm notice
    // "Updated with the new game data  [Show]" floats over the bottom of the page (UpdateNotice); Show - or going to any other page - draws the new data, which fades in.
    // A page that had nothing yet (waiting for its data, or a zones list whose levels were still being worked out) is simply drawn again.
    // A page with nothing to show while its data is read for the first time shows the calm build stack (GameDataWait) instead of a bare message.
    public partial class AtlasPage
    {
        string pendingSig;                     // the signature of the newer page the notice offers (null = none)
        public bool UpdateOfferedForTest => UpdateNotice.ShownForTest;
        public void ShowUpdateForTest() => UpdateNotice.ClickForTest();
        public int SilentUpdatesForTest, OfferedUpdatesForTest;

        void InitUpdates()
        {
            UpdateNotice.ShowClicked += () => { pendingSig = null; _ = ApplyUpdate(); };
        }

        async Task ApplyUpdate()
        {
            await Redraw();
            DataUpdateNotice.FadeIn(Body);
        }

        // a navigation draws the current data anyway: the offer is void
        void ForgetUpdateOffer() { pendingSig = null; UpdateNotice.Hide(); }

        // the data under the open page changed (debounced): redraw what was incomplete, offer what would change
        async Task OfferUpdate()
        {
            int gen = buildGen;
            var id = history.Current ?? "";
            if (string.IsNullOrEmpty(id)) return;
            string oldSig = browse != null ? BrowseSig(browse) : model != null ? ModelSig(model) : null;
            if (oldSig == null) return;
            string newSig;
            try
            {
                newSig = await Task.Run(() => AtlasBrowse.IsBrowseId(id) ? BrowseSig(AtlasBrowse.Run(id)) : ModelSig(AtlasQuery.Build(id)));
            }
            catch (Exception e) { Util.Log("atlas update check " + id + ": " + e.Message); return; }
            if (gen != buildGen || (history.Current ?? "") != id) return;     // the reader went elsewhere meanwhile
            if (newSig == oldSig) { SilentUpdatesForTest++; drawnSig = Signature(); return; }
            if (pendingSig == newSig) return;
            pendingSig = newSig; OfferedUpdatesForTest++;
            UpdateNotice.Offer();
        }

        static string BrowseSig(BrowseResult r)
        {
            if (r == null) return "";
            var sb = new StringBuilder();
            sb.Append(r.NotFound).Append('|').Append(r.Total).Append('|').Append(r.InCategory).Append('|').Append(r.Title).Append('\n');
            foreach (var c in r.Children) sb.Append(c.Title).Append(':').Append(c.Count).Append(';');
            foreach (var n in r.Notes) sb.Append(n).Append('\n');
            foreach (var row in r.Rows) sb.Append(row.Link).Append('|').Append(row.Title).Append('|').Append(row.Detail).Append('|').Append(row.Extra).Append('|').Append(row.Right).Append('\n');
            return sb.ToString();
        }

        static string ModelSig(AtlasPageModel m)
        {
            if (m == null) return "";
            var sb = new StringBuilder();
            sb.Append(m.NotFound).Append('|').Append(m.Title).Append('|').Append(m.Subtitle).Append('|').Append(m.Message).Append('\n');
            foreach (var t in m.Tooltip) sb.Append(t.Text).Append('|').Append(t.Right).Append('\n');
            foreach (var f in m.Facts) sb.Append(f.Label).Append('=').Append(f.Value).Append('\n');
            foreach (var t in m.Texts) sb.Append(t.Title).Append('=').Append(t.Body).Append('\n');
            foreach (var n in m.Notes) sb.Append(n).Append('\n');
            foreach (var s in m.Sections)
            {
                sb.Append('#').Append(s.Title).Append('\n');
                foreach (var row in s.Rows.Concat(s.Hidden)) sb.Append(row.Link).Append('|').Append(row.Title).Append('|').Append(row.Detail).Append('|').Append(row.Right).Append('\n');
            }
            return sb.ToString();
        }

        // ---- waiting for the first read of the game data
        // the readers a page needs, by its id (browse:zones -> maps; item:12 -> items ...)
        static string[] JobsFor(string id)
        {
            id = id ?? "";
            string kind = id.StartsWith("browse:", StringComparison.Ordinal) ? (id.Substring(7).Split('/', '?')[0]) : id.Contains(":") ? id.Substring(0, id.IndexOf(':')) : id;
            switch (kind)
            {
                case "zones": case "instances": case "zone": case "instance": return new[] { MapData.JobId, ZoneLevels.JobId };
                case "items": case "item": return new[] { ItemData.JobId };
                case "spells": case "skills": case "recipes": case "factions": case "spell": case "skill": case "faction": return new[] { SpellData.JobId };
                case "classes": case "races": case "class": case "race": return new[] { ClassData.JobId, SpellData.JobId };
                case "creatures": case "objects": case "quests": case "npc": case "obj": case "quest": return new[] { SeenData.JobId };
                default: return new string[0];
            }
        }

        static string WhatFor(string id)
        {
            var j = JobsFor(id).FirstOrDefault();
            switch (j)
            {
                case MapData.JobId: return "the maps";
                case ItemData.JobId: return "the items";
                case SpellData.JobId: return "the spells";
                case ClassData.JobId: return "the classes and races";
                case SeenData.JobId: return "what your client has seen";
                default: return "the game data";
            }
        }

        // the page has nothing yet and its reader is (about to be) at work: the build stack, not a bare "not ready yet"
        static bool WaitingForData(string id)
        {
            var jobs = JobsFor(id);
            if (jobs.Length == 0) return false;
            bool Loading(string j)
            {
                switch (j)
                {
                    case MapData.JobId: return MapData.Status == MapData.State.Loading || (MapData.Status == MapData.State.NotStarted && MapData.AutoStart);
                    case ItemData.JobId: return ItemData.Status == ItemData.State.Loading || (ItemData.Status == ItemData.State.NotStarted && ItemData.AutoStart);
                    case SpellData.JobId: return SpellData.Status == SpellData.State.Loading || (SpellData.Status == SpellData.State.NotStarted && SpellData.AutoStart);
                    case ClassData.JobId: return ClassData.Status == ClassData.State.Loading || (ClassData.Status == ClassData.State.NotStarted && ClassData.AutoStart);
                    case SeenData.JobId: return SeenData.Status == SeenData.State.Loading || (SeenData.Status == SeenData.State.NotStarted && SeenData.AutoStart);
                    default: return false;
                }
            }
            return Loading(jobs[0]);
        }

        FrameworkElement WaitCard(string id) => new GameDataWait(WhatFor(id), JobsFor(id));
        public GameDataWait WaitCardForTest => Body.Children.OfType<FrameworkElement>().SelectMany(c => c is System.Windows.Controls.Panel p ? p.Children.OfType<FrameworkElement>().Prepend(c) : new[] { c }).OfType<GameDataWait>().FirstOrDefault();
    }
}
