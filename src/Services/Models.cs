using System.Collections.Generic;
using System.Runtime.Serialization;

namespace ElansAddonHub.Services
{
    // manifest.json - published next to every release. One file describes everything the
    // hub can install, plus the hub's own latest version (for self-update).
    [DataContract]
    public class Manifest
    {
        [DataMember(Name = "hub")] public HubInfo Hub { get; set; }
        [DataMember(Name = "addons")] public List<AddonInfo> Addons { get; set; }
    }

    [DataContract]
    public class HubInfo
    {
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "url")] public string Url { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
    }

    [DataContract]
    public class AddonInfo
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "url")] public string Url { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        // WoW client folder, e.g. "_classic_beta_"
        [DataMember(Name = "flavor")] public string Flavor { get; set; }
        [DataMember(Name = "flavorName")] public string FlavorName { get; set; }
        // top-level folders inside the zip that go into Interface\AddOns
        [DataMember(Name = "folders")] public List<string> Folders { get; set; }
        [DataMember(Name = "changelog")] public List<ChangeEntry> Changelog { get; set; }
        // installed for everyone (e.g. the small Elan's Hub companion), not only for one class
        [DataMember(Name = "required")] public bool Required { get; set; }
        // optional: classes (WoW class files, e.g. "PALADIN") this addon is meant for. Only a hint - never installed automatically.
        [DataMember(Name = "classes")] public List<string> Classes { get; set; }
    }

    [DataContract]
    public class ChangeEntry
    {
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "text")] public string Text { get; set; }
    }

    [DataContract]
    public class QuickLink
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "url")] public string Url { get; set; }
    }

    [DataContract]
    public class Settings
    {
        [DataMember(Name = "wowRoot")] public string WowRoot { get; set; }
        [DataMember(Name = "manifestUrl")] public string ManifestUrl { get; set; }
        [DataMember(Name = "runInBackground")] public bool RunInBackground { get; set; } = true;
        [DataMember(Name = "startWithWindows")] public bool StartWithWindows { get; set; }
        [DataMember(Name = "autoUpdate")] public bool AutoUpdate { get; set; }
        [DataMember(Name = "checkMinutes")] public int CheckMinutes { get; set; } = 30;
        // update channel: null/"stable" = promoted releases only, "test" = the newest build incl. pre-releases
        [DataMember(Name = "channel")] public string Channel { get; set; }
        // hub version known to be a pre-release (so the "Test build" chip can show while running it)
        [DataMember(Name = "testBuild")] public string TestBuild { get; set; }
        // addon id -> manifest version the user rolled back from (not auto-installed again)
        [DataMember(Name = "skipVersions")] public Dictionary<string, string> SkipVersions { get; set; }
        // versions we already showed a tray notification for
        [DataMember(Name = "notified")] public List<string> Notified { get; set; }

        // ---- Lodge (chat/voice). The invite code is stored encrypted for this Windows user (DPAPI).
        [DataMember(Name = "lodgeUrl")] public string LodgeUrl { get; set; }
        [DataMember(Name = "lodgeCode")] public string LodgeCodeProtected { get; set; }
        // 2.38: a one-time invite is traded for a per-device token (DPAPI too); once there is a token the permanent code is no longer kept here
        [DataMember(Name = "lodgeToken")] public string LodgeTokenProtected { get; set; }
        [DataMember(Name = "lodgeDevice")] public string LodgeDeviceName { get; set; }          // shown in "Your devices"; the PC name unless changed
        [DataMember(Name = "lodgeDeviceId")] public string LodgeDeviceId { get; set; }          // random, made once; lets a re-join replace this device instead of adding another
        [DataMember(Name = "lodgeUpgradeNo")] public string LodgeUpgradeDeclined { get; set; }  // lodge address for which "Upgrade this device" was dismissed
        [DataMember(Name = "lodgeName")] public string LodgeName { get; set; }
        [DataMember(Name = "lodgeManualConnect")] public bool LodgeManualConnect { get; set; } // false = connect when the hub starts
        [DataMember(Name = "voicePtt")] public bool VoicePushToTalk { get; set; }
        [DataMember(Name = "voicePttKey")] public int VoicePttKey { get; set; }
        [DataMember(Name = "voiceThreshold")] public double? VoiceThreshold { get; set; }
        [DataMember(Name = "voiceInput")] public int? VoiceInput { get; set; }
        [DataMember(Name = "voiceOutput")] public int? VoiceOutput { get; set; }
        [DataMember(Name = "voiceVolume")] public double? VoiceVolume { get; set; }
        [DataMember(Name = "voicePreset")] public string VoicePreset { get; set; }           // voice changer id (orc, gnome, ...); empty = normal
        [DataMember(Name = "voiceNsOff")] public bool VoiceNoiseSuppressOff { get; set; }       // false (default) = noise suppression on
        [DataMember(Name = "voiceAgcOff")] public bool VoiceAutoGainOff { get; set; }           // false (default) = automatic gain control + limiter on
        [DataMember(Name = "voiceHigh")] public bool VoiceHighQuality { get; set; }              // true = 48 kbps instead of 32
        [DataMember(Name = "lodgeStatus")] public string LodgeStatus { get; set; }      // online | away | busy | dungeon | lfg
        [DataMember(Name = "lodgeNote")] public string LodgeNote { get; set; }
        [DataMember(Name = "autoAwayOff")] public bool AutoAwayOff { get; set; }        // false = AFK after 10 min idle
        [DataMember(Name = "pixelOff")] public bool PixelOff { get; set; }              // true = don't read the addon's status strip from the screen
        [DataMember(Name = "videoPreviewsOff")] public bool VideoPreviewsOff { get; set; } // false = YouTube links in chat show a preview card (fetches the thumbnail + title from YouTube)
        [DataMember(Name = "eventsStripOff")] public bool EventsStripOff { get; set; }      // false (default) = the next events show in a strip above the Lodge chat
        // the filter chips above the Calendar: which kinds are hidden (all false = everything shown). "Show in-game WoW events" (WowEventsOff) stays the master switch for the three WoW kinds.
        [DataMember(Name = "calHideLodge")] public bool CalHideLodge { get; set; }
        [DataMember(Name = "calHideHolidays")] public bool CalHideHolidays { get; set; }
        [DataMember(Name = "calHideResets")] public bool CalHideResets { get; set; }
        [DataMember(Name = "calHideMine")] public bool CalHideMine { get; set; }
        [DataMember(Name = "flightShareOff")] public bool FlightShareOff { get; set; }      // false (default) = my learned flight-path times are uploaded to the lodge (times only); off = nothing is uploaded, downloading still works
        [DataMember(Name = "atlasShareOff")] public bool AtlasShareOff { get; set; }        // false (default) = what Elan's Toolkit observed (loot, vendors, finds, spawn points; no character names) is shared with the lodge, guests never share; off = nothing is uploaded, the lodge's Atlas is still received
        // ---- gathering routes and the leveling guide in game (Atlas / Professions)
        [DataMember(Name = "atlasRecents")] public List<string> AtlasRecents { get; set; }        // the Atlas pages opened last (ids only, newest first, at most 8)
        [DataMember(Name = "atlasPins")] public List<string> AtlasPins { get; set; }              // the Atlas pages the person pinned, in their order (ids only)
        [DataMember(Name = "atlasHomeChar")] public string AtlasHomeChar { get; set; }             // the character Atlas Home recommends for (its key, "Realm-Name"); null / gone = the one played last
        [DataMember(Name = "worldMapLayers")] public List<string> WorldMapLayers { get; set; }       // the layers switched on in the Atlas world map (null = the defaults)
        [DataMember(Name = "worldMapLayersV")] public int WorldMapLayersVersion { get; set; }          // which layer catalog the saved list was made for (a layer added later is switched on once, see WorldMapLayers.WithNewDefaults)
        [DataMember(Name = "worldMapLast")] public string WorldMapLast { get; set; }                  // the continent shown last (its slug)
        [DataMember(Name = "worldMap3d")] public bool WorldMap3d { get; set; }                       // the world map opens in the 3D terrain view (the last mode used)
        [DataMember(Name = "worldMap3dDetail")] public string WorldMap3dDetail { get; set; }          // 3D detail: low | medium (null = the default) | high
        [DataMember(Name = "worldMapExag")] public double WorldMapExag { get; set; }                  // 3D height exaggeration, 1 to 3 (0 = the default, 1.5)
        [DataMember(Name = "worldMap3dBuildings")] public string WorldMap3dBuildings { get; set; }          // 3D buildings: off (null = the default) | bridges | all
        [DataMember(Name = "worldMapFlat")] public bool WorldMapFlat { get; set; }                    // Settings > World map view "Flat only": the 3D view is never started (false = Automatic)
        [DataMember(Name = "worldMap3dProblem")] public string WorldMap3dProblem { get; set; }        // why the 3D view was switched off by itself on this PC (null = it was not); cleared when 3D works again
        [DataMember(Name = "savedRoutes")] public List<ElansAddonHub.Services.Atlas.SavedRoute> SavedRoutes { get; set; }              // named loops per zone (at most SavedRoute.MaxSaved)
        [DataMember(Name = "routeForGuide")] public Dictionary<string, string> RouteForGuide { get; set; }                         // "<profession>:<zone>" -> id of the saved route the in-game guide uses (absent = the automatic loop)
        [DataMember(Name = "followedGuides")] public List<ElansAddonHub.Services.Professions.FollowedGuide> FollowedGuides { get; set; }   // the guides written into SharedData.lua ("Follow in game")
        [DataMember(Name = "classicLodgeOff")] public bool ClassicFromLodgeOff { get; set; }   // false (default) = when the Lodge hosts the Classic data and this PC has none (or an older release), it is fetched from the Lodge automatically; on = never fetched from the Lodge (the Atlas page still offers the public download)
        [DataMember(Name = "classicLodgePaused")] public bool ClassicLodgePaused { get; set; }  // true after "Remove from this PC": no automatic fetch until the person asks for the data again or turns the switch off and on
        [DataMember(Name = "classicShareOffered")] public string ClassicShareOffered { get; set; }   // the release (version string) an owner or officer was already offered to share with the Lodge: asked once per release
        [DataMember(Name = "armoryShare")] public bool ArmoryShareOn { get; set; }            // false (default) = no character is shared; on = the characters in ArmoryChars are shared with the lodge (gear, talents, professions, pets - never bags, bank or gold)
        [DataMember(Name = "armoryChars")] public List<string> ArmoryChars { get; set; }       // "Realm-Name" keys of the characters to share (null / empty = none)
        [DataMember(Name = "upgradeWeights")] public Dictionary<string, string> UpgradeWeights { get; set; }   // Characters > Gear > Upgrades: the stat weights the person changed, "CLASS/spec" -> "agi=1.2;sta=0.4" (only what differs from the defaults)
        [DataMember(Name = "upgradeFilter")] public string UpgradeFilter { get; set; }                         // the filters of that panel, as UpgradeFilters.Serialize writes them (null = the defaults)
        [DataMember(Name = "wowEventsOff")] public bool WowEventsOff { get; set; }          // false (default) = WoW's own calendar (from the companion addon) is listed in the Calendar
        [DataMember(Name = "itemDataOff")] public bool ItemDataOff { get; set; }            // false (default) = item names, stats and levels are read from the user's own WoW installation (Atlas); true = not read
        // ---- Outpost browser (Browser page)
        [DataMember(Name = "browserSearch")] public string BrowserSearch { get; set; }       // search engine id (BrowserUrl.Engines); null = duckduckgo
        [DataMember(Name = "browserRemember")] public bool BrowserRemember { get; set; }     // false (default) = private browsing: nothing is kept; true = logins/cookies kept under <data>\webview2rowser
        [DataMember(Name = "browserAdsOff")] public bool BrowserAdsOff { get; set; }         // false (default) = built-in ad / tracker host blocklist on
        [DataMember(Name = "browserFromChat")] public bool BrowserFromChat { get; set; }     // default off: chat links open in the default browser; on: in the Browser page
        [DataMember(Name = "browserTiles")] public List<QuickLink> BrowserTiles { get; set; } // null = the default tiles
        [DataMember(Name = "shareGameOff")] public bool ShareGameOff { get; set; }      // false = show friends what I play
        // ---- in-game toasts (Settings > In-game overlay)
        [DataMember(Name = "toastsOff")] public bool ToastsOff { get; set; }
        [DataMember(Name = "toastMentionsOff")] public bool ToastMentionsOff { get; set; }
        [DataMember(Name = "toastRepliesOff")] public bool ToastRepliesOff { get; set; }
        [DataMember(Name = "toastReactions")] public bool ToastReactions { get; set; }  // default off
        [DataMember(Name = "toastPins")] public bool ToastPins { get; set; }            // default off
        [DataMember(Name = "toastUpdateOff")] public bool ToastUpdateOff { get; set; }
        [DataMember(Name = "toastHealthOff")] public bool ToastHealthOff { get; set; }      // default on: "Elan's Bags: 2 new errors"
        [DataMember(Name = "realmStatusOff")] public bool RealmStatusOff { get; set; }          // default on: realm dots at the top (needs a Lodge with the realms feature)
        [DataMember(Name = "realmsBackNotice")] public bool RealmsBackNotice { get; set; }    // default off: a quiet toast when the realms are up again
        // Addon health: errors the user already looked at ("seen") and already got a toast for, per "addon|message" -> count
        [DataMember(Name = "healthSeen")] public Dictionary<string, int> HealthSeen { get; set; }
        [DataMember(Name = "healthToasted")] public Dictionary<string, int> HealthToasted { get; set; }
        [DataMember(Name = "toastCorner")] public string ToastCorner { get; set; }      // tl | tr (default) | bl | br
        [DataMember(Name = "shareZoneOff")] public bool ShareZoneOff { get; set; }      // false = friends see my zone / instance
        [DataMember(Name = "shareXpOff")] public bool ShareXpOff { get; set; }          // false = friends see my XP % and rested state
        [DataMember(Name = "autoStatusOff")] public bool AutoStatusOff { get; set; }    // false = "In combat" / AFK are set as my Lodge status automatically
        [DataMember(Name = "lodgeGroupsCollapsed")] public string LodgeGroupsCollapsed { get; set; } // collapsed member-list groups, comma separated (ingame,online,away,invisible)
        [DataMember(Name = "lodgeInvisible")] public bool LodgeInvisible { get; set; }  // "Appear offline"
        [DataMember(Name = "invisibleForget")] public bool InvisibleForget { get; set; } // true = Appear offline resets to visible at every start
        [DataMember(Name = "invisibleHintShown")] public bool InvisibleHintShown { get; set; } // the one-time "others can see your messages" note
        [DataMember(Name = "notify")] public string NotifyMode { get; set; }          // mentions (default) | all | none
        [DataMember(Name = "displayNameHintShown")] public bool DisplayNameHintShown { get; set; } // the one-time "pick a display name" tip
        [DataMember(Name = "soundsOff")] public bool SoundsOff { get; set; }
        [DataMember(Name = "peerVolumes")] public Dictionary<string, double> PeerVolumes { get; set; } // by name, 0..2
        [DataMember(Name = "lastChannel")] public string LastChannel { get; set; }
        [DataMember(Name = "overlayOff")] public bool OverlayOff { get; set; }          // false = overlay on
        [DataMember(Name = "overlayRight")] public bool OverlayRight { get; set; }
        [DataMember(Name = "overlayTop")] public double? OverlayTop { get; set; }       // 0..1 of the screen height
        [DataMember(Name = "overlayAlways")] public bool OverlayAlways { get; set; }    // also when WoW isn't in front
        [DataMember(Name = "cfAutoCheck")] public bool CfAutoCheck { get; set; }        // off by default: let the CurseForge app refresh its update info (daily / when WoW starts)
        [DataMember(Name = "paintedArt")] public bool PaintedArt { get; set; }          // false = vector card icons
        [DataMember(Name = "noLinkHandler")] public bool NoLinkHandler { get; set; }    // false = this PC opens elanshub:// invite links with the Outpost
        [DataMember(Name = "noSplash")] public bool NoSplash { get; set; }              // false = show the campfire splash screen on start
        [DataMember(Name = "lodgeUpdateDismissed")] public string LodgeUpdateDismissed { get; set; } // "server update available" note closed for this server version
        [DataMember(Name = "welcomeDone")] public bool WelcomeDone { get; set; }        // the first-run guide was finished or skipped
        [DataMember(Name = "windowWidth")] public double? WindowWidth { get; set; }
        [DataMember(Name = "windowHeight")] public double? WindowHeight { get; set; }
        // how many cards the Addons page had after the last good load (our list, the Other addons list): the next start shows that many placeholders
        [DataMember(Name = "lastAddonCount")] public int? LastAddonCount { get; set; }
        [DataMember(Name = "lastOtherCount")] public int? LastOtherCount { get; set; }
        // the Addons page as a "grid" (cards side by side as the width allows, the default) or a "list" (one column)
        [DataMember(Name = "addonsView")] public string AddonsView { get; set; }
    }
}
