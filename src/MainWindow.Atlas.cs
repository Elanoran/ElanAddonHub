using System;
using System.Windows;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // The Atlas page's connections to the rest of the app (the Lodge pool, the browser for web links, item links from chat and the Characters page).
    public partial class MainWindow
    {
        // Talents and Professions are hosted categories of the Atlas (tiles on its start page, ids "talents:..." and "professions:..."): one page each, built once
        public TalentsPage TalentsView;
        public ProfessionsPage ProfessionsView;
        bool talentsWired, professionsWired;
        public ClassicLodgeSync ClassicSync;

        void InitAtlas()
        {
            RegisterAtlasHosts();
            AtlasView.Init(() => settings.WowRoot);
            AtlasView.UseRouteSettings(settings, () => ProfessionsView?.RouteChoiceChanged());
            AtlasQuery.Context.Lodge = () => Session?.AtlasData;
            AtlasQuery.Context.OwnNodes = () => AtlasObs.Read(settings.WowRoot)?.Nodes;      // the gathering nodes this PC's Hub observed (Forever)
            AtlasView.LodgeState = LodgeAtlasState;
            AtlasView.OpenLinkOverride = url => OpenChatLink(url);
            // the Lodge can hand out the Classic data (server 2.17): fetched automatically for members, shareable by the owner and officers
            ClassicSync = new ClassicLodgeSync(Session, () => Dispatcher.CheckAccess() ? AtlasView.EnsureClassicSetup() : Dispatcher.Invoke(() => AtlasView.EnsureClassicSetup()), settings);
            AtlasView.UseLodgeSync(ClassicSync);
            SettingsPage.UseClassicSync(ClassicSync);
            LodgePage.OpenAtlas += OpenInAtlas;
            InventoryView.OpenAtlas += OpenInAtlas;
            // links on the Professions page (recipes, reagents, sources) open their Atlas page
            ElansAddonHub.Services.Professions.ProfessionLinks.OpenRequested += OnProfessionLink;
        }

        // The two pages move into the Atlas: a category each. Order puts Talents after Spells and Professions after Recipes.
        void RegisterAtlasHosts()
        {
            AtlasCategories.Register(new AtlasCategory
            {
                Id = AtlasHosts.Talents, Title = "Talents", Glyph = "", Blurb = "Plan a build for any class, try recommended builds, share a build code or link", Order = 75,
                Host = HostTalents, SubTitle = AtlasHosts.TalentsSubTitle, Search = AtlasHosts.TalentHits,
                Canonical = sub => sub != null && sub.StartsWith("build/", StringComparison.OrdinalIgnoreCase) ? sub : TalentsSub(TalentsView.ClassFileNow, sub),
            });
            AtlasCategories.Register(new AtlasCategory
            {
                Id = AtlasHosts.Professions, Title = "Professions", Glyph = AtlasKinds.Glyph("skill"), Blurb = "Recipes, leveling guides, what you can craft now, camping", Order = 95,
                Host = HostProfessions, SubTitle = AtlasHosts.ProfessionsSubTitle, Search = AtlasHosts.ProfessionHits,
                Canonical = sub => ProfessionsSub(ProfessionsView.ProfessionNow, ProfessionsView.TabNow),
            });
        }

        static string TalentsSub(string classFile, string keep) => string.IsNullOrEmpty(classFile) ? keep : AtlasHosts.TalentsId(classFile).Substring(AtlasHosts.Talents.Length + 1);
        static string ProfessionsSub(string profession, string tab) => AtlasHosts.ProfessionsId(profession, tab).Substring(AtlasHosts.Professions.Length + 1);

        FrameworkElement HostTalents(string sub)
        {
            if (!talentsWired)
            {
                talentsWired = true;
                TalentsView.Embed();
                TalentsView.ClassChanged += cf => AtlasView.HostedChanged(AtlasHosts.Talents, TalentsSub(cf, ""));
            }
            AtlasHosts.ParseTalents(sub, out var classFile, out var code);
            if (classFile.Length > 0) TalentsView.ShowClass(classFile);
            TalentsView.Shown();
            if (code.Length > 0) TalentsView.Import(code);
            return TalentsView;
        }

        FrameworkElement HostProfessions(string sub)
        {
            if (!professionsWired)
            {
                professionsWired = true;
                ProfessionsView.Embed();
                ProfessionsView.ViewChanged += (p, t) => AtlasView.HostedChanged(AtlasHosts.Professions, ProfessionsSub(p, t));
            }
            AtlasHosts.ParseProfessions(sub, out var slug, out var tab);
            ProfessionsView.Navigate(slug, tab);
            return ProfessionsView;
        }

        // Ctrl+6, ShowTab("talents") and friends: the Atlas on that category. Already inside it (on any of its sub-pages) nothing changes, so the class or profession stays.
        public void OpenAtlasCategory(string id)
        {
            TabAtlas.IsChecked = true;
            if ((AtlasView.CurrentId ?? "").StartsWith(id + ":", StringComparison.OrdinalIgnoreCase)) return;
            _ = AtlasView.Open(id + ":");
        }

        void OnProfessionLink(string id) => Dispatcher.BeginInvoke(new Action(() => OpenInAtlas(id)));

        // "Open in Atlas": an Atlas id ("item:123") or a name to search for
        public void OpenInAtlas(string idOrText)
        {
            ShowTab("atlas");
            _ = AtlasView.Open(idOrText);
        }

        string LodgeAtlasState()
        {
            if (Session == null || !Session.Online) return "Not connected to a Lodge";
            if (!Session.SupportsAtlas) return "Connected, but this Lodge's server has no Atlas pool yet (it needs server 2.15)";
            return settings.AtlasShareOff ? "Connected · receiving the Lodge's observations, not sharing yours (Settings > Lodge)" : "Connected · sharing your observations and receiving the Lodge's";
        }
    }
}
