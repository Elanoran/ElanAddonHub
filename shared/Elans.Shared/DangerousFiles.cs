using System;
using System.Collections.Generic;

namespace Elans.Shared
{
    // One list of the file types that can run something on Windows when they are opened (programs, scripts, shortcuts, installers, disk
    // images that mount and auto-run, help files, Office add-ins, shell search / library / settings links). The Outpost uses it both to warn
    // before a chat file is downloaded or a shared file is saved, and to refuse an upload; the Lodge server refuses the same types.
    // Extensions are lowercase, without the dot. Only ever add to the list: a type taken off it would suddenly be accepted again.
    // Tests: Elans.Shared.Tests/DangerousFilesTests.cs; on the Outpost --selftest lodgefiles and --selftest lodgehardening.
    public static class DangerousFiles
    {
        public static readonly IReadOnlyCollection<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // programs, libraries, drivers
            "exe", "dll", "com", "scr", "cpl", "sys", "drv", "pif", "msi", "msp",
            // scripts and script hosts
            "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "ws", "wsc", "hta", "jar", "reg", "inf",
            // shortcuts and shell links
            "lnk", "url", "scf", "library-ms", "search-ms", "settingcontent-ms",
            // app installers / ClickOnce
            "application", "appref-ms", "msix", "msixbundle", "appx", "appxbundle", "appinstaller",
            // disk images (mount with a double-click), compiled help, Excel add-ins
            "iso", "img", "vhd", "vhdx", "chm", "xll",
        };

        static readonly HashSet<string> set = (HashSet<string>)Extensions;

        // the last extension, trailing dots and spaces ignored ("setup.exe. " is setup.exe on Windows); "" when there is none
        public static string Extension(string name)
        {
            name = (name ?? "").Trim(' ', '.', '\t');
            var dot = name.LastIndexOf('.');
            var slash = name.LastIndexOfAny(new[] { '/', '\\' });
            return dot > slash && dot >= 0 ? name.Substring(dot + 1).Trim().ToLowerInvariant() : "";
        }

        public static bool IsDangerous(string name) => set.Contains(Extension(name));
    }
}
