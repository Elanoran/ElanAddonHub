namespace ElansAddonHub
{
    // The product name as people see it (title bar, tray tooltip, dialogs, About page).
    // Renaming the app is a one-line change here. NOT affected: exe name, data folder, mutex, update URLs, user agent.
    public static class Brand
    {
        public const string Name = "Elan's Outpost";
        // Newest Lodge server this build knows about = server/Lodge.Server/Hub.cs "Version". Keep in sync (release.py warns when they differ).
        public const string LatestLodgeServer = "2.7.1";
        public const string ServerUpdateCommand = "sudo lodge-update";
        public static string SourceTip => "Source: " + Name + " (GitHub releases)";
    }
}
