using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // Lodge 2.38 "versions": tell the Lodge which channel this Outpost follows and which Elan's addons are installed, so the others can see
    // who is behind (MemberVersions). Only after an update check (the manifest says which addons are ours); the session skips an unchanged one.
    public partial class MainWindow
    {
        void ShareMyVersions()
        {
            if (manifest == null || Session == null) return;
            var root = settings.WowRoot;
            Session.SetMyVersions(MemberVersions.Mine(settings, manifest, a => root == null ? null : Installer.InstalledVersion(root, a)));
        }
    }
}
