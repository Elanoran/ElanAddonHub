using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ElansAddonHub.Lodge
{
    // The toast stack: the same kind of window as the voice overlay (topmost, tool window, click-through, never activated -
    // it can't take keyboard focus or mouse clicks from the game) placed in a screen corner.
    public partial class ToastWindow : Window
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        IntPtr hwnd;
        public int ExStyleForTest => hwnd == IntPtr.Zero ? 0 : GetWindowLong(hwnd, GWL_EXSTYLE);
        public static bool IsClickThrough(int ex) => (ex & WS_EX_TRANSPARENT) != 0 && (ex & WS_EX_NOACTIVATE) != 0;

        public ToastWindow(IEnumerable cards)
        {
            InitializeComponent();
            Cards.ItemsSource = cards;
            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
            };
        }

        // corner: tl | tr | bl | br
        public void Place(IntPtr near, string corner)
        {
            if (hwnd == IntPtr.Zero) return;
            var screen = near != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(near) : System.Windows.Forms.Screen.PrimaryScreen;
            var area = screen.Bounds;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            int w = (int)(ActualWidth * dpi.DpiScaleX), h = (int)(ActualHeight * dpi.DpiScaleY);
            bool right = corner == "tr" || corner == "br", bottom = corner == "bl" || corner == "br";
            int x = right ? area.Right - w - 14 : area.Left + 14;
            int y = bottom ? area.Bottom - h - 48 : area.Top + 40;
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }
}
