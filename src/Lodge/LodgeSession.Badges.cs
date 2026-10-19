using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ElansAddonHub.Services;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // Badges and the ladder (Lodge server feature "badges"). Everything comes from the server (the lodge's own records: nothing is collected from the game for this):
    // badges.mine (all badges with progress), badges.member (someone's earned badges), ladder.data, badges.state (the Guild Master's switch). On a server without the
    // feature nothing is asked and nothing is shown. Quiet by design: no toast, no sound, no chat line - a new badge is a small dot on the profile's Badges area until
    // the member has looked at it. Refusals ("switched off", "guests have no badges", "slow down") are swallowed, never shown.
    public partial class LodgeSession
    {
        public bool SupportsBadges { get; private set; }              // the server lists "badges" (and neither the server operator nor the Guild Master switched it off)
        public bool BadgesLocked { get; private set; }                // switched off in the server's own settings: the Guild Master can't turn it on
        public bool BadgesStateKnown { get; private set; }            // the welcome / badges.state said whether the Guild Master's switch is on
        public bool BadgesOwnerOn { get; private set; }               // that switch
        public bool HideFromLadder { get; private set; }
        public int NewBadges { get; private set; }
        public bool MyBadgesLoaded { get; private set; }
        public int EarnedCount { get; private set; }
        public int TotalBadges { get; private set; }
        public long MyPoints { get; private set; }
        public long MySeasonPoints { get; private set; }
        public int MyRank { get; private set; }
        public int MySeasonRank { get; private set; }
        public string BadgeSeason { get; private set; } = "";
        public List<BadgeVM> MyBadges { get; private set; } = new List<BadgeVM>();

        public bool LadderLoaded { get; private set; }
        public string LadderSeason { get; private set; } = "";        // the season the rows belong to ("all" or "2026-10")
        public string LadderCurrent { get; private set; } = "";
        public List<string> LadderSeasons { get; private set; } = new List<string>();
        public List<LadderRowVM> LadderRows { get; private set; } = new List<LadderRowVM>();

        public event Action BadgesChanged;                            // my badges or the feature's state changed
        public event Action LadderChanged;
        public event Action<string> MemberBadgesLoaded;               // login

        readonly Dictionary<string, List<BadgeVM>> memberBadges = new Dictionary<string, List<BadgeVM>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> memberBadgesAsked = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        DateTime badgesLastAsk = DateTime.MinValue;

        public bool CanSwitchBadges => MyRole == "owner" && !BadgesLocked;

        void ReadBadgesFeatures(WelcomeMsg w, List<string> features)
        {
            ResetBadges();
            SupportsBadges = features.Contains("badges");
            var bs = w.Badges;
            if (bs != null) { BadgesStateKnown = true; BadgesOwnerOn = bs.Enabled; BadgesLocked = bs.Locked; }
        }

        void ResetBadges()
        {
            SupportsBadges = false; BadgesLocked = false; BadgesStateKnown = false; BadgesOwnerOn = false; HideFromLadder = false; NewBadges = 0; MyBadgesLoaded = false;
            EarnedCount = 0; TotalBadges = 0; MyPoints = 0; MySeasonPoints = 0; MyRank = 0; MySeasonRank = 0; BadgeSeason = "";
            MyBadges = new List<BadgeVM>();
            LadderLoaded = false; LadderSeason = ""; LadderCurrent = ""; LadderSeasons = new List<string>(); LadderRows = new List<LadderRowVM>();
            memberBadges.Clear(); memberBadgesAsked.Clear(); badgesLastAsk = DateTime.MinValue;
        }

        // the welcome has set Me: ask for my badges once (a guest has none)
        void AfterWelcomeBadges()
        {
            if (SupportsBadges && Me != null && !Me.Guest) RequestMyBadges();
        }

        public void RequestMyBadges()
        {
            if (!SupportsBadges || (Me != null && Me.Guest)) return;
            badgesLastAsk = Clock();
            _ = Send(LodgeJson.Build(new BadgesMineGetMsg().Write));
        }

        // I looked at my badges: the dot goes (locally at once, and on the server so it stays gone)
        public void MarkBadgesSeen()
        {
            if (!SupportsBadges || NewBadges == 0) return;
            NewBadges = 0;
            BadgesChanged?.Invoke();
            _ = Send(LodgeJson.Build(new BadgesSeenMsg().Write));
        }

        public void SetHideFromLadder(bool hide)
        {
            if (!SupportsBadges) return;
            HideFromLadder = hide;
            badgesLastAsk = Clock();
            _ = Send(LodgeJson.Build(new BadgesHideMsg { Hide = hide }.Write));
            BadgesChanged?.Invoke();
        }

        public void RequestLadder(string season)
        {
            if (!SupportsBadges) return;
            badgesLastAsk = Clock();
            _ = Send(LodgeJson.Build(new LadderGetMsg { Season = string.IsNullOrEmpty(season) ? "all" : season }.Write));
        }

        // someone's earned badges (a profile card); asked again at most once a minute
        public void RequestMemberBadges(string login)
        {
            if (!SupportsBadges || string.IsNullOrEmpty(login)) return;
            if (memberBadgesAsked.TryGetValue(login, out var at) && Clock() - at < TimeSpan.FromMinutes(1)) return;
            memberBadgesAsked[login] = Clock();
            badgesLastAsk = Clock();
            _ = Send(LodgeJson.Build(new BadgesMemberGetMsg { Name = login }.Write));
        }

        public List<BadgeVM> BadgesOf(string login) =>
            Me != null && string.Equals(login, Me.Name, StringComparison.OrdinalIgnoreCase) ? MyBadges.Where(b => b.Earned).ToList()
            : login != null && memberBadges.TryGetValue(login, out var l) ? l : null;

        // Guild Master: the whole feature on / off for the lodge
        public void SetBadgesForLodge(bool on)
        {
            if (!CanSwitchBadges) return;
            _ = Send(LodgeJson.Build(new AdminBadgesMsg { On = on }.Write));
        }

        // the badges shown beside a name: highest tier first, then the newest
        public static List<BadgeVM> Top(IEnumerable<BadgeVM> badges, int n) =>
            (badges ?? Enumerable.Empty<BadgeVM>()).Where(b => b.Earned).OrderByDescending(b => b.Tier).ThenByDescending(b => b.At).ThenBy(b => b.Id, StringComparer.Ordinal).Take(n).ToList();

        public static List<BadgeGroupVM> BadgeGroups(IEnumerable<BadgeVM> badges)
        {
            var res = new List<BadgeGroupVM>();
            foreach (var b in badges ?? Enumerable.Empty<BadgeVM>())
            {
                var g = res.FirstOrDefault(x => x.Name == b.FamilyName);
                if (g == null) res.Add(g = new BadgeGroupVM { Name = b.FamilyName });
                g.Items.Add(b);
            }
            return res;
        }

        bool OnBadgesFrame(JsonObject m)
        {
            switch (LodgeJson.Str(m, "t"))
            {
                case LodgeMsg.BadgesState:
                    var state = BadgesStateMsg.Read(new LodgeJsonFields(m));
                    BadgesStateKnown = true; BadgesOwnerOn = state.Enabled; BadgesLocked = state.Locked;
                    SupportsBadges = BadgesOwnerOn && !BadgesLocked;
                    if (SupportsBadges) AfterWelcomeBadges(); else { var keep = BadgesStateKnown; var l = BadgesLocked; var o = BadgesOwnerOn; ResetBadges(); BadgesStateKnown = keep; BadgesLocked = l; BadgesOwnerOn = o; }
                    BadgesChanged?.Invoke(); LadderChanged?.Invoke();
                    return true;
                case LodgeMsg.BadgesMine:
                    var mine = BadgesMineMsg.Read(new LodgeJsonFields(m));
                    if (!SupportsBadges || !mine.Ok) return true;
                    HideFromLadder = mine.Hide; NewBadges = mine.New;
                    MyPoints = mine.Points; MyRank = mine.Rank; MySeasonPoints = mine.SeasonPoints; MySeasonRank = mine.SeasonRank;
                    BadgeSeason = mine.Season ?? ""; EarnedCount = mine.Earned; TotalBadges = mine.Total;
                    MyBadges = mine.Badges.Select(BadgeVM.From).Where(b => b.Id != null).ToList();
                    MyBadgesLoaded = true;
                    BadgesChanged?.Invoke();
                    return true;
                case LodgeMsg.BadgesMember:
                    var theirs = BadgesMemberMsg.Read(new LodgeJsonFields(m));
                    if (!SupportsBadges || !theirs.Ok) return true;
                    var who = theirs.Name;
                    if (string.IsNullOrEmpty(who)) return true;
                    memberBadges[who] = theirs.Badges.Select(BadgeVM.From).Where(b => b.Id != null).ToList();
                    MemberBadgesLoaded?.Invoke(who);
                    return true;
                case LodgeMsg.LadderData:
                    var ladder = LadderDataMsg.Read(new LodgeJsonFields(m));
                    if (!SupportsBadges || !ladder.Ok) return true;
                    LadderSeason = ladder.Season ?? "all"; LadderCurrent = ladder.Current ?? "";
                    LadderSeasons = ladder.Seasons;
                    LadderRows = ladder.Rows.Select(LadderRowVM.From).ToList();
                    LadderLoaded = true;
                    LadderChanged?.Invoke();
                    return true;
            }
            return false;
        }

        // errors that belong to this feature are never shown: a lodge without badges is just a lodge without badges
        bool OnBadgesError(string text, string code)
        {
            text = text ?? "";
            if (text.StartsWith("Badges are switched off", StringComparison.Ordinal) || text.StartsWith("Guests have no badges", StringComparison.Ordinal)
                || text.StartsWith("Nothing to change yet", StringComparison.Ordinal)) return true;
            if (code == "locked") return true;
            if (code == "rate" && Clock() - badgesLastAsk < TimeSpan.FromSeconds(5)) return true;
            return false;
        }

        // ---- test hooks
        public void ForgetMemberBadgesForTest() { memberBadges.Clear(); memberBadgesAsked.Clear(); }
    }
}
