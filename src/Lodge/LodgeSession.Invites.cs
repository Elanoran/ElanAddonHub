using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using Elans.Shared;

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
        public bool CanPurge { get; private set; }                // owner (server 2.22): revert one member's Atlas contributions
        public string DeviceId { get; private set; }               // the device I am on (tokens only)
        public string DeviceName { get; private set; }

        void ReadInviteFeatures(WelcomeMsg w, List<string> features)
        {
            var v = ParseVersion(ServerVersion);
            SupportsTokens = features.Contains("tokens") && (v == null || v >= new Version(2, 10, 0));
            CanInvite = SupportsTokens && w.CanInvite;
            CanRetire = SupportsTokens && w.CanRetire;
            CanPurge = SupportsTokens && w.CanPurge && (v == null || v >= new Version(2, 22, 0));
            DeviceId = w.Device?.Id;
            DeviceName = w.Device?.Name;
            ReadLodgeInfo(w, features);
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

        void OnInviteFrame(JsonObject m)
        {
            long Now(long sent) => sent > 0 ? sent : NowMs();                  // the server's clock when it sent one
            switch (LodgeJson.Str(m, "t"))
            {
                case LodgeMsg.AdminInvites:
                    Invites.Clear();
                    var invites = AdminInvitesMsg.Read(new LodgeJsonFields(m));
                    foreach (var d in invites.Invites.Select(x => InviteVM.From(x, Now(invites.Now)))
                        .OrderByDescending(i => i.Pending).ThenByDescending(i => i.Created)) Invites.Add(d);
                    InvitesChanged?.Invoke();
                    break;
                case LodgeMsg.AdminInviteCreated:
                    {
                        var made = AdminInviteCreatedMsg.Read(new LodgeJsonFields(m));
                        var inv = InviteVM.From(made.Invite, NowMs());
                        LastInviteLink = InviteLinks.Make(LodgeAddress, made.Secret);
                        InviteCreated?.Invoke(inv, LastInviteLink);
                        break;
                    }
                case LodgeMsg.Devices:
                case LodgeMsg.AdminDevices:
                    {
                        bool mine = LodgeJson.Str(m, "t") == LodgeMsg.Devices;
                        var target = mine ? MyDevices : AllDevices;
                        List<DeviceView> rows; long sent;
                        if (mine) { var x = DevicesMsg.Read(new LodgeJsonFields(m)); rows = x.Devices; sent = x.Now; }
                        else { var x = AdminDevicesMsg.Read(new LodgeJsonFields(m)); rows = x.Devices; sent = x.Now; }
                        target.Clear();
                        foreach (var d in rows.Select(x => DeviceVM.From(x, Now(sent)))) target.Add(d);
                        DevicesChanged?.Invoke();
                        break;
                    }
            }
        }

        // ---- requests (the server decides who may: officers and the owner for the invite calls)
        public void RequestInvites() { if (SupportsTokens) _ = Send(LodgeJson.Build(new AdminInvitesGetMsg().Write)); }
        public void RequestMyDevices() { if (SupportsTokens) _ = Send(LodgeJson.Build(new DeviceListMsg().Write)); }
        public void RequestAllDevices() { if (SupportsTokens && CanInvite) _ = Send(LodgeJson.Build(new AdminDevicesGetMsg().Write)); }

        // kind: "person" (name + role) or "guest" (uses = how many people may use the link); hours 1..720 (0 = the server's default, 7 days)
        public void CreateInvite(string kind, string name, string role, int hours, int uses = 1) =>
            _ = Send(LodgeJson.Build(new AdminInviteCreateMsg { Kind = kind, Name = name ?? "", Role = role ?? "member", Hours = hours, Uses = uses }.Write));

        public void RevokeInvite(string id) => _ = Send(LodgeJson.Build(new AdminInviteRevokeMsg { Id = id }.Write));
        public void RevokeDevice(string id) => _ = Send(LodgeJson.Build(new DeviceRevokeMsg { Id = id }.Write));
        public void RetireCode(string name) => _ = Send(LodgeJson.Build(new AdminRetireMsg { Name = name }.Write));

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
