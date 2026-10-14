using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // Realm status (Lodge server feature "realms", server 2.20). The Lodge asks the Blizzard Game Data API with its own credentials (they are never here) and pushes:
    //   realms.status { available, region, realms:[{ id, name, slug, ruleset, status UP|DOWN|UNKNOWN, population?, hasQueue? }], checkedAt (unix s), why? } after the welcome and on every change
    //   realms.back { realms:[names], checkedAt } once, when everything that was down is up again
    // Nothing is ever sent to the server for this. A server without the feature shows nothing; available=false (the Forever realms were not found in the API) shows nothing either:
    // a grey dot that is always there would be noise, and the reason is in the Lodge server's log.
    public class RealmVM
    {
        public int Id;
        public string Name = "", Slug = "", Ruleset = "", Status = "UNKNOWN", Population;
        public bool? HasQueue;
        public string Label = "";                      // what the chip says: the ruleset ("Normal", "PvP"...) when it is unique in the list, else the realm's name
        public bool Up => Status == "UP";
        public bool Down => Status == "DOWN";
        public string StatusWord => Up ? "Up" : Down ? "Down" : "Unknown";
    }

    public partial class LodgeSession
    {
        public bool SupportsRealms { get; private set; }
        public bool RealmsAvailable { get; private set; }
        public string RealmsWhy { get; private set; } = "";
        public string RealmsRegion { get; private set; } = "";
        public DateTime RealmsCheckedAt { get; private set; }          // UTC; MinValue = never
        public List<RealmVM> Realms { get; private set; } = new List<RealmVM>();

        public event Action RealmsChanged;
        public event Action<List<string>> RealmsBack;                  // names that came back (the window decides whether to say so)

        void ReadRealmsFeatures(List<string> features)
        {
            ResetRealms();
            SupportsRealms = features.Contains("realms");
        }

        void ResetRealms()
        {
            bool had = SupportsRealms || Realms.Count > 0;
            SupportsRealms = false; RealmsAvailable = false; RealmsWhy = ""; RealmsRegion = ""; RealmsCheckedAt = DateTime.MinValue; Realms = new List<RealmVM>();
            if (had) RealmsChanged?.Invoke();
        }

        void OnRealmsFrame(Dictionary<string, object> m)
        {
            if (!SupportsRealms) return;           // a server that doesn't list the feature has nothing to say about it
            if (m.Str("t") == "realms.back")
            {
                var names = m.List("realms").OfType<string>().Where(n => n.Length > 0).Take(8).ToList();
                if (names.Count > 0) OnRealmsBack(names);
                return;
            }
            RealmsAvailable = m.Bool("available");
            RealmsWhy = RealmsAvailable ? "" : (m.Str("why") ?? "");
            RealmsRegion = (m.Str("region") ?? "").ToUpperInvariant();
            var at = m.Long("checkedAt");
            RealmsCheckedAt = at > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Math.Min(at, 4102444800L)) : Clock();
            var list = new List<RealmVM>();
            foreach (var r in m.List("realms").OfType<Dictionary<string, object>>().Take(16))
            {
                var name = CleanRealmText(r.Str("name"), 40);
                if (name.Length == 0) continue;
                var st = (r.Str("status") ?? "").ToUpperInvariant();
                list.Add(new RealmVM
                {
                    Id = r.Int("id"), Name = name, Slug = CleanRealmText(r.Str("slug"), 40), Ruleset = CleanRealmText(r.Str("ruleset"), 12),
                    Status = st == "UP" || st == "DOWN" ? st : "UNKNOWN",
                    Population = string.IsNullOrWhiteSpace(r.Str("population")) ? null : CleanRealmText(r.Str("population"), 16),
                    HasQueue = r.ContainsKey("hasQueue") ? (bool?)r.Bool("hasQueue") : null,
                });
            }
            foreach (var r in list)
                r.Label = r.Ruleset.Length > 0 && list.Count(x => x.Ruleset == r.Ruleset) == 1 ? r.Ruleset : r.Name;
            Realms = RealmsAvailable ? list : new List<RealmVM>();
            RealmsChanged?.Invoke();
        }

        void OnRealmsBack(List<string> names)
        {
            RealmsBack?.Invoke(names);
            // the quiet line: off unless the member asked for it (Settings > General), and only while the strip itself is on
            if (Settings.RealmsBackNotice && !Settings.RealmStatusOff)
                Toasts.Push(new ToastItem
                {
                    Kind = ToastKind.Realms, Kicker = "Realms", Sender = names.Count == 1 ? names[0] + " is back up" : "Realms are back up",
                    Text = names.Count == 1 ? "The realm is up again." : string.Join(", ", names) + " are up again.",
                });
        }

        static string CleanRealmText(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        // "just now" / "3 min ago" / "2 h ago"
        public static string AgoText(DateTime then, DateTime now)
        {
            var d = now - then;
            if (d.TotalSeconds < 90) return "just now";
            if (d.TotalMinutes < 90) return Math.Round(d.TotalMinutes) + " min ago";
            if (d.TotalHours < 36) return Math.Round(d.TotalHours) + " h ago";
            return Math.Round(d.TotalDays) + " days ago";
        }

        // the tooltip of one chip
        public string RealmTip(RealmVM r)
        {
            var lines = new List<string> { r.Name };
            lines.Add((r.Ruleset.Length > 0 ? r.Ruleset + " · " : "") + r.StatusWord);
            var extra = new List<string>();
            if (!string.IsNullOrEmpty(r.Population)) extra.Add("Population: " + r.Population);
            if (r.HasQueue == true) extra.Add("Queue");
            if (extra.Count > 0) lines.Add(string.Join(" · ", extra));
            lines.Add("Checked " + AgoText(RealmsCheckedAt, Clock()) + " · via your Lodge");
            return string.Join("\n", lines);
        }

        // should the strip show? (the window adds its own switch)
        public bool RealmsShowable => (Online || TestFed) && SupportsRealms && RealmsAvailable && Realms.Count > 0;
    }
}
