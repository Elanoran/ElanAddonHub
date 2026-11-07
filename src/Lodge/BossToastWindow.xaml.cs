using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub.Lodge
{
    // The Camp Boss over the game: the same kind of window as the toast cards (topmost, tool window, click-through, never activated),
    // placed where she stands in game (BossNotes.Spot) and moved frame by frame from BossPose while she is visible.
    public partial class BossToastWindow : Window
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOSIZE = 0x1;
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        IntPtr hwnd;
        public int ExStyleForTest => hwnd == IntPtr.Zero ? 0 : GetWindowLong(hwnd, GWL_EXSTYLE);
        ToastVM vm;
        int face = -1;
        public Func<DateTime> Clock = () => DateTime.UtcNow;
        public BossPose.Pose LastPoseForTest { get; private set; }

        public BossToastWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
            };
            IsVisibleChanged += (s, e) =>
            {
                CompositionTarget.Rendering -= OnFrame;
                if (IsVisible) CompositionTarget.Rendering += OnFrame;
            };
        }

        // ---- her faces: 4 x 4 frames of 128 px (Assets/Boss, made from the Toolkit's sheets)
        static readonly string[] FaceFiles = { "boss_face_hairpin.png", "boss_face_cook.png" };
        static readonly Dictionary<int, BitmapSource[]> sheets = new Dictionary<int, BitmapSource[]>();
        public static BitmapSource[] Frames(int faceIndex)
        {
            if (faceIndex < 0 || faceIndex >= FaceFiles.Length) faceIndex = 0;
            if (sheets.TryGetValue(faceIndex, out var f)) return f;
            var sheet = new BitmapImage();
            sheet.BeginInit();
            sheet.UriSource = new Uri("pack://application:,,,/Assets/Boss/" + FaceFiles[faceIndex]);
            sheet.CacheOption = BitmapCacheOption.OnLoad;
            sheet.EndInit();
            sheet.Freeze();
            int cell = sheet.PixelWidth / 4;
            f = new BitmapSource[16];
            for (int i = 0; i < 16; i++)
            {
                var c = new CroppedBitmap(sheet, new Int32Rect(i % 4 * cell, i / 4 * cell, cell, cell));
                c.Freeze();
                f[i] = c;
            }
            sheets[faceIndex] = f;
            return f;
        }

        // what she says now (null = nothing: the caller hides the window)
        public void Say(ToastVM v, int faceIndex)
        {
            if (faceIndex != face) { face = faceIndex; vm = null; }
            if (ReferenceEquals(v, vm)) return;
            vm = v;
            if (v == null) return;
            TitleText.Text = v.Title ?? "";
            Kicker.Text = v.Kicker ?? "";
            FullText.Text = v.Preview ?? "";
            Typed.Text = "";
            Apply(0);
        }

        void OnFrame(object sender, EventArgs e) { if (vm != null) Apply((Clock() - vm.Born).TotalSeconds); }

        void Apply(double t)
        {
            var text = vm?.Preview ?? "";
            var p = BossPose.At(Math.Max(0, t), text.Length, vm != null && vm.Turn);
            LastPoseForTest = p;
            var frames = Frames(face);
            Face.Source = frames[p.From];
            if (p.To != p.From && p.Blend > 0) { FaceNext.Source = frames[p.To]; FaceNext.Opacity = p.Blend; }
            else FaceNext.Opacity = 0;
            if (Typed.Text.Length != p.Chars) Typed.Text = text.Substring(0, Math.Min(text.Length, p.Chars));
            Bar.Width = Math.Max(1, 328 * p.Left);
            double h = Box.ActualHeight > 0 ? Box.ActualHeight : 76;
            Drop.Y = -p.Down * (h + 8);
            Box.Opacity = 1 - p.Down * 0.6;
        }
        public void ApplyForTest(double t) => Apply(t);
        public string TypedForTest => Typed.Text;

        // on the game window `near` (its client area), or the screen of `near` when it is not the game; fractions from BossNotes.Spot
        public void Place(IntPtr near, bool isGame, Services.Settings s)
        {
            if (hwnd == IntPtr.Zero) return;
            int left, top, cw, ch;
            if (isGame && near != IntPtr.Zero && GetClientRect(near, out var rc))
            {
                var pt = new POINT();
                ClientToScreen(near, ref pt);
                left = pt.X; top = pt.Y; cw = rc.Right - rc.Left; ch = rc.Bottom - rc.Top;
            }
            else
            {
                var b = (near != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(near) : System.Windows.Forms.Screen.PrimaryScreen).Bounds;
                left = b.Left; top = b.Top; cw = b.Width; ch = b.Height;
            }
            if (cw < 64 || ch < 64) return;
            var (fx, ftop, fw) = BossNotes.Spot(s, cw / (double)ch);
            var dpi = VisualTreeHelper.GetDpi(this);
            double widthPx = fw * cw;
            double dip = widthPx / dpi.DpiScaleX;
            if (Math.Abs(Width - dip) > 0.5) { Width = dip; UpdateLayout(); }
            int x = left + (int)Math.Round(fx * cw - widthPx / 2), y = top + (int)Math.Round(ftop * ch);
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }
}
