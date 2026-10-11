using System;
using System.Threading.Tasks;
using System.Windows;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // An elanshub:// link was clicked: bring the Outpost forward and show the join with the link filled in. It never joins by itself.
    public partial class MainWindow
    {
        public void OpenInviteLink(string link)
        {
            link = LinkOpen.Validate(link);
            if (link == null) return;
            ShowFromTray();
            ShowTab("lodge");
            LodgePage.ShowInviteLink(link);
        }

        // a start with the link on the command line: wait until the window exists (the splash may still be going)
        public async void OpenInviteLinkWhenReady(string link)
        {
            for (int i = 0; i < 80 && !IsLoaded; i++) await Task.Delay(100);
            await Task.Delay(300);
            OpenInviteLink(link);
        }
    }
}
