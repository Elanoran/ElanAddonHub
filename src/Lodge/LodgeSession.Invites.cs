using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // One-time invites, device tokens and "Your devices" (Lodge server 2.10). The pure rules are in LodgeInvites.cs.
    public partial class LodgeSession
    {
        // ---- what this connection is
        public bool UsingToken { get; private set; }               // signed in with a device token (not a permanent code)
        public bool StoppedSignedOut { get; private set; }         // the last stop was a revoked device
        public bool SupportsTokens { get; private set; }           // server 2.10: invites, device tokens, device list
        public bool CanInvite { get; private set; }                // owner and officers: one-time invite links, everyone's devices
        public bool CanRetire { get; private set; }               // owner: retire a permanent code
        public string DeviceId { get; private set; }               // the device I am on (tokens only)
        public string DeviceName { get; private set; }

        void ReadInviteFeatures(Dictionary<string, object> m, List<string> features)
        {
            var v = ParseVersion(ServerVersion);
            SupportsTokens = features.Contains("tokens") && (v == null || v >= new Version(2, 10, 0));
            CanInvite = SupportsTokens && m.Bool("canInvite");
            CanRetire = SupportsTokens && m.Bool("canRetire");
            DeviceId = m.Child("device")?.Str("id");
            DeviceName = m.Child("device")?.Str("name");
            Changed?.Invoke();
        }

        // ---- the lists the Settings pages show
        public ObservableCollection<InviteVM> Invites { get; } = new ObservableCollection<InviteVM>();
        public ObservableCollection<DeviceVM> MyDevices { get; } = new ObservableCollection<DeviceVM>();
        public ObservableCollection<DeviceVM> AllDevices { get; } = new ObservableCollection<DeviceVM>();
        public event Action InvitesChanged, DevicesChanged;
        public event Action<InviteVM, string> InviteCreated;       // the invite and its ready-to-send link (shown once; the secret is in the link only)
        public string LastInviteLink { get; private set; }

        // the lodge this session talks to (the stored address while a test feeds frames without a connection)
        public string LodgeAddress => BaseUrl ?? Settings.LodgeUrl;

        static long NowMs() => new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();

        void OnInviteFrame(Dictionary<string, object> m)
        {
            var now = m.Long("now") > 0 ? m.Long("now") : NowMs();
            switch (m.Str("t"))
            {
                case "admin.invites":
                    Invites.Clear();
                    foreach (var d in m.List("invites").OfType<Dictionary<string, object>>().Select(x => InviteVM.Parse(x, now))
                        .OrderByDescending(i => i.Pending).ThenByDescending(i => i.Created)) Invites.Add(d);
                    InvitesChanged?.Invoke();
                    break;
                case "admin.invite.created":
                    {
                        var inv = InviteVM.Parse(m, NowMs());
                        LastInviteLink = InviteLinks.Make(LodgeAddress, m.Str("secret"));
                        InviteCreated?.Invoke(inv, LastInviteLink);
                        break;
                    }
                case "devices":
                case "admin.devices":
                    {
                        var target = m.Str("t") == "devices" ? MyDevices : AllDevices;
                        target.Clear();
                        foreach (var d in m.List("devices").OfType<Dictionary<string, object>>().Select(x => DeviceVM.Parse(x, now))) target.Add(d);
                        DevicesChanged?.Invoke();
                        break;
                    }
            }
        }

        // ---- requests (the server decides who may: officers and the owner for the invite calls)
        public void RequestInvites() { if (SupportsTokens) _ = Send(new Dictionary<string, object> { ["t"] = "admin.invites" }); }
        public void RequestMyDevices() { if (SupportsTokens) _ = Send(new Dictionary<string, object> { ["t"] = "device.list" }); }
        public void RequestAllDevices() { if (SupportsTokens && CanInvite) _ = Send(new Dictionary<string, object> { ["t"] = "admin.devices" }); }

        // kind: "person" (name + role) or "guest" (uses = how many people may use the link); hours 1..720 (0 = the server's default, 7 days)
        public void CreateInvite(string kind, string name, string role, int hours, int uses = 1) =>
            _ = Send(new Dictionary<string, object> { ["t"] = "admin.invite.create", ["kind"] = kind, ["name"] = name ?? "", ["role"] = role ?? "member", ["hours"] = hours, ["uses"] = uses });

        public void RevokeInvite(string id) => _ = Send(new Dictionary<string, object> { ["t"] = "admin.invite.revoke", ["id"] = id });
        public void RevokeDevice(string id) => _ = Send(new Dictionary<string, object> { ["t"] = "device.revoke", ["id"] = id });
        public void RetireCode(string name) => _ = Send(new Dictionary<string, object> { ["t"] = "admin.retire", ["name"] = name });

        // ---- "Upgrade this device": a quiet offer, once, for someone still on a permanent code on a server that can do tokens
        public bool UpgradeOffered =>
            (Connected || TestFed) && SupportsTokens && !ShowServerNotice
            && UpgradePolicy.ShouldOffer(LodgeCredentials.HasToken(Settings) || UsingToken, LodgeCredentials.HasCode(Settings), SupportsTokens, Me != null && Me.Guest, Settings.LodgeUpgradeDeclined, LodgeAddress);

        public void DeclineUpgrade() { Settings.LodgeUpgradeDeclined = LodgeAddress ?? "local"; SettingsStore.Save(Settings); Changed?.Invoke(); }

        // trade the stored permanent code for a device token and reconnect with it. Null = done, otherwise the sentence to show.
        public async Task<string> UpgradeThisDevice()
        {
            var url = LodgeAddress;
            var code = SettingsStore.Unprotect(Settings.LodgeCodeProtected);
            if (url == null || string.IsNullOrEmpty(code)) return "There is no saved code to upgrade";
            var res = await InviteExchange.Exchange(url, null, code, LodgeCredentials.DeviceName(Settings), LodgeCredentials.DeviceId(Settings));
            if (!res.Ok) return res.Message;
            // the saved code stays until the server welcomes the new token
            Connect(url, res.Token, true, new PendingCredential
            {
                Url = url, Secret = res.Token, IsToken = true, DeviceName = LodgeCredentials.DeviceName(Settings),
                OnCommitted = () => Hint?.Invoke("This device now has its own sign-in. Your permanent code is no longer stored on this PC.")
            });
            return null;
        }
    }
}
