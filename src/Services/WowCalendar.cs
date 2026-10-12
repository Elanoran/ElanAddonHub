using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ElansAddonHub.Lodge;

namespace ElansAddonHub.Services
{
    // WoW's own calendar as the companion addon saves it (ElansHubDB.calendar in SavedVariables\ElansHub.lua, written on /reload and logout):
    // holidays and world events, raid reset times, the player's own and guild events. Read-only, local to this PC, never sent to the Lodge server.
    // Times: the addon stores real unix seconds (start / finish) when it could measure the calendar clock's offset from real time; without them
    // only the calendar's wall-clock text (startText / endText, "YYYY-MM-DD HH:MM") is there, and this treats that as the PC's local time.
    public class WowEvent
    {
        public string Title = "", Kind = "holiday";       // holiday | raidreset | player | guild
        public long Start, Finish;                        // unix seconds, 0 = not stored
        public string StartText, EndText, Desc, Status;   // Status (player / guild): going | maybe | declined | invited
        public bool AllDay;
        public int Icon;                                  // texture file id, 0 = none
    }

    public class WowCalendarData
    {
        public long At;                                   // unix seconds of the read in game
        public string Realm;
        public int Region;
        public int? ServerOffsetSeconds;                  // the calendar clock's offset from UTC when it was read (null = unknown)
        public List<WowEvent> Events = new List<WowEvent>();
        public bool Found;                                // a calendar block was there at all
        public string SourceFile;
    }

    public static class WowCalendar
    {
        public static readonly string[] Kinds = { "holiday", "raidreset", "player", "guild" };
        public const int MaxEvents = 200;

        // tests replace this; null = read the WoW folder's SavedVariables
        public static Func<WowCalendarData> TestSource;

        static string cacheKey = ""; static WowCalendarData cached;

        // the Lua array part has keys "1", "2", ... in a dictionary
        static IEnumerable<Dictionary<string, object>> Items(Dictionary<string, object> t)
        {
            if (t == null) yield break;
            foreach (var kv in t.Select(k => new { k.Key, k.Value, N = int.TryParse(k.Key, out var n) ? n : int.MaxValue }).OrderBy(x => x.N))
                if (kv.Value is Dictionary<string, object> d) yield return d;
        }

        public static WowCalendarData Parse(string luaText)
        {
            var data = new WowCalendarData();
            Dictionary<string, object> db;
            try { db = LuaData.ReadGlobals(luaText ?? "").TryGetValue("ElansHubDB", out var v) ? v as Dictionary<string, object> : null; }
            catch (Exception e) { Util.Log("wow calendar: " + e.Message); return data; }
            var cal = db?.Child("calendar");
            if (cal == null) return data;
            data.Found = true;
            data.At = cal.Long("at");
            data.Realm = cal.Str("realm");
            data.Region = cal.Int("region");
            if (cal.TryGetValue("serverOffset", out var so) && so is double off) data.ServerOffsetSeconds = (int)off;
            foreach (var d in Items(cal.Child("events")))
            {
                var title = SafeFile.Text(d.Str("title") ?? "").Trim();
                if (title.Length == 0) continue;
                var kind = d.Str("kind");
                if (!Kinds.Contains(kind)) kind = "player";
                var e = new WowEvent
                {
                    Title = title, Kind = kind, Start = d.Long("start"), Finish = d.Long("finish"), StartText = d.Str("startText"), EndText = d.Str("endText"),
                    Desc = SafeFile.Text(d.Str("desc") ?? "").Trim(), Status = d.Str("status"), AllDay = d.Bool("allDay"), Icon = d.Int("icon"),
                };
                if (e.Desc.Length > 400) e.Desc = e.Desc.Substring(0, 400);
                if (e.Start <= 0 && ParseText(e.StartText) == null) continue;     // no usable time at all
                data.Events.Add(e);
                if (data.Events.Count >= MaxEvents) break;
            }
            return data;
        }

        // the newest calendar among all SavedVariables\ElansHub.lua files (one per account / client)
        public static WowCalendarData Read(string root)
        {
            if (TestSource != null) return TestSource() ?? new WowCalendarData();
            WowCalendarData best = new WowCalendarData();
            foreach (var f in GamePresence.SavedVariablesFiles(root))
            {
                try
                {
                    string text;
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, new UTF8Encoding(false), true)) text = sr.ReadToEnd();
                    var d = Parse(text);
                    d.SourceFile = f;
                    if (d.Found && (!best.Found || d.At >= best.At)) best = d;
                }
                catch (Exception e) { Util.Log("wow calendar read failed: " + f + ": " + e.Message); }
            }
            return best;
        }

        // cheap enough to call every few seconds: the file is only read again when it changed
        public static WowCalendarData ReadCached(string root, out bool changed)
        {
            if (TestSource != null) { changed = true; return Read(root); }
            string key = "";
            try { key = string.Join("|", GamePresence.SavedVariablesFiles(root).Select(f => f + ":" + File.GetLastWriteTimeUtc(f).Ticks + ":" + new FileInfo(f).Length)); }
            catch { }
            if (cached != null && key == cacheKey) { changed = false; return cached; }
            cached = Read(root); cacheKey = key; changed = true;
            return cached;
        }

        public static void ResetCache() { cached = null; cacheKey = ""; }

        // "2026-10-18 20:00" (the calendar clock's wall-clock text) -> a date, or null
        public static DateTime? ParseText(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            return DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null;
        }

        // start / end as UTC: the addon's real unix seconds, else the wall-clock text read as this PC's local time (CalendarRules.Zone)
        public static DateTime? StartUtc(WowEvent e)
        {
            if (e.Start > 0) return CalendarRules.FromMs(e.Start * 1000L);
            var t = ParseText(e.StartText);
            return t == null ? (DateTime?)null : CalendarRules.ToUtc(t.Value);
        }

        public static DateTime? EndUtc(WowEvent e)
        {
            if (e.Finish > 0) return CalendarRules.FromMs(e.Finish * 1000L);
            var t = ParseText(e.EndText);
            return t == null ? (DateTime?)null : CalendarRules.ToUtc(t.Value);
        }

        public static string KindLabel(string kind) =>
            kind == "holiday" ? "Holiday" : kind == "raidreset" ? "Raid reset" : kind == "guild" ? "Guild event" : "Your event";

        public static string StatusWord(string s) =>
            s == "going" ? "going" : s == "maybe" ? "maybe" : s == "declined" ? "declined" : s == "invited" ? "invited" : "";
    }
}
