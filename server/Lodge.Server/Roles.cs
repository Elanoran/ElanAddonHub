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
        Perm.ManageMembers => AtLeast(role, "owner"),     // invites, ranks, removing people
        Perm.ManageChannels => AtLeast(role, "owner"),
        _ => false,
    };
}

public enum Perm { Chat, Voice, ShareFiles, Moderate, ManageMembers, ManageChannels }
