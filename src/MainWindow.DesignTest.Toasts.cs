using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // ELANSHUB_DESIGN_SHOTS=toasts with --selftest <dir>: renders the in-game toast stack (t1-toasts.png, t2-alerts.png) and each step of
    // the welcome guide (w1..w4, w3-error) so the toast / welcome design pass can be compared before / after. Quits when done.
    public partial class MainWindow
    {
        async Task ToastAndWelcomeShots(string dir)
        {
            var s = settings;
            bool o1 = s.ToastsOff, o2 = s.ToastReactions, o3 = s.ToastPins, o4 = s.ToastMentionsOff; var corner = s.ToastCorner;
            try
            {
                s.ToastsOff = false; s.ToastReactions = true; s.ToastPins = true; s.ToastMentionsOff = false;
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Session.FeedForTest("{\"t\":\"welcome\",\"you\":1,\"name\":\"Elan\",\"role\":\"owner\",\"server\":\"2.5.0\",\"canPin\":true,\"features\":[\"reply\",\"react\",\"react2\",\"pin\"],"
                    + "\"reactions\":[\"ready\",\"notready\",\"lol\",\"love\",\"fight\",\"loot\",\"wipe\",\"epic\"],"
                    + "\"channels\":[{\"id\":\"general\",\"name\":\"General\",\"type\":\"text\"},{\"id\":\"loot\",\"name\":\"Loot\",\"type\":\"text\"}],"
                    + "\"users\":[{\"id\":1,\"name\":\"Elan\",\"role\":\"owner\"},{\"id\":2,\"name\":\"Bob\",\"role\":\"officer\",\"game\":{\"playing\":true,\"name\":\"Bob\",\"class\":\"Paladin\",\"classFile\":\"PALADIN\",\"level\":25}},"
                    + "{\"id\":3,\"name\":\"Tess\",\"role\":\"member\",\"game\":{\"playing\":true,\"name\":\"Tess\",\"class\":\"Mage\",\"classFile\":\"MAGE\",\"level\":33}}],"
                    + "\"history\":{\"general\":[],\"loot\":[]},\"pins\":{}}");
                var T = Session.Toasts;
                Session.Select(Session.TextChannels.First(c => c.Id == "loot"));
                Session.IsShownToUser = () => false;
                Session.Presence.SetForTest(null, true);
                T.Visible.Clear(); T.Clock = () => DateTime.UtcNow;
                LodgePage.ForceToastsForTest = true;

                T.Push(new ToastItem { Kind = ToastKind.Mention, Sender = "Bob", Channel = "General", ChannelId = "g", Text = "@Elan the Wailing Caverns group needs a hunter, are you in? We leave in five minutes and have everything else covered.", Who = Session.Members.First(m => m.Name == "Bob") });
                T.Push(new ToastItem { Kind = ToastKind.Reply, Sender = "Tess", Channel = "General", ChannelId = "g2", Text = "Yes, sign me up", Who = Session.Members.First(m => m.Name == "Tess") });
                T.Push(new ToastItem { Kind = ToastKind.Update, Sender = "Hub 2.18.0 is ready", Text = "Open the Hub to update." });
                await Task.Delay(1200); T.Tick(); await Task.Delay(200); T.Tick();
                LodgePage.SnapshotToasts(System.IO.Path.Combine(dir, "t1-toasts.png"));
                T.Visible.Clear(); T.Tick(); await Task.Delay(300);

                T.Push(new ToastItem { Kind = ToastKind.Summary, Sender = "5 missed in combat", Text = "3 mentions, 2 replies · #General, #Loot" });
                T.Push(new ToastItem { Kind = ToastKind.Health, Sender = "Addon error: ElansHelper", Text = "attempt to index a nil value (Core.lua:42)" });
                T.Push(new ToastItem { Kind = ToastKind.Pin, Sender = "Bob", Channel = "General", ChannelId = "g3", Text = "Raid is on Saturday at 20:00", Who = Session.Members.First(m => m.Name == "Bob") });
                await Task.Delay(1200); T.Tick(); await Task.Delay(200); T.Tick();
                LodgePage.SnapshotToasts(System.IO.Path.Combine(dir, "t2-alerts.png"));
                T.Visible.Clear(); T.Tick();
                LodgePage.ForceToastsForTest = false;
                Session.Presence.SetForTest(null, false);

                // the welcome guide, one shot per step
                TabAddons.IsChecked = true;
                GuideInstalledForTest = new List<string>(); GuideJoinedForTest = new List<string>();
                GuideAddonsOverride = new List<GuideAddon>
                {
                    new GuideAddon { Id = "core", Name = "Elan's Core", Description = "The shared library the other addons talk to the Outpost through.", Required = true, Installed = false },
                    new GuideAddon { Id = "hunter", Name = "Elan's Hunter Helper", Description = "Pet, ammo and trap reminders for hunters, with a calm on-screen strip.", Installed = true },
                    new GuideAddon { Id = "bags", Name = "Elan's Bags", Description = "One tidy bag window for every character, searchable.", Installed = false },
                    new GuideAddon { Id = "chat", Name = "Elan's Chat", Description = "Quieter chat tabs and a mention highlight.", Installed = false },
                };
                ShowWelcomeGuide();
                var g = guide;
                var shots = new[] { "w1-folder", "w2-addons", "w3-lodge", "w4-done" };
                for (int i = 0; i < 4; i++)
                {
                    g.Show(i);
                    await Task.Delay(500);
                    SnapshotScaled(System.IO.Path.Combine(dir, shots[i] + ".png"), 1);
                    if (i == 2)
                    {
                        g.LinkText = "not a link"; g.Next();
                        await Task.Delay(300);
                        SnapshotScaled(System.IO.Path.Combine(dir, "w3-lodge-error.png"), 1);
                        g.LinkText = "";
                    }
                }
                g.Skip();
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "toast + welcome shots done");
            }
            catch (Exception e) { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "crashed: " + e); }
            finally { s.ToastsOff = o1; s.ToastReactions = o2; s.ToastPins = o3; s.ToastMentionsOff = o4; s.ToastCorner = corner; SettingsStore.Save(s); }
            Quit();
        }
    }
}
