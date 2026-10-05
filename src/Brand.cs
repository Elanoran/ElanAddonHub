namespace ElansAddonHub
{
    // The product name as people see it (title bar, tray tooltip, dialogs, About page).
    // Renaming the app is a one-line change here. NOT affected: exe name, data folder, mutex, update URLs, user agent.
    public static class Brand
    {
        public const string Name = "Elan's Outpost";
        public static string SourceTip => "Source: " + Name + " (GitHub releases)";
    }
}
