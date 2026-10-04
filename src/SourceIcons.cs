using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // Small source icons for the third-party cards. Marks are only used to show where an addon comes from.
    public static class SourceIcons
    {
        // Octicons "mark-github" (MIT, github.com/primer/octicons), 16x16 viewbox.
        public static readonly Geometry GitHub = Freeze(Geometry.Parse(
            "M8 0c4.42 0 8 3.58 8 8a8.013 8.013 0 0 1-5.45 7.59c-.4.08-.55-.17-.55-.38 0-.27.01-1.13.01-2.2 0-.75-.25-1.23-.54-1.48 1.78-.2 3.65-.88 3.65-3.95 0-.88-.31-1.59-.82-2.15.08-.2.36-1.02-.08-2.12 0 0-.67-.22-2.2.82-.64-.18-1.32-.27-2-.27-.68 0-1.36.09-2 .27-1.53-1.03-2.2-.82-2.2-.82-.44 1.1-.16 1.92-.08 2.12-.51.56-.82 1.28-.82 2.15 0 3.06 1.86 3.75 3.64 3.95-.23.2-.44.55-.51 1.07-.46.21-1.61.55-2.33-.66-.15-.24-.6-.83-1.23-.82-.67.01-.27.38.01.53.34.19.73.9.82 1.13.16.45.68 1.31 2.69.94 0 .67.01 1.3.01 1.49 0 .21-.15.45-.55.38A7.995 7.995 0 0 1 0 8c0-4.42 3.58-8 8-8Z"));

        // Own drawing: "installed by hand" = an arrow dropping into an open tray.
        public static readonly Geometry Manual = Freeze(Geometry.Parse("M6.8 1H9.2V6.2H12L8 10.4 4 6.2H6.8Z M1.5 9.8H3.6V12.9H12.4V9.8H14.5V15H1.5Z"));

        static Geometry Freeze(Geometry g) { g.Freeze(); return g; }

        static ImageSource cf; static bool cfTried;

        // The icon of the installed CurseForge app (read from its exe at runtime, cached); null if not installed.
        public static ImageSource CurseForge()
        {
            if (cfTried) return cf;
            cfTried = true;
            try
            {
                var exe = CurseForgeLocal.ExePath;
                if (!File.Exists(exe)) return null;
                using (var ico = Icon.ExtractAssociatedIcon(exe))
                {
                    if (ico == null) return null;
                    var bs = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    cf = bs;
                }
            }
            catch { cf = null; }
            return cf;
        }
    }
}
