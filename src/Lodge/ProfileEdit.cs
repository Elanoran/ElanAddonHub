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

    // the Settings > Profile page: everything the member can choose, with a live preview card that shows what others will see.
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
        public bool SupportsDisplayName { get; }
        public bool Dirty { get; private set; }     // the member changed something that is not saved yet
        public bool Saving { get; private set; }
        readonly bool init;
        readonly string login;

        string displayName = "", displayServerError, statusText = "";
        string avatarId, accent, about = "", playTimes = "", main, showChars = "all", visibleTo = "everyone";

        public ProfileEditVM(LodgeSession session)
        {
            this.session = session;
            var me = session.Me;
            var p = session.MyProfile ?? new ProfileData { Name = me?.Name };
            Supported = session.SupportsProfile;
            SupportsDisplayName = session.SupportsDisplayName;
            IsGuest = me != null && me.Guest;
            login = me?.Name ?? "";
            displayName = p.DisplayName ?? me?.DisplayName ?? "";
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
            init = true;
            Dirty = false;
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
            if (init) Dirty = true;
            if (init && !keepErr && !Saving && IsError) statusText = "";   // editing again clears an old error
            Preview.SetDisplayName(DisplayProblem == null ? DisplayNames.Clean(displayName) : "");
            foreach (var t in Avatars) { t.Selected = t.Id == avatarId; t.SetImage(t.Id == null ? null : AvatarCatalog.Image(t.Id, accent)); }
            foreach (var s in Swatches) s.Selected = s.Id == accent;
            Preview.SetAvatar(avatarId, accent);
            Card.Data = BuildPreview();
            Raise(nameof(AboutCounter)); Raise(nameof(AboutCounterBrush)); Raise(nameof(Hint)); Raise(nameof(HintVisibility)); Raise(nameof(MainChoice));
            Raise(nameof(ShowAll)); Raise(nameof(ShowMain)); Raise(nameof(ShowNone)); Raise(nameof(VisEveryone)); Raise(nameof(VisOfficers));
            Raise(nameof(CharsVisibility)); Raise(nameof(NoCharsVisibility));
            Raise(nameof(DisplayHint)); Raise(nameof(DisplayHintBrush)); RaiseStatus();
        }

        // what other people will see (the server applies the same rules)
        ProfileData BuildPreview()
        {
            var d = new ProfileData { Name = Preview.Name, DisplayName = Preview.DisplayName, Found = true, Role = session.MyRole, Online = true, AvatarId = avatarId, Accent = accent, About = about, PlayTimes = playTimes, Main = main };
            foreach (var c in Chars.OrderByDescending(x => x.Seen))
            {
                bool isMain = main != null && string.Equals(c.Name, main, StringComparison.OrdinalIgnoreCase);
                bool shown = (showChars == "all" && (!c.Hidden || isMain)) || (showChars == "main" && isMain);
                if (shown) d.Chars.Add(new CharVM { Name = c.Name, Class = c.Class, ClassFile = c.ClassFile, Race = c.Race, Level = c.Level, IsMain = isMain });
            }
            return d;
        }

        // ---- display name (server 2.7)
        public string DisplayName { get => displayName; set { value = value ?? ""; if (value == displayName) return; displayName = value; displayServerError = null; Update(); Raise(nameof(DisplayName)); } }
        public string DisplayProblem => SupportsDisplayName && !IsGuest ? DisplayNames.Problem(displayName) : null;
        public bool DisplayEnabled => !IsGuest;
        public Visibility DisplayVisibility => SupportsDisplayName ? Visibility.Visible : Visibility.Collapsed;
        public string Login => login;
        public string DisplayHint
        {
            get
            {
                if (IsGuest) return "Guests keep their guest name - a display name needs a personal invite code.";
                if (displayServerError != null) return displayServerError;
                var p = DisplayProblem;
                if (p != null) return p;
                var clean = DisplayNames.Clean(displayName);
                if (clean.Length == 0) return "Shown instead of your login name (@" + login + ") in the member list, chat and notifications. Leave it empty to use @" + login + ".";
                return "Others see \"" + clean + "\", with @" + login + " next to it on your card. You can change it once a minute.";
            }
        }
        public Brush DisplayHintBrush => !IsGuest && (displayServerError != null || DisplayProblem != null) ? Avatar.Res("Danger") : Avatar.Res("TextDim");
        public bool CanSave => Supported && !Saving && DisplayProblem == null && (Dirty || statusText == "Not saved." || IsError);
        public bool CanDiscard => Dirty && !Saving;
        public Visibility DiscardVisibility => Dirty && !Saving ? Visibility.Visible : Visibility.Collapsed;
        bool IsError => statusText != "" && !statusText.StartsWith("Saved") && !statusText.StartsWith("Saving");
        // inline status next to the buttons: errors, "Saving...", "Unsaved changes", or "Saved" which fades after a few seconds
        public string StatusText => Saving ? "Saving..." : IsError ? statusText : Dirty ? "Unsaved changes" : statusText;
        public Brush StatusBrush => Saving ? Avatar.Res("TextDim") : IsError ? Avatar.Res("Danger") : Dirty ? Avatar.Res("Gold") : statusText.StartsWith("Saved") ? Avatar.Res("Accent") : Avatar.Res("TextDim");

        void RaiseStatus() { Raise(nameof(CanSave)); Raise(nameof(CanDiscard)); Raise(nameof(DiscardVisibility)); Raise(nameof(StatusText)); Raise(nameof(StatusBrush)); }
        public void BeginSave() { Saving = true; displayServerError = null; statusText = "Saving..."; RaiseStatus(); Raise(nameof(DisplayHint)); Raise(nameof(DisplayHintBrush)); }
        public DateTime SavedAt = DateTime.MinValue;
        System.Windows.Threading.DispatcherTimer savedFade;
        public void Saved()
        {
            SavedAt = DateTime.UtcNow; Saving = false; displayServerError = null; statusText = "Saved ✓ everyone sees it now."; Update(); Dirty = false; RaiseStatus();
            savedFade?.Stop();
            savedFade = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            savedFade.Tick += (s, e) => { savedFade.Stop(); if (!Dirty && statusText.StartsWith("Saved")) { statusText = ""; RaiseStatus(); } };
            savedFade.Start();
        }
        // the server refused the save (taken name, rate limit, ...): nothing was saved
        public void ServerError(string text)
        {
            Saving = false;
            if ((text ?? "").IndexOf("display name", StringComparison.OrdinalIgnoreCase) >= 0) { displayServerError = text; statusText = "Not saved."; }
            else statusText = text ?? "Not saved.";
            keepErr = true; Update(); keepErr = false;
            Dirty = true;
            RaiseStatus();
        }

        bool keepErr;
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
            if (SupportsDisplayName && !IsGuest) o["displayName"] = DisplayNames.Clean(displayName);
            if (Chars.Count > 0)
            {
                o["main"] = main;
                o["hidden"] = Chars.Where(c => c.Hidden && !c.IsMain).Select(c => c.Name).ToList();
            }
            return o;
        }
    }
}
