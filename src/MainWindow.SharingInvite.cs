using System;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // A friendly, honest way into sharing (2.87). The welcome guide has a Sharing step (WelcomeGuide.cs); a Lodge member whose Atlas sharing is
    // off sees ONE calm card on Atlas Home (AtlasPage.SharingInvite.cs) - "Turn on" / "Not now" / "What is shared?" - once per offer: the answer is
    // remembered (settings.SharingInviteAnswered) and the card comes back only when the Lodge offers MORE than when it was answered (it starts
    // handing out the Cairn's data). Where the Cairn's rows would be, a non-sharing member gets one quiet line with the way to the switch.
    // Guests and players without a Lodge see none of it. Self-test section "cairn".
    public partial class MainWindow
    {
        // what the Lodge offers a member who would share: "atlas" (the pool), "atlas+cairn" (and the Cairn's numbers); null = nothing to ask
        public string SharingOffer()
        {
            if (Session == null || !(Session.Online || Session.TestFedForView) || !Session.SupportsAtlas || Session.IsGuestNow) return null;
            return Session.SupportsCairnData ? "atlas+cairn" : "atlas";
        }

        static int OfferRank(string offer) => offer == "atlas+cairn" ? 2 : offer == "atlas" ? 1 : 0;

        // the card's text, or null: only while sharing is off, and only for an offer bigger than the one last answered
        public string SharingInviteText()
        {
            if (!settings.AtlasShareOff) return null;
            var offer = SharingOffer();
            if (offer == null || OfferRank(offer) <= OfferRank(settings.SharingInviteAnswered)) return null;
            return offer == "atlas+cairn"
                ? "Your Lodge shares what its members see in the world. Turn on sharing to add yours - and get the Cairn's numbers from other Lodges."
                : "Your Lodge shares what its members see in the world. Turn on sharing to add yours.";
        }

        // the one place the sharing switch is changed from outside Settings (guide, card): saved, the Lodge told, the Cairn's copy follows
        public void SetAtlasSharing(bool on)
        {
            if (settings.AtlasShareOff == !on) return;
            settings.AtlasShareOff = !on;
            SettingsStore.Save(settings);
            Session?.AtlasSettingsChanged();
            AtlasView?.FillSharingInvite();
        }

        public void AnswerSharingInvite(string answer)
        {
            var offer = SharingOffer();
            switch (answer)
            {
                case "on":
                    settings.SharingInviteAnswered = offer;
                    SetAtlasSharing(true);
                    SettingsStore.Save(settings);
                    break;
                case "no":
                    settings.SharingInviteAnswered = offer;
                    SettingsStore.Save(settings);
                    break;
                case "what":
                    ShowTab("settings");
                    SettingsPage.Show("privacy");
                    return;
            }
            AtlasView?.FillSharingInvite();
        }

        void InitSharingInvite()
        {
            AtlasView.SharingInviteText = SharingInviteText;
            AtlasView.SharingInviteAnswer = AnswerSharingInvite;
            AtlasView.OpenSharingSettings = () => { ShowTab("settings"); SettingsPage.Show("lodge"); };
            AtlasQuery.Context.CairnNeedsSharing = () => settings.AtlasShareOff && Session != null && Session.SupportsCairnData && !Session.IsGuestNow;
            // every welcome raises CairnStatusChanged (what the Lodge offers may have changed): the card follows
            if (Session != null) Session.CairnStatusChanged += () => Dispatcher.BeginInvoke(new Action(() => AtlasView?.FillSharingInvite()));
        }
    }
}
