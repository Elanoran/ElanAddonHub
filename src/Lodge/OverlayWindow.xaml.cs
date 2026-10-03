using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ElansAddonHub.Lodge
{
    // A see-through, click-through list of who's in voice, drawn on top of WoW (Windowed/Fullscreen mode).
    // Nothing is injected into the game: it's an ordinary topmost window the mouse passes through.
    public partial class OverlayWindow : Window
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        IntPtr hwnd;

        public OverlayWindow(IEnumerable members)
        {
            InitializeComponent();
            People.ItemsSource = members;
            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
            };
        }

        // the WoW window when it's in front (Wow.exe, WowClassic.exe, WowClassicB.exe ...), else IntPtr.Zero
        static uint lastPid;
        static bool lastWasWow;
        public static IntPtr WowInFront()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return IntPtr.Zero;
            GetWindowThreadProcessId(fg, out var pid);
            if (pid != lastPid)
            {
                lastPid = pid;
                try { lastWasWow = Process.GetProcessById((int)pid).ProcessName.StartsWith("Wow", StringComparison.OrdinalIgnoreCase); }
                catch { lastWasWow = false; }
            }
            return lastWasWow ? fg : IntPtr.Zero;
        }

        // place on the screen of `near` (WoW, or this hub), left or right edge, `top` = 0..1 of the screen height
        public void Place(IntPtr near, bool right, double top)
        {
            if (hwnd == IntPtr.Zero) return;
            var screen = near != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(near) : System.Windows.Forms.Screen.PrimaryScreen;
            var area = screen.Bounds;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            int w = (int)(ActualWidth * dpi.DpiScaleX), h = (int)(ActualHeight * dpi.DpiScaleY);
            int x = right ? area.Right - w - 12 : area.Left + 12;
            int y = area.Top + (int)((area.Height - h) * Math.Max(0, Math.Min(1, top)));
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }
}
