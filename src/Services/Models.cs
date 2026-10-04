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
    public class Settings
    {
        [DataMember(Name = "wowRoot")] public string WowRoot { get; set; }
        [DataMember(Name = "manifestUrl")] public string ManifestUrl { get; set; }
        [DataMember(Name = "runInBackground")] public bool RunInBackground { get; set; } = true;
        [DataMember(Name = "startWithWindows")] public bool StartWithWindows { get; set; }
        [DataMember(Name = "autoUpdate")] public bool AutoUpdate { get; set; }
        [DataMember(Name = "checkMinutes")] public int CheckMinutes { get; set; } = 30;
        // versions we already showed a tray notification for
        [DataMember(Name = "notified")] public List<string> Notified { get; set; }

        // ---- Lodge (chat/voice). The invite code is stored encrypted for this Windows user (DPAPI).
        [DataMember(Name = "lodgeUrl")] public string LodgeUrl { get; set; }
        [DataMember(Name = "lodgeCode")] public string LodgeCodeProtected { get; set; }
        [DataMember(Name = "lodgeName")] public string LodgeName { get; set; }
        [DataMember(Name = "lodgeManualConnect")] public bool LodgeManualConnect { get; set; } // false = connect when the hub starts
        [DataMember(Name = "voicePtt")] public bool VoicePushToTalk { get; set; }
        [DataMember(Name = "voicePttKey")] public int VoicePttKey { get; set; }
        [DataMember(Name = "voiceThreshold")] public double? VoiceThreshold { get; set; }
        [DataMember(Name = "voiceInput")] public int? VoiceInput { get; set; }
        [DataMember(Name = "voiceOutput")] public int? VoiceOutput { get; set; }
        [DataMember(Name = "voiceVolume")] public double? VoiceVolume { get; set; }
        [DataMember(Name = "lodgeStatus")] public string LodgeStatus { get; set; }      // online | away | busy | dungeon | lfg
        [DataMember(Name = "lodgeNote")] public string LodgeNote { get; set; }
        [DataMember(Name = "autoAwayOff")] public bool AutoAwayOff { get; set; }        // false = AFK after 10 min idle
        [DataMember(Name = "pixelOff")] public bool PixelOff { get; set; }              // true = don't read the addon's status strip from the screen
        [DataMember(Name = "shareGameOff")] public bool ShareGameOff { get; set; }      // false = show friends what I play
        [DataMember(Name = "notify")] public string NotifyMode { get; set; }          // mentions (default) | all | none
        [DataMember(Name = "soundsOff")] public bool SoundsOff { get; set; }
        [DataMember(Name = "peerVolumes")] public Dictionary<string, double> PeerVolumes { get; set; } // by name, 0..2
        [DataMember(Name = "lastChannel")] public string LastChannel { get; set; }
        [DataMember(Name = "overlayOff")] public bool OverlayOff { get; set; }          // false = overlay on
        [DataMember(Name = "overlayRight")] public bool OverlayRight { get; set; }
        [DataMember(Name = "overlayTop")] public double? OverlayTop { get; set; }       // 0..1 of the screen height
        [DataMember(Name = "overlayAlways")] public bool OverlayAlways { get; set; }    // also when WoW isn't in front
        [DataMember(Name = "cfAutoCheck")] public bool CfAutoCheck { get; set; }        // off by default: let the CurseForge app refresh its update info (daily / when WoW starts)
        [DataMember(Name = "paintedArt")] public bool PaintedArt { get; set; }          // false = vector card icons
        [DataMember(Name = "windowWidth")] public double? WindowWidth { get; set; }
        [DataMember(Name = "windowHeight")] public double? WindowHeight { get; set; }
    }
}
