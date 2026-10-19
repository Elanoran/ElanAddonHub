using System.Collections.Generic;

namespace Elans.Shared
{
    // Raid / event calendar messages (server/PROTOCOL.md, 2.11 additions): the field names live only here. The classes only map fields;
    // every rule (who may create, edit or cancel, the limits, time zones, places per role) stays in the Lodge server (Calendar.cs,
    // CalendarModule.cs). The server still parses the event fields of cal:create / cal:edit and the bounds of cal:range from the raw JSON
    // (Cal.Parse, CalendarModule.Range): that parse is the validation, and it refuses a wrong-typed count or bound that the lenient
    // getters here would read as 0.

    // the event fields cal:create and cal:edit share
    public abstract class CalEventFields
    {
        public string Title, Note;
        public long Start;             // unix ms, UTC
        public int DurationMin;        // 0 = no end (not sent)
        public bool Roles = true;      // false = a social event with no role slots (then no slots are sent)
        public int Tank, Healer, Dps;
        public string Tz;              // the creator's zone id
        public int Off;                // its UTC offset in minutes at the start

        protected void WriteFields(ILodgeWriter w)
        {
            w.Set("title", Title); w.Set("note", Note); w.Set("start", Start); w.Set("roles", Roles); w.Set("tz", Tz); w.Set("off", Off);
            if (Roles) { var s = w.Child("slots"); s.Set("tank", Tank); s.Set("healer", Healer); s.Set("dps", Dps); }
            if (DurationMin > 0) w.Set("durationMin", DurationMin);
        }

        protected void ReadFields(ILodgeFields f)
        {
            Title = f.Str("title"); Note = f.Str("note"); Start = f.Long("start"); DurationMin = f.Int("durationMin");
            Roles = !f.Has("roles") || f.Bool("roles"); Tz = f.Str("tz"); Off = f.Int("off");
            var s = f.Child("slots");
            Tank = s?.Int("tank") ?? 0; Healer = s?.Int("healer") ?? 0; Dps = s?.Int("dps") ?? 0;
        }
    }

    // client -> server: a new event (once, a number of weeks, or until cancelled), optionally with a card in a chat channel
    public sealed class CalCreateMsg : CalEventFields
    {
        public const string Type = LodgeMsg.CalCreate;
        public int RepeatWeeks;        // 0 = once, 2..26 = that many weeks, -1 = until cancelled
        public string Channel;         // null = no card in chat ("post": false)

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); WriteFields(w);
            if (RepeatWeeks == -1) w.Child("repeat").Set("open", true);
            else if (RepeatWeeks >= 2) w.Child("repeat").Set("weeks", RepeatWeeks);
            if (Channel == null) w.Set("post", false); else w.Set("channel", Channel);
        }

        public static CalCreateMsg Read(ILodgeFields f)
        {
            var m = new CalCreateMsg(); m.ReadFields(f);
            var rep = f.Child("repeat");
            m.RepeatWeeks = rep == null ? 0 : rep.Bool("open") ? -1 : rep.Int("weeks");
            m.Channel = f.Has("post") && !f.Bool("post") ? null : f.Str("channel");
            return m;
        }
    }

    // client -> server: change an event (scope "following" also changes the later dates of its series)
    public sealed class CalEditMsg : CalEventFields
    {
        public const string Type = LodgeMsg.CalEdit;
        public string Id;
        public string Scope;           // this | following

        public void Write(ILodgeWriter w) { w.Set("t", Type); WriteFields(w); w.Set("id", Id); w.Set("scope", Scope); }
        public static CalEditMsg Read(ILodgeFields f) { var m = new CalEditMsg { Id = f.Str("id"), Scope = f.Str("scope") }; m.ReadFields(f); return m; }
    }

    // client -> server: cancel an event (scope "following" also ends its series from there)
    public sealed class CalCancelMsg
    {
        public const string Type = LodgeMsg.CalCancel;
        public string Id;
        public string Scope;           // this | following

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("id", Id); w.Set("scope", Scope); }
        public static CalCancelMsg Read(ILodgeFields f) => new CalCancelMsg { Id = f.Str("id"), Scope = f.Str("scope") };
    }

    // client -> server: Going / Maybe / Can't, with a role (left out for a social event and for Can't) and a comment
    public sealed class CalSignupMsg
    {
        public const string Type = LodgeMsg.CalSignup;
        public string Id;
        public string Status;          // going | maybe | declined
        public string Role;            // null = not sent
        public string Comment;

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("id", Id); w.Set("status", Status); w.Set("comment", Comment);
            if (Role != null) w.Set("role", Role);
        }
        public static CalSignupMsg Read(ILodgeFields f) => new CalSignupMsg { Id = f.Str("id"), Status = f.Str("status"), Role = f.Str("role"), Comment = f.Str("comment") };
    }

    // client -> server: take your sign-up back
    public sealed class CalLeaveMsg : LodgeIdMsg<CalLeaveMsg> { public const string Type = LodgeMsg.CalLeave; protected override string MsgType => Type; }

    // client -> server: the events in a window (a month view; at most 62 days)
    public sealed class CalRangeGetMsg
    {
        public const string Type = LodgeMsg.CalRange;
        public long From, To;          // unix ms

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("from", From); w.Set("to", To); }
        public static CalRangeGetMsg Read(ILodgeFields f) => new CalRangeGetMsg { From = f.Long("from"), To = f.Long("to") };
    }

    // server -> client: the answer to cal:range
    public sealed class CalRangeMsg
    {
        public const string Type = LodgeMsg.CalRange;
        public long From, To;
        public List<CalEvent> Events = new List<CalEvent>();

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("from", From); w.Set("to", To); CalEvent.WriteList(w, "events", Events); }
        public static CalRangeMsg Read(ILodgeFields f) => new CalRangeMsg { From = f.Long("from"), To = f.Long("to"), Events = CalEvent.ReadList(f, "events") };
    }

    // server -> client: events changed (upserts - a cancelled event is an upsert too)
    public sealed class CalEventsMsg
    {
        public const string Type = LodgeMsg.CalEvents;
        public List<CalEvent> Events = new List<CalEvent>();

        public void Write(ILodgeWriter w) { w.Set("t", Type); CalEvent.WriteList(w, "events", Events); }
        public static CalEventsMsg Read(ILodgeFields f) => new CalEventsMsg { Events = CalEvent.ReadList(f, "events") };
    }

    // welcome.calendar: events still running or starting within the next 30 days
    public sealed class CalWelcome
    {
        public long Now;
        public List<CalEvent> Events = new List<CalEvent>();

        public void Write(ILodgeWriter w) { w.Set("now", Now); CalEvent.WriteList(w, "events", Events); }
        public static CalWelcome Read(ILodgeFields f) => new CalWelcome { Now = f.Long("now"), Events = CalEvent.ReadList(f, "events") };
    }

    // An event, as the server stores and sends it:
    //   { id, series?, title, start, end (0 = none), note, status: open|cancelled, by, roles, slots: { tank, healer, dps },
    //     signups: [ { n, s, r, c } ], channel?, msg?, tz, off, rep?: { weeks, open }, created }
    // A missing string, roles or rep stays missing when written, so an event goes out exactly as it is stored.
    public sealed class CalEvent
    {
        public string Id, Series, Title, Note, Status, By, Channel, Msg, Tz;
        public long Start, End, Created;
        public bool? Roles;            // null when missing (readers treat that as true)
        public int Tank, Healer, Dps;  // slots per role
        public int Off;
        public List<CalSignup> Signups = new List<CalSignup>();
        public CalRepeat Rep;          // on every event of a weekly series

        public void Write(ILodgeWriter w)
        {
            if (Id != null) w.Set("id", Id);
            if (Series != null) w.Set("series", Series);
            if (Title != null) w.Set("title", Title);
            w.Set("start", Start); w.Set("end", End);
            if (Note != null) w.Set("note", Note);
            if (Status != null) w.Set("status", Status);
            if (By != null) w.Set("by", By);
            if (Roles != null) w.Set("roles", Roles.Value);
            var slots = w.Child("slots");
            slots.Set("tank", Tank); slots.Set("healer", Healer); slots.Set("dps", Dps);
            var list = w.List("signups");
            foreach (var s in Signups)
            {
                var e = list.Add();
                if (s.Name != null) e.Set("n", s.Name);
                if (s.Status != null) e.Set("s", s.Status);
                if (s.Role != null) e.Set("r", s.Role);
                if (s.Comment != null) e.Set("c", s.Comment);
            }
            if (Channel != null) w.Set("channel", Channel);
            if (Msg != null) w.Set("msg", Msg);
            if (Tz != null) w.Set("tz", Tz);
            w.Set("off", Off);
            if (Rep != null) { var r = w.Child("rep"); r.Set("weeks", Rep.Weeks); r.Set("open", Rep.Open); }
            w.Set("created", Created);
        }

        public static CalEvent Read(ILodgeFields f)
        {
            var slots = f.Child("slots");
            var rep = f.Child("rep");
            var e = new CalEvent
            {
                Id = f.Str("id"), Series = f.Str("series"), Title = f.Str("title"), Start = f.Long("start"), End = f.Long("end"), Note = f.Str("note"),
                Status = f.Str("status"), By = f.Str("by"), Roles = f.Has("roles") ? f.Bool("roles") : (bool?)null,
                Tank = slots?.Int("tank") ?? 0, Healer = slots?.Int("healer") ?? 0, Dps = slots?.Int("dps") ?? 0,
                Channel = f.Str("channel"), Msg = f.Str("msg"), Tz = f.Str("tz"), Off = f.Int("off"), Created = f.Long("created"),
                Rep = rep == null ? null : new CalRepeat { Weeks = rep.Int("weeks"), Open = rep.Bool("open") },
            };
            foreach (var s in f.List("signups")) e.Signups.Add(new CalSignup { Name = s.Str("n"), Status = s.Str("s"), Role = s.Str("r"), Comment = s.Str("c") });
            return e;
        }

        public static void WriteList(ILodgeWriter w, string key, List<CalEvent> events)
        {
            var list = w.List(key);
            foreach (var e in events) e.Write(list.Add());
        }

        public static List<CalEvent> ReadList(ILodgeFields f, string key)
        {
            var list = new List<CalEvent>();
            foreach (var e in f.List(key)) list.Add(Read(e));
            return list;
        }
    }

    public sealed class CalSignup
    {
        public string Name;            // "n"
        public string Status;          // "s": going | maybe | declined
        public string Role;            // "r": tank | healer | dps | "" (none)
        public string Comment;         // "c"
    }

    public sealed class CalRepeat
    {
        public int Weeks;              // 0 = until cancelled
        public bool Open;              // still being extended (an open-ended series that wasn't cancelled)
    }

    // The snapshot in an event's chat card (a msg with kind "event"): { id, title, start, end, by, status, weekly }. The Outpost draws the
    // live event from its calendar and uses this only when it has no copy.
    public sealed class CalCard
    {
        public string Id, Title, By, Status;
        public long Start, End;
        public bool Weekly;

        public void Write(ILodgeWriter w)
        {
            w.Set("id", Id); w.Set("title", Title); w.Set("start", Start); w.Set("end", End); w.Set("by", By); w.Set("status", Status); w.Set("weekly", Weekly);
        }
        public static CalCard Read(ILodgeFields f) =>
            new CalCard { Id = f.Str("id"), Title = f.Str("title"), Start = f.Long("start"), End = f.Long("end"), By = f.Str("by"), Status = f.Str("status"), Weekly = f.Bool("weekly") };
    }
}
