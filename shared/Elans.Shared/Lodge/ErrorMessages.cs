namespace Elans.Shared
{
    // The Lodge server's one error frame (server/PROTOCOL.md, WebSocket and 2.8): a refusal or a problem, sent by LodgeHub.Error from every
    // module and read in one place by the Outpost (LodgeSession.OnServerError). The words are for people; the code is for the Outpost.

    // server -> client: { text, code?, room? }
    public sealed class LodgeErrorMsg
    {
        public const string Type = LodgeMsg.Error;
        public string Text;
        public string Code;            // a machine-readable reason (one of Codes); null = not sent
        public string Room;            // the voice room a "room-full" is about; null = not sent

        // every code the Lodge server sends (a shared test checks the server's Error calls against this list). What the Outpost acts on:
        // last-owner (leaving refused), noreps / notfound / disabled / guest (the Armory), room-full (voice), rate (a pending send), locked and
        // rate (badges); the others are shown as text.
        public static readonly string[] Codes =
        {
            "rate",        // too fast (a budget or a rate window)
            "disabled",    // the feature is switched off on this Lodge (Armory, Atlas, badges, datasets)
            "guest",       // not for guests (Armory, badges, datasets)
            "locked",      // set in the server's own settings, not changeable here (badges, the Cairn)
            "invalid",     // a malformed request (Armory)
            "notfound",    // no such character (Armory)
            "noreps",      // reputations are switched off (Armory)
            "cal",         // a calendar request refused (the calendar, a calendar card in the chat)
            "cal-full",    // the calendar is full
            "lfg",         // a sign-up card request refused
            "lfg-full",    // the sign-up card is full
            "room-full",   // the voice room is full (with room)
            "last-owner",  // the last Guild Master can't leave
            "owner-only",  // only the Guild Master may do that (2.31.2: a device link for someone who is already in)
            "address",     // the Cairn: the public address is refused or can't change any more
            "atlas",       // the Cairn: the Atlas is switched off on this server
        };

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("text", Text);
            if (Code != null) w.Set("code", Code);
            if (Room != null) w.Set("room", Room);
        }

        public static LodgeErrorMsg Read(ILodgeFields f) => new LodgeErrorMsg { Text = f.Str("text"), Code = f.Str("code"), Room = f.Str("room") };
    }
}
