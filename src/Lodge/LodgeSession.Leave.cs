using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // Lodge server 2.12: the lodge's name and welcome text, and leaving the lodge for good ("member.leave"). Protocol: server/PROTOCOL.md, "2.12 additions".
    public partial class LodgeSession
    {
        public bool SupportsLeave { get; private set; }                 // the server can remove a member's whole membership
        public bool SupportsLodgeInfo { get; private set; }             // the server keeps a lodge name and welcome text
        public string LodgeName { get; private set; } = "";             // set by the Guild Master; empty = not named yet
        public string LodgeWelcome { get; private set; } = "";
        public string LodgeDisplayName => string.IsNullOrWhiteSpace(LodgeName) ? "the lodge" : LodgeName;

        public event Action LodgeInfoChanged;
        public event Action Left;                                       // the server removed my membership (the view forgets the sign-in)
        public event Action<string> LeaveRefused;                       // "Transfer ownership first": I am the last Guild Master

        void ReadLodgeInfo(Dictionary<string, object> m, List<string> features)
        {
            SupportsLeave = features.Contains("leave");
            SupportsLodgeInfo = features.Contains("lodgeinfo");
            ApplyLodgeInfo(m.Child("lodge"));
        }

        void ApplyLodgeInfo(Dictionary<string, object> lodge)
        {
            LodgeName = lodge?.Str("name") ?? "";
            LodgeWelcome = lodge?.Str("welcome") ?? "";
            LodgeInfoChanged?.Invoke();
        }

        // frames of this part; true = handled
        bool OnLeaveFrame(Dictionary<string, object> m)
        {
            switch (m.Str("t"))
            {
                case LodgeMsg.LodgeInfo: ApplyLodgeInfo(m.Child("lodge")); Changed?.Invoke(); return true;
                case LodgeMsg.MemberLeft: Left?.Invoke(); return true;
            }
            return false;
        }

        // "Leave the lodge": the server drops my codes entry, all my devices and my pending device links; my messages stay.
        // Answers: member.left (done) or an error with code "last-owner".
        public void RequestLeave() => _ = Send(new Dictionary<string, object> { ["t"] = LodgeMsg.MemberLeave });

        // Transfer = make someone else a Guild Master (owner only); afterwards I may leave or stay.
        public void TransferOwnership(string name) => SetRole(name, "owner");

        // owner: name (max 40) and a short welcome text (max 280 characters, 4 lines); empty clears
        public void SetLodgeInfo(string name, string welcome) =>
            _ = Send(new Dictionary<string, object> { ["t"] = LodgeMsg.AdminLodgeSet, ["name"] = name ?? "", ["welcome"] = welcome ?? "" });

        // the same limits as the server, so a form can say so before asking
        public const int MaxLodgeName = 40, MaxLodgeWelcome = 280, MaxLodgeWelcomeLines = 4;

        public static string CheckLodgeInfo(string name, string welcome)
        {
            name = (name ?? "").Trim(); welcome = (welcome ?? "").Replace("\r\n", "\n").Trim();
            if (name.Length > MaxLodgeName) return "The lodge name can be at most " + MaxLodgeName + " characters";
            if (welcome.Length > MaxLodgeWelcome) return "The welcome text can be at most " + MaxLodgeWelcome + " characters";
            if (welcome.Split('\n').Length > MaxLodgeWelcomeLines) return "The welcome text can have at most " + MaxLodgeWelcomeLines + " lines";
            return null;
        }
    }

    // Who has to type what before a destructive action (Settings > Lodge > Danger zone). Pure, so the self-test can check the rules.
    public static class LeavePolicy
    {
        // "Sign out this PC" by an owner whose only device this is: nobody else holds the key back in, so the lodge's name must be typed.
        // (Back in: sudo lodge-admin recover-owner <name> on the server.)
        public static bool SignOutNeedsTypedName(string myRole, int deviceCount) => myRole == "owner" && deviceCount <= 1;

        public static string RecoverCommand(string myName) => "sudo lodge-admin recover-owner " + (string.IsNullOrWhiteSpace(myName) ? "<your name>" : myName);

        // the phrase to type: the lodge's name, or "lodge" when it has no name yet
        public static string Phrase(string lodgeName, string address) =>
            !string.IsNullOrWhiteSpace(lodgeName) ? lodgeName.Trim() : LodgeView.ShortAddress(address);

        public static bool Matches(string typed, string phrase) =>
            !string.IsNullOrWhiteSpace(phrase) && string.Equals((typed ?? "").Trim(), phrase.Trim(), StringComparison.OrdinalIgnoreCase);

        // who can take over when the last Guild Master wants to leave: officers first; members only when there is no officer
        public static List<string> TransferCandidates(IEnumerable<KeyValuePair<string, string>> nameAndRole, string me)
        {
            var others = nameAndRole.Where(p => !string.Equals(p.Key, me, StringComparison.OrdinalIgnoreCase) && p.Value != "owner" && p.Value != "guest").ToList();
            var officers = others.Where(p => p.Value == "officer").Select(p => p.Key).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (officers.Count > 0) return officers;
            return others.Select(p => p.Key).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
