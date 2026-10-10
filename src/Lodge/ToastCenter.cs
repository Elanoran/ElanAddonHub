using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // What a toast is about. Reminders (events with a time) come later: add a kind here, give it a setting in
    // ToastCenter.Enabled and call Push - nothing else needs to change.
    public enum ToastKind { Mention, Reply, Reaction, Pin, Update, Summary, Health }

    public class ToastItem
    {
        public ToastKind Kind;
        public string Sender, Channel, ChannelId, Text;
        public MemberVM Who;          // for the avatar / class icon (null = the hub's own toast)
    }

    public class ToastVM : Bindable
    {
        public ToastKind Kind { get; set; }
        public string Sender { get; set; }
        public string Channel { get; set; }
        public string ChannelId { get; set; }
        public MemberVM Who { get; set; }
        public DateTime Born, Expires;
        string text, title;
        int count = 1;
        double opacity;
        public string Text { get => text; set => Set(ref text, value); }
        public string Title { get => title; set => Set(ref title, value); }
        public int Count { get => count; set => Set(ref count, value); }
        public double Opacity { get => opacity; set => Set(ref opacity, value); }
        public string Kicker => Kind == ToastKind.Mention ? "Mention" : Kind == ToastKind.Reply ? "Reply" : Kind == ToastKind.Reaction ? "Reaction"
            : Kind == ToastKind.Pin ? "Pinned" : Kind == ToastKind.Update ? "Update" : Kind == ToastKind.Health ? "Health" : "Summary";
        public string Glyph => Kind == ToastKind.Mention ? "@" : Kind == ToastKind.Reply ? "" : Kind == ToastKind.Reaction ? ""
            : Kind == ToastKind.Pin ? "" : Kind == ToastKind.Update ? "" : "";
        public bool GlyphIsText => Kind == ToastKind.Mention;
        public Brush Color => Who != null ? Who.Color : Avatar.Frozen("#ABD473");
        public Brush IconBrush => Who?.IconBrush;
        public Visibility InitialVisibility => IconBrush != null ? Visibility.Collapsed : Visibility.Visible;
        public string Initial => Who != null ? Who.Initial : Kind == ToastKind.Summary || Kind == ToastKind.Health ? "!" : Avatar.Initial(Sender ?? "H");
        public Brush NameBrush => Who != null ? Who.ClassBrushOrText : Avatar.Res("Text");
        // the personal avatar and class badge come from the member (the hub's own toasts have none)
        public System.Windows.Media.ImageSource AvatarImage => Who?.AvatarImage;
        public Visibility PersonalVisibility => Who?.PersonalVisibility ?? Visibility.Collapsed;
        public Visibility DefaultVisibility => Who?.DefaultVisibility ?? Visibility.Visible;
        public Visibility BadgeVisibility => Who?.BadgeVisibility ?? Visibility.Collapsed;
        public Brush BadgeRing => Who?.BadgeRing;
        public string BadgeText => Who?.BadgeText;
        public Visibility BadgeTextVisibility => Who?.BadgeTextVisibility ?? Visibility.Collapsed;
        public Brush FrameBrush => null;
        public Brush Ring => Brushes.Transparent;
        public Brush StatusBrush => null;
        public string StatusGlyph => "";
        public Visibility StatusVisibility => Visibility.Collapsed;
        public Visibility InvisibleVisibility => Visibility.Collapsed;
    }

    // The in-game toasts: small cards over WoW (click-through, never taking focus - see ToastWindow).
    // Rules: a global switch and one per kind; nothing is shown while I'm in combat (they wait and arrive as ONE summary after
    // the fight); nothing when the Lodge window is focused on that very channel; at most 3 on screen; a burst of the same kind in
    // the same channel becomes one card ("3 new mentions in #general"). Pure logic with an injectable clock.
    public class ToastCenter
    {
        public static readonly TimeSpan Stay = TimeSpan.FromSeconds(6);
        public const int Max = 3;
        const double FadeIn = 0.25, FadeOut = 0.45;

        readonly Settings settings;
        public Func<DateTime> Clock = () => DateTime.UtcNow;
        public Func<bool> InCombat = () => false;
        public Func<string, bool> ChannelOnScreen = ch => false;   // the Lodge window is focused and shows this channel
        public readonly ObservableCollection<ToastVM> Visible = new ObservableCollection<ToastVM>();
        readonly List<ToastItem> held = new List<ToastItem>();
        public int HeldCount => held.Count;
        public int Shown { get; private set; }                     // toasts created so far (tests)

        public ToastCenter(Settings s) { settings = s; }

        public bool Enabled(ToastKind k)
        {
            if (settings.ToastsOff) return false;
            switch (k)
            {
                case ToastKind.Mention: return !settings.ToastMentionsOff;
                case ToastKind.Reply: return !settings.ToastRepliesOff;
                case ToastKind.Reaction: return settings.ToastReactions;   // default off
                case ToastKind.Pin: return settings.ToastPins;             // default off
                case ToastKind.Update: return !settings.ToastUpdateOff;
                case ToastKind.Health: return !settings.ToastHealthOff;
                default: return true;
            }
        }

        public void Push(ToastItem it)
        {
            if (!Enabled(it.Kind)) return;
            if (it.ChannelId != null && ChannelOnScreen(it.ChannelId)) return;
            if (InCombat()) { held.Add(it); if (held.Count > 60) held.RemoveAt(0); return; }
            Show(it);
        }

        void Show(ToastItem it)
        {
            var now = Clock();
            // coalesce a burst: same kind + channel still on screen
            var same = Visible.FirstOrDefault(v => v.Kind == it.Kind && v.ChannelId == it.ChannelId && it.Kind != ToastKind.Update && it.Kind != ToastKind.Summary && it.Kind != ToastKind.Health);
            if (same != null)
            {
                same.Count++;
                same.Expires = now + Stay;
                same.Title = Plural(it.Kind, same.Count, it.Channel);
                same.Text = it.Sender + ": " + it.Text;
                return;
            }
            var vm = new ToastVM
            {
                Kind = it.Kind, Sender = it.Sender, Channel = it.Channel, ChannelId = it.ChannelId, Who = it.Who, Born = now, Expires = now + Stay,
                Title = it.Kind == ToastKind.Update || it.Kind == ToastKind.Summary || it.Kind == ToastKind.Health ? it.Sender : it.Sender + (it.Channel != null ? " · #" + it.Channel : ""),
                Text = it.Text, Opacity = 0,
            };
            Visible.Add(vm);
            Shown++;
            while (Visible.Count > Max) Visible.RemoveAt(0);
        }

        static string Plural(ToastKind k, int n, string channel)
        {
            var noun = k == ToastKind.Mention ? "mention" : k == ToastKind.Reply ? "repl" + (n == 1 ? "y" : "ies") : k == ToastKind.Reaction ? "reaction" : "pin";
            if (k != ToastKind.Reply && n != 1) noun += "s";
            return $"{n} new {noun}" + (channel != null ? " in #" + channel : "");
        }

        // every ~100 ms: fade, expire, and after combat one summary of what was held
        public bool NeedsTick => held.Count > 0 || Visible.Count > 0;
        public void Tick()
        {
            var now = Clock();
            if (held.Count > 0 && !InCombat())
            {
                var items = held.ToList();
                held.Clear();
                if (items.Count == 1) Show(items[0]);
                else
                {
                    var parts = items.GroupBy(i => i.Kind).Select(g => g.Count() + " " + (g.Key == ToastKind.Mention ? "mention" : g.Key == ToastKind.Reply ? "repl" + (g.Count() == 1 ? "y" : "ies")
                        : g.Key == ToastKind.Reaction ? "reaction" : g.Key == ToastKind.Pin ? "pin" : g.Key == ToastKind.Health ? "addon error" : "update") + (g.Count() > 1 && g.Key != ToastKind.Reply ? "s" : ""));
                    var chans = items.Where(i => i.Channel != null).Select(i => "#" + i.Channel).Distinct().ToList();
                    Show(new ToastItem { Kind = ToastKind.Summary, Sender = items.Count + " missed in combat", Text = string.Join(", ", parts) + (chans.Count > 0 ? " · " + string.Join(", ", chans.Take(3)) : "") });
                }
            }
            foreach (var v in Visible.ToList())
            {
                if (now >= v.Expires) { Visible.Remove(v); continue; }
                double age = (now - v.Born).TotalSeconds, left = (v.Expires - now).TotalSeconds;
                v.Opacity = Math.Max(0, Math.Min(1, Math.Min(age / FadeIn, left / FadeOut)));
            }
        }
    }
}
