using System;
using System.Windows;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // The Atlas page's connections to the rest of the app (the Lodge pool, the browser for web links, item links from chat and the Characters page).
    public partial class MainWindow
    {
        void InitAtlas()
        {
            AtlasView.Init(() => settings.WowRoot);
            AtlasQuery.Context.Lodge = () => Session?.AtlasData;
            AtlasView.LodgeState = LodgeAtlasState;
            AtlasView.OpenLinkOverride = url => OpenChatLink(url);
            LodgePage.OpenAtlas += OpenInAtlas;
            InventoryView.OpenAtlas += OpenInAtlas;
        }

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
