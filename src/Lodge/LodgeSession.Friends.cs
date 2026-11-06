using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // "A friend comes online": a toast over the game, or a tray note when no toast would show. Off until asked for
    // (Settings > Toasts and notifications), for everyone or for chosen people, by login name. Someone who appears offline is never
    // mentioned, and a connection that drops and comes back, or a character switch, says nothing new for FriendQuiet.
    public partial class LodgeSession
    {
        public static readonly TimeSpan FriendQuiet = TimeSpan.FromMinutes(10);
        readonly Dictionary<string, DateTime> friendQuietUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public bool IsChosenFriend(string name) =>
            Settings.ToastFriendNames != null && Settings.ToastFriendNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        // choosing someone turns the feature on for chosen people, or the click would seem to do nothing
        public void SetChosenFriend(string name, bool on)
        {
            if (string.IsNullOrEmpty(name)) return;
            var list = Settings.ToastFriendNames ?? new List<string>();
            list.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (on) list.Add(name);
            Settings.ToastFriendNames = list;
            if (on && Settings.ToastFriends != "all") Settings.ToastFriends = "chosen";
            SettingsStore.Save(Settings);
        }

        bool FriendWanted(string name) => Settings.ToastFriends == "all" || (Settings.ToastFriends == "chosen" && IsChosenFriend(name));

        void FriendJoined(MemberVM m)
        {
            // another device of someone already here isn't them coming online
            if (Members.Any(x => x.Id != m.Id && string.Equals(x.Name, m.Name, StringComparison.OrdinalIgnoreCase))) return;
            if (!Settings.ToastFriendsPlaying) TellFriend(m, "Just came online");
            else if (m.Playing) TellFriend(m, m.GameDetail ?? "Started playing WoW");
        }

        void FriendGameChanged(MemberVM m, bool wasPlaying)
        {
            if (!wasPlaying && m.Playing && Settings.ToastFriendsPlaying) TellFriend(m, m.GameDetail ?? "Started playing WoW");
        }

        void FriendLeft(MemberVM m)
        {
            if (m.Name != null) friendQuietUntil[m.Name] = Toasts.Clock() + FriendQuiet;
        }

        void TellFriend(MemberVM m, string what)
        {
            if (m.IsMe || m.Invisible || m.Offline || string.IsNullOrEmpty(m.Name)) return;
            if (!FriendWanted(m.Name) || Settings.NotifyMode == "none") return;
            var now = Toasts.Clock();
            if (friendQuietUntil.TryGetValue(m.Name, out var until) && now < until) return;
            friendQuietUntil[m.Name] = now + FriendQuiet;
            bool accepted = Toasts.Push(new ToastItem { Kind = ToastKind.Friend, Sender = m.Display, Text = what, Who = m });
            if (!(accepted && (ToastWillShow?.Invoke() ?? false))) Notify?.Invoke(m.Display, what);
            if (Settings.ToastFriendSound && !Toasts.Holding) Sounds.Join();
        }

        public void ForgetFriendQuietForTest() => friendQuietUntil.Clear();
    }
}
