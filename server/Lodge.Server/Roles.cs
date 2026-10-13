namespace Lodge;

// Ranks, lowest to highest. Stored per personal code in the codes file ("Name:code:role"),
// the shared LODGE_CODE is always "guest". New features ask Roles.Can(role, Perm.X).
public static class Roles
{
    public static readonly string[] Order = { "guest", "member", "veteran", "officer", "owner" };

    public static string Normalize(string role)
    {
        role = (role ?? "").Trim().ToLowerInvariant();
        if (role == "initiate") role = "guest";
        if (role == "gm" || role == "guildmaster") role = "owner";
        return Array.IndexOf(Order, role) >= 0 ? role : "member";
    }

    public static int Level(string role) => Math.Max(0, Array.IndexOf(Order, Normalize(role)));

    public static bool AtLeast(string role, string min) => Level(role) >= Level(min);

    // what each rank may do - the one place to change or extend permissions
    public static bool Can(string role, Perm p) => p switch
    {
        Perm.Chat => true,
        Perm.Voice => true,
        Perm.ShareFiles => AtLeast(role, "member"),
        Perm.Moderate => AtLeast(role, "officer"),        // kick, server-mute, delete others' messages
        Perm.PinMessages => AtLeast(role, "officer"),     // pin / unpin messages
        Perm.ManageMembers => AtLeast(role, "owner"),     // invites, ranks, removing people
        Perm.ManageChannels => AtLeast(role, "owner"),
        Perm.SeeInvisible => AtLeast(role, "owner"),      // members who chose "Appear offline" (needed to moderate them)
        Perm.ResetProfiles => AtLeast(role, "owner"),
        Perm.Invite => AtLeast(role, "officer"),          // one-time invite links; see and revoke anyone's devices
        Perm.CalendarCreate => AtLeast(role, "member"),   // guests view the calendar and sign up, but do not create events
        Perm.CalendarManage => AtLeast(role, "officer"),  // edit / cancel anyone's event
        Perm.RetireCodes => AtLeast(role, "owner"),       // retire a person's permanent code     // put someone's avatar back to the default (moderation)
        Perm.FlightShare => AtLeast(role, "member"),      // guests may read the shared flight times, not contribute
        Perm.FilesRead => true,                           // 2.14: everyone who can see the lodge sees and saves the shared files
        Perm.FilesManage => AtLeast(role, "officer"),     // 2.14: upload a slot, replace it with a new version, roll back, delete (officers and the owner only)
        _ => false,
    };
}

public enum Perm { Chat, Voice, ShareFiles, Moderate, PinMessages, ManageMembers, ManageChannels, SeeInvisible, ResetProfiles, Invite, RetireCodes, CalendarCreate, CalendarManage, FlightShare, FilesRead, FilesManage }
