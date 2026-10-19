using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// Calendar messages (2.11; typed in shared/Elans.Shared/Lodge/CalendarMessages.cs). Client to server: cal:create, cal:edit, cal:cancel,
// cal:signup, cal:leave, cal:range.
// Server to client: cal:events { events:[event] } (upserts - a cancelled event is an upsert too), cal:range { from, to, events }
// and, on connect, welcome.calendar = { now, events } with the next 30 days.
//
// Every change happens under the store's lock, so two people taking the last place can't both get it. Creating, editing and
// cancelling spend the Chat budget (they post / refresh a chat card) on top of the Control budget every non-chat frame costs;
// a month fetch spends the Page budget (same as scrolling history). The calendar never carries presence: a sign-up is an
// explicit act, the list holds names only, and nothing here depends on whether someone is online or invisible.
public sealed class CalendarModule : IModule
{
    readonly LodgeHub hub;
    public readonly CalendarStore Store;

    public CalendarModule(LodgeHub hub)
    {
        this.hub = hub;
        Store = new CalendarStore(hub.Cfg.DataDir, hub.Log, hub.Clock);
    }

    long Now => Store.NowMs;
    static string NewId() => Guid.NewGuid().ToString("N")[..10];

    // the welcome: events that are still running or start within 30 days
    public CalWelcome WelcomeCalendar()
    {
        var now = Now;
        var until = now + Cal.WelcomeDays * 86_400_000L;
        return new CalWelcome { Now = now, Events = Typed(Store.Snapshot(e => Cal.Over(e) >= now && Cal.Start(e) <= until)) };
    }
    public JsonObject WelcomeView() => LodgeFieldsJson.Build(WelcomeCalendar().Write);

    // stored events (copies) as the typed events the messages carry (shared/Elans.Shared/Lodge/CalendarMessages.cs)
    static List<CalEvent> Typed(IEnumerable<JsonNode> evs) => evs.OfType<JsonObject>().Select(e => CalEvent.Read(new LodgeFieldsJson(e))).ToList();

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        switch (type)
        {
            case LodgeMsg.CalCreate: await Create(me, m); return true;
            case LodgeMsg.CalEdit: await Edit(me, m); return true;
            case LodgeMsg.CalCancel: await Cancel(me, CalCancelMsg.Read(new LodgeFieldsJson(m))); return true;
            case LodgeMsg.CalSignup: await SignUp(me, CalSignupMsg.Read(new LodgeFieldsJson(m))); return true;
            case LodgeMsg.CalLeave: await Leave(me, CalLeaveMsg.Read(new LodgeFieldsJson(m)).Id); return true;
            case LodgeMsg.CalRange: await Range(me, m); return true;
        }
        return false;
    }

    static bool IsOrganiser(Member me, JsonObject ev) => me.Personal && Names.Same(J.Str(ev, "by"), me.Name);
    static bool CanManage(Member me, JsonObject ev) => IsOrganiser(me, ev) || Roles.Can(me.Role, Perm.CalendarManage);

    Task Broadcast(IEnumerable<JsonObject> evs) => hub.Broadcast(LodgeFieldsJson.Build(new CalEventsMsg { Events = Typed(evs) }.Write));

    async Task Create(Member me, JsonObject m)
    {
        if (!Roles.Can(me.Role, Perm.CalendarCreate)) { await hub.Error(me, "Guests can view the calendar and sign up, but not create events", "cal"); return; }
        if (!me.Budget.Chat.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return; }
        var now = Now;
        var spec = Cal.Parse(m, now, Roles.AtLeast(me.Role, "officer"), edit: false, out var why);
        if (spec == null) { await hub.Error(me, why, "cal"); return; }
        Channel ch = null;
        if (spec.Post)
        {
            ch = spec.Channel == null ? hub.Channels.DefaultText : hub.Channels.Get(spec.Channel);
            if (ch == null || ch.Type != "text" || !ch.VisibleTo(me.Role)) { await hub.Error(me, "You can't post in that channel", "cal"); return; }
        }

        List<JsonObject> made;
        lock (Store.Gate)
        {
            if (Store.Prune(now) > 0) Store.Save();
            made = Cal.Build(spec, me.Name, now, NewId);
            if (Store.Future(now) + made.Count > Cal.MaxFuture) { made = null; why = $"The calendar is full ({Cal.MaxFuture} upcoming events) - cancel or wait for some to pass"; }
            else if (Store.Events.Count(e => Cal.Start(e) >= now && !Cal.IsCancelled(e) && Names.Same(J.Str(e, "by"), me.Name)) + made.Count > Cal.MaxOpenPerAuthor)
            { made = null; why = $"You already have {Cal.MaxOpenPerAuthor} upcoming events - cancel some first"; }
            else
            {
                Store.Events.AddRange(made);
                if (!Store.Save()) { foreach (var e in made) Store.Events.Remove(e); made = null; why = "The server couldn't save the event - try again"; }
            }
        }
        if (made == null) { await hub.Error(me, why, "cal"); return; }

        hub.Log.LogInformation("{Name} created calendar event {Id} ({Count} date(s))", me.Name, J.Str(made[0], "id"), made.Count);
        hub.Badges.Note(me.Name, "events");
        if (ch != null)
        {
            var id = await hub.Chat.PostEventCard(me, ch, made[0]);
            lock (Store.Gate) { made[0]["msg"] = id; made[0]["channel"] = ch.Id; Store.Save(); }
        }
        List<JsonObject> copy;
        lock (Store.Gate) copy = made.Select(e => (JsonObject)e.DeepClone()).ToList();
        await Broadcast(copy);
    }

    async Task Edit(Member me, JsonObject m)
    {
        if (!me.Budget.Chat.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return; }
        var now = Now;
        var spec = Cal.Parse(m, now, Roles.AtLeast(me.Role, "officer"), edit: true, out var why);   // the event fields: Cal.Parse validates the raw request
        if (spec == null) { await hub.Error(me, why, "cal"); return; }
        var req = CalEditMsg.Read(new LodgeFieldsJson(m));
        bool following = req.Scope == "following";
        List<JsonObject> changed = new();
        lock (Store.Gate)
        {
            var ev = Store.Find(req.Id);
            if (ev == null) { why = "That event doesn't exist any more"; goto fail; }
            if (!CanManage(me, ev)) { why = "Only the organiser or an officer can change this event"; goto fail; }
            if (Cal.IsCancelled(ev)) { why = "A cancelled event can't be changed"; goto fail; }
            var targets = new List<JsonObject> { ev };
            var series = J.Str(ev, "series");
            if (following && series != null)
                targets = Store.Events.Where(e => J.Str(e, "series") == series && Cal.Start(e) >= Cal.Start(ev) && !Cal.IsCancelled(e)).OrderBy(Cal.Start).ToList();
            foreach (var t in targets)
            {
                var hold = Cal.CheckSlotsHold(t, spec);
                if (hold != null) { why = hold; goto fail; }
            }
            // the first target takes the new instant; later ones keep their date and take the new local wall-clock time
            var tz = Cal.ResolveTz(string.IsNullOrEmpty(spec.Tz) ? J.Str(ev, "tz") : spec.Tz, string.IsNullOrEmpty(spec.Tz) ? J.Int(ev, "off") : spec.Off);
            var newLocal = Cal.ToLocal(spec.Start, tz);
            bool timeChanged = Cal.Start(ev) != spec.Start;
            foreach (var t in targets)
            {
                long start = Cal.Start(t);
                if (ReferenceEquals(t, ev)) start = spec.Start;
                else if (timeChanged) start = Cal.FromLocal(Cal.ToLocal(Cal.Start(t), tz).Date + newLocal.TimeOfDay, tz);
                Cal.Apply(t, spec, start, timeToo: true);
                changed.Add(t);
            }
            if (!Store.Save()) { why = "The server couldn't save the change - try again"; goto fail; }
            goto ok;
        fail:
            changed = null;
        ok:;
        }
        if (changed == null) { await hub.Error(me, why, "cal"); return; }
        foreach (var t in changed) await hub.Chat.RefreshEventCard(t);
        List<JsonObject> copy;
        lock (Store.Gate) copy = changed.Select(e => (JsonObject)e.DeepClone()).ToList();
        await Broadcast(copy);
    }

    async Task Cancel(Member me, CalCancelMsg m)
    {
        if (!me.Budget.Chat.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return; }
        bool following = m.Scope == "following";
        List<JsonObject> changed = new();
        string why = null;
        lock (Store.Gate)
        {
            var ev = Store.Find(m.Id);
            if (ev == null) why = "That event doesn't exist any more";
            else if (!CanManage(me, ev)) why = "Only the organiser or an officer can cancel this event";
            else if (Cal.IsCancelled(ev)) why = null; // already done: nothing to change
            else
            {
                var series = J.Str(ev, "series");
                var targets = following && series != null
                    ? Store.Events.Where(e => J.Str(e, "series") == series && Cal.Start(e) >= Cal.Start(ev) && !Cal.IsCancelled(e)).ToList()
                    : new List<JsonObject> { ev };
                foreach (var t in targets) { t["status"] = "cancelled"; changed.Add(t); }
                // "until cancelled": nothing new is added to a series that was cancelled from here on
                if (following && series != null)
                    foreach (var e in Store.Events.Where(e => J.Str(e, "series") == series && e["rep"] is JsonObject r)) ((JsonObject)e["rep"])["open"] = false;
                if (!Store.Save()) { why = "The server couldn't save the change - try again"; changed.Clear(); }
            }
        }
        if (why != null) { await hub.Error(me, why, "cal"); return; }
        if (changed.Count == 0) return;
        hub.Log.LogInformation("{Name} cancelled calendar event {Id} ({Count} date(s))", me.Name, J.Str(changed[0], "id"), changed.Count);
        foreach (var t in changed) await hub.Chat.RefreshEventCard(t);
        List<JsonObject> copy;
        lock (Store.Gate) copy = changed.Select(e => (JsonObject)e.DeepClone()).ToList();
        await Broadcast(copy);
    }

    async Task SignUp(Member me, CalSignupMsg m)
    {
        var status = m.Status;
        Cal.Result r;
        JsonObject copy;
        lock (Store.Gate)
        {
            var ev = Store.Find(m.Id);
            if (ev == null) { r = Cal.Result.Denied; copy = null; }
            else
            {
                r = Cal.SignUp(ev, me.Name, status, m.Role, m.Comment, Now, Roles.AtLeast(me.Role, "officer"));
                if (r == Cal.Result.Changed && !Store.Save()) hub.Log.LogWarning("Calendar: a sign-up could not be saved");
                copy = r == Cal.Result.Changed ? (JsonObject)ev.DeepClone() : null;
            }
        }
        switch (r)
        {
            case Cal.Result.Closed: await hub.Error(me, "That event is over or was cancelled", "cal"); return;
            case Cal.Result.Full: await hub.Error(me, "That role is full", "cal-full"); return;
            case Cal.Result.Denied: await hub.Error(me, "Pick Going, Maybe or Can't with a role", "cal"); return;
            case Cal.Result.Unchanged: return;
        }
        if (status is "going" or "maybe") hub.Badges.OnSignup(me, m.Id);   // counted once per event
        await Finish(copy);
    }

    async Task Leave(Member me, string id)
    {
        Cal.Result r; JsonObject copy = null;
        lock (Store.Gate)
        {
            var ev = Store.Find(id);
            if (ev == null) return;
            r = Cal.Leave(ev, me.Name);
            if (r == Cal.Result.Changed) { Store.Save(); copy = (JsonObject)ev.DeepClone(); }
        }
        if (r == Cal.Result.Closed) { await hub.Error(me, "That event is over or was cancelled", "cal"); return; }
        if (copy != null) await Finish(copy);
    }

    async Task Finish(JsonObject ev)
    {
        await hub.Chat.RefreshEventCard(ev);
        await Broadcast(new[] { ev });
    }

    // a month (or any window up to 62 days) of events, for the month view. The bounds are read strictly from the raw request (a fractional
    // or wrong-typed bound is refused), not through the lenient CalRangeGetMsg.
    async Task Range(Member me, JsonObject m)
    {
        if (!me.Budget.Page.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return; }
        long from = m["from"] is JsonValue fv && fv.TryGetValue<long>(out var f) ? f : 0;
        long to = m["to"] is JsonValue tv && tv.TryGetValue<long>(out var t) ? t : 0;
        if (from <= 0 || to <= from || to - from > Cal.MaxRangeDays * 86_400_000L) { await hub.Error(me, "Ask for at most two months at a time", "cal"); return; }
        await hub.Send(me, LodgeFieldsJson.Build(new CalRangeMsg { From = from, To = to, Events = Typed(Store.Snapshot(e => Cal.Start(e) >= from && Cal.Start(e) < to)) }.Write));
    }

    // 2.22: a member left / was removed. Their sign-ups come off every event (the events stay: they belong to the lodge's calendar) and the events
    // they organised show "(former member)" as organiser, so nobody inherits the name. Returns (sign-ups removed, events re-attributed).
    public async Task<(int Signups, int Organised)> Purge(string login, string former)
    {
        var changed = new List<JsonObject>();
        int signups = 0, organised = 0;
        lock (Store.Gate)
        {
            foreach (var ev in Store.Events)
            {
                bool touched = false;
                if (ev["signups"] is JsonArray arr)
                    foreach (var e in arr.OfType<JsonObject>().Where(x => Names.Same(J.Str(x, "n"), login)).ToList()) { arr.Remove(e); signups++; touched = true; }
                if (Names.Same(J.Str(ev, "by"), login)) { ev["by"] = former; organised++; touched = true; }
                if (touched) changed.Add(ev);
            }
            if (changed.Count > 0 && !Store.Save()) hub.Log.LogWarning("Calendar: the removal of a member's sign-ups could not be saved");
        }
        if (changed.Count == 0) return (0, 0);
        foreach (var ev in changed) await hub.Chat.RefreshEventCard(ev);
        List<JsonObject> copy;
        lock (Store.Gate) copy = changed.Select(e => (JsonObject)e.DeepClone()).ToList();
        await Broadcast(copy);
        return (signups, organised);
    }

    // housekeeping (every 15 s): drop events 30 days after their end, keep open-ended weekly series 26 weeks ahead
    public async Task Tick()
    {
        var now = Now;
        var added = new List<JsonObject>();
        bool changed;
        lock (Store.Gate)
        {
            changed = Store.Prune(now) > 0;
            var horizon = now + Cal.MaxWeeks * 7L * 86_400_000;
            foreach (var grp in Store.Events.Where(e => e["rep"] is JsonObject r && J.Bool(r, "open") && J.Str(e, "series") != null).GroupBy(e => J.Str(e, "series")).ToList())
            {
                var last = grp.OrderBy(Cal.Start).Last();
                for (int guard = 0; guard < Cal.MaxWeeks; guard++)
                {
                    var next = Cal.Next(last, NewId);
                    if (Cal.Start(next) > horizon || Store.Future(now) + added.Count >= Cal.MaxFuture) break;
                    added.Add(next); last = next;
                }
            }
            if (added.Count > 0) { Store.Events.AddRange(added); changed = true; }
            if (changed) Store.Save();
        }
        if (added.Count > 0) await Broadcast(added);
    }
}
