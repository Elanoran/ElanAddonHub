using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ElansAddonHub.Lodge
{
    // one picture in the avatar gallery (Id null = "no avatar": the class-colour default)
    public class AvatarTile : Bindable
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public ImageSource Image { get; set; }
        public MemberVM DefaultLook { get; set; }
        bool selected;
        public bool Selected { get => selected; set { if (Set(ref selected, value)) Raise(nameof(RingBrush)); } }
        public Brush RingBrush => selected ? Avatar.Res("Accent") : Brushes.Transparent;
        public Visibility ImageVisibility => Image != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DefaultVisibility => Image == null ? Visibility.Visible : Visibility.Collapsed;
        public void SetImage(ImageSource img) { Image = img; Raise(nameof(Image)); }
    }

    // one accent colour swatch (Id null = none)
    public class SwatchTile : Bindable
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Brush Fill { get; set; }
        public Visibility NoneVisibility => Id == null ? Visibility.Visible : Visibility.Collapsed;
        bool selected;
        public bool Selected { get => selected; set { if (Set(ref selected, value)) Raise(nameof(RingBrush)); } }
        public Brush RingBrush => selected ? Avatar.Res("Text") : Brushes.Transparent;
    }

    // the "Edit profile" page: everything the member can choose, with a live preview card that shows what others will see.
    public class ProfileEditVM : Bindable
    {
        readonly LodgeSession session;
        public ObservableCollection<AvatarTile> Avatars { get; } = new ObservableCollection<AvatarTile>();
        public ObservableCollection<SwatchTile> Swatches { get; } = new ObservableCollection<SwatchTile>();
        public ObservableCollection<CharVM> Chars { get; } = new ObservableCollection<CharVM>();
        public ObservableCollection<string> MainChoices { get; } = new ObservableCollection<string>();
        public const string NoMain = "(none)";
        public MemberVM Preview { get; }
        public ProfileCardVM Card { get; }
        public bool IsGuest { get; }
        public bool Supported { get; }

        string avatarId, accent, about = "", playTimes = "", main, showChars = "all", visibleTo = "everyone";

        public ProfileEditVM(LodgeSession session)
        {
            this.session = session;
            var me = session.Me;
            var p = session.MyProfile ?? new ProfileData { Name = me?.Name };
            Supported = session.SupportsProfile;
            IsGuest = me != null && me.Guest;
            avatarId = AvatarCatalog.IsAvatar(p.AvatarId) ? p.AvatarId : null;
            accent = AvatarCatalog.IsAccent(p.Accent) ? p.Accent : null;
            about = p.About ?? ""; playTimes = p.PlayTimes ?? ""; main = p.Main;
            showChars = p.ShowChars ?? "all"; visibleTo = p.VisibleTo ?? "everyone";
            foreach (var c in p.Chars) Chars.Add(new CharVM { Name = c.Name, Class = c.Class, ClassFile = c.ClassFile, Race = c.Race, RaceFile = c.RaceFile, Level = c.Level, Seen = c.Seen, Hidden = c.Hidden, IsMain = c.IsMain });
            Preview = new MemberVM { Id = -1, Name = me?.Name ?? "You", Role = session.MyRole, IsMe = true };
            if (me != null) Preview.CopyGameFrom(me);
            var look = new MemberVM { Id = -2, Name = me?.Name ?? "You", Role = session.MyRole };
            if (me != null) look.CopyGameFrom(me);

            Avatars.Add(new AvatarTile { Id = null, Name = "Default look", DefaultLook = look });
            foreach (var id in AvatarCatalog.Ids) Avatars.Add(new AvatarTile { Id = id, Name = AvatarCatalog.Name(id) });
            Swatches.Add(new SwatchTile { Id = null, Name = "No accent", Fill = Brushes.Transparent });
            foreach (var a in AvatarCatalog.AccentIds)
            {
                var b = new SolidColorBrush(AvatarCatalog.AccentColor(a)); b.Freeze();
                Swatches.Add(new SwatchTile { Id = a, Name = AvatarCatalog.AccentName(a), Fill = b });
            }
            Card = new ProfileCardVM(Preview);
            RefreshChoices();
            Update();
        }

        void RefreshChoices()
        {
            MainChoices.Clear();
            MainChoices.Add(NoMain);
            foreach (var c in Chars) MainChoices.Add(c.Name);
            foreach (var c in Chars) c.IsMain = main != null && string.Equals(c.Name, main, StringComparison.OrdinalIgnoreCase);
            if (main != null && !Chars.Any(c => c.IsMain)) main = null;
        }

        void Update()
        {
            foreach (var t in Avatars) { t.Selected = t.Id == avatarId; t.SetImage(t.Id == null ? null : AvatarCatalog.Image(t.Id, accent)); }
            foreach (var s in Swatches) s.Selected = s.Id == accent;
            Preview.SetAvatar(avatarId, accent);
            Card.Data = BuildPreview();
            Raise(nameof(AboutCounter)); Raise(nameof(AboutCounterBrush)); Raise(nameof(Hint)); Raise(nameof(HintVisibility)); Raise(nameof(MainChoice));
            Raise(nameof(ShowAll)); Raise(nameof(ShowMain)); Raise(nameof(ShowNone)); Raise(nameof(VisEveryone)); Raise(nameof(VisOfficers));
            Raise(nameof(CharsVisibility)); Raise(nameof(NoCharsVisibility));
        }

        // what other people will see (the server applies the same rules)
        ProfileData BuildPreview()
        {
            var d = new ProfileData { Name = Preview.Name, Found = true, Role = session.MyRole, Online = true, AvatarId = avatarId, Accent = accent, About = about, PlayTimes = playTimes, Main = main };
            foreach (var c in Chars.OrderByDescending(x => x.Seen))
            {
                bool isMain = main != null && string.Equals(c.Name, main, StringComparison.OrdinalIgnoreCase);
                bool shown = (showChars == "all" && (!c.Hidden || isMain)) || (showChars == "main" && isMain);
                if (shown) d.Chars.Add(new CharVM { Name = c.Name, Class = c.Class, ClassFile = c.ClassFile, Race = c.Race, Level = c.Level, IsMain = isMain });
            }
            return d;
        }

        public string AvatarId => avatarId;
        public string Accent => accent;
        public void SelectAvatar(string id) { avatarId = id; Update(); }
        public void SelectAccent(string id) { accent = id; Update(); }
        public void Refresh() => Update();

        public string About { get => about; set { value = (value ?? "").Replace("\r", " ").Replace("\n", " "); if (Set(ref about, value.Length > 140 ? value.Substring(0, 140) : value)) Update(); } }
        public string PlayTimes { get => playTimes; set { if (Set(ref playTimes, (value ?? "").Length > 40 ? value.Substring(0, 40) : value ?? "")) Update(); } }
        public string AboutCounter => about.Length + " / 140";
        public Brush AboutCounterBrush => about.Length >= 130 ? Avatar.Res("Gold") : Avatar.Res("TextDim");

        public string MainChoice
        {
            get => main ?? NoMain;
            set
            {
                var v = value == null || value == NoMain ? null : value;
                if (v == main) return;
                main = v;
                foreach (var c in Chars) { c.IsMain = main != null && string.Equals(c.Name, main, StringComparison.OrdinalIgnoreCase); if (c.IsMain) c.Hidden = false; }
                Update();
            }
        }
        public Visibility CharsVisibility => Chars.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NoCharsVisibility => Chars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // a hidden character can't be the main one
        public void HideChanged(CharVM c)
        {
            if (c.Hidden && c.IsMain) { main = null; c.IsMain = false; }
            Update();
        }

        public bool ShowAll { get => showChars == "all"; set { if (value) { showChars = "all"; Update(); } } }
        public bool ShowMain { get => showChars == "main"; set { if (value) { showChars = "main"; Update(); } } }
        public bool ShowNone { get => showChars == "none"; set { if (value) { showChars = "none"; Update(); } } }
        public bool VisEveryone { get => visibleTo == "everyone"; set { if (value) { visibleTo = "everyone"; Update(); } } }
        public bool VisOfficers { get => visibleTo == "officers"; set { if (value) { visibleTo = "officers"; Update(); } } }

        public string Hint => visibleTo == "officers" ? "Preview: this is what officers see. Everyone else only sees your avatar and name." : "Preview: this is what others see when they click your name.";
        public Visibility HintVisibility => Visibility.Visible;
        public Visibility GuestVisibility => IsGuest ? Visibility.Visible : Visibility.Collapsed;
        public Visibility UnsupportedVisibility => Supported ? Visibility.Collapsed : Visibility.Visible;

        // the profile:set message
        public Dictionary<string, object> ToMessage()
        {
            var o = new Dictionary<string, object>
            {
                ["avatarId"] = avatarId, ["accent"] = accent, ["about"] = about.Trim(), ["playTimes"] = playTimes.Trim(),
                ["showChars"] = showChars, ["visibleTo"] = visibleTo,
            };
            if (Chars.Count > 0)
            {
                o["main"] = main;
                o["hidden"] = Chars.Where(c => c.Hidden && !c.IsMain).Select(c => c.Name).ToList();
            }
            return o;
        }
    }
}
