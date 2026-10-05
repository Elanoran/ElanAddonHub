using System;

namespace ElansAddonHub.Lodge
{
    // "Set my Lodge status automatically": In combat -> Busy with the note "In combat" (kept for a few seconds after the fight
    // so it doesn't flicker), the game's AFK flag -> AFK. It only ever fills in a status the person hasn't chosen: anything but a
    // plain Online without a note is a manual choice and is never overridden. Pure logic (no I/O) so it can be tested; the session
    // feeds it every 100 ms and sends whatever it returns - and only after the value really changed and at most every 2 seconds.
    public sealed class AutoStatus
    {
        public static readonly TimeSpan Linger = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
        public const string CombatNote = "In combat";

        bool wasCombat;
        DateTime combatEndedAt = DateTime.MinValue;
        string sentStatus = "online", sentNote = "";
        DateTime sentAt = DateTime.MinValue;

        // the status I would show right now (without sending anything)
        public string Status { get; private set; } = "online";
        public string Note { get; private set; } = "";
        public bool IsAuto { get; private set; }

        public static bool IsManual(string status, string note) =>
            (!string.IsNullOrEmpty(status) && status != "online") || !string.IsNullOrEmpty(note);

        // what to show: manual choice > combat > game AFK > idle away > online
        public void Evaluate(DateTime now, bool enabled, bool inCombat, bool gameAfk, bool idleAway, string manualStatus, string manualNote)
        {
            if (wasCombat && !inCombat) combatEndedAt = now;
            wasCombat = inCombat;
            bool combat = inCombat || (now - combatEndedAt) < Linger;
            manualStatus = string.IsNullOrEmpty(manualStatus) ? "online" : manualStatus;
            manualNote = manualNote ?? "";
            IsAuto = false;
            if (IsManual(manualStatus, manualNote)) { Status = manualStatus; Note = manualNote; return; }
            if (enabled && combat) { Status = "busy"; Note = CombatNote; IsAuto = true; return; }
            if (enabled && gameAfk) { Status = "away"; Note = ""; IsAuto = true; return; }
            if (idleAway) { Status = "away"; Note = ""; IsAuto = true; return; }
            Status = "online"; Note = "";
        }

        // a status to send, or null when nothing changed / it is too soon (the next call retries)
        public bool TrySend(DateTime now, out string status, out string note)
        {
            status = Status; note = Note;
            if (status == sentStatus && note == sentNote) return false;
            if (now - sentAt < Debounce) return false;
            sentStatus = status; sentNote = note; sentAt = now;
            return true;
        }

        // the status was sent by other means (the person chose one, the welcome re-sent it)
        public void MarkSent(DateTime now, string status, string note)
        {
            sentStatus = status; sentNote = note ?? ""; sentAt = now;
        }
    }
}
