using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ElansAddonHub
{
    public enum PillKind { Quiet, Action, Danger, Busy }

    // The status pill on a card IS the card's only action control: "Up to date" (quiet, not clickable), "Update" / "Install" (accent, clickable),
    // "Retry" (after a failed attempt), a spinner while working. Shared by our own cards and the third-party cards.
    public abstract class PillBase : INotifyPropertyChanged
    {
        string pillText, pillTip;
        PillKind pillKind;
        bool pillClickable, failed;

        protected void SetPill(string text, PillKind kind, string tip, bool clickable)
        {
            pillText = text; pillKind = kind; pillTip = tip; pillClickable = clickable;
        }

        public void SetPillForTest(string text, PillKind kind, string tip, bool clickable) { SetPill(text, kind, tip, clickable); Notify(string.Empty); }
        public bool Failed
        {
            get => failed;
            protected set { if (failed == value) return; failed = value; Notify(string.Empty); }
        }

        bool Retry => failed && pillClickable;
        PillKind Kind => Retry ? PillKind.Danger : pillKind;

        public string PillText => Retry ? "Retry" : pillText;
        public string RetryTip { get; set; }
        public string PillTip => Retry ? (RetryTip ?? "The last attempt failed. Click to try again.") : pillTip;
        public bool PillClickable => pillClickable;
        public Cursor PillCursor => pillClickable ? Cursors.Hand : Cursors.Arrow;
        public Visibility PillVisibility => string.IsNullOrEmpty(pillText) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility PillSpinVisibility => pillKind == PillKind.Busy ? Visibility.Visible : Visibility.Collapsed;

        static Brush Res(string key) => (Brush)Application.Current.Resources[key];
        static Brush Tint(string key, byte a) { var c = ((SolidColorBrush)Res(key)).Color; var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b; }
        static Brush bgQuiet, bgDanger, bgBusy, fgDark;
        public Brush PillBg
        {
            get
            {
                switch (Kind)
                {
                    case PillKind.Action: return Res("Accent");
                    case PillKind.Danger: return bgDanger ?? (bgDanger = Tint("Danger", 0x33));
                    case PillKind.Busy: return bgBusy ?? (bgBusy = Tint("Accent", 0x26));
                    default: return bgQuiet ?? (bgQuiet = Tint("Text", 0x14));
                }
            }
        }
        public Brush PillFg
        {
            get
            {
                switch (Kind)
                {
                    case PillKind.Action: return fgDark ?? (fgDark = new SolidColorBrush(Color.FromRgb(0x11, 0x13, 0x15)));
                    case PillKind.Danger: return Res("Danger");
                    case PillKind.Busy: return Res("Accent");
                    default: return Res("TextDim");
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void Notify([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
