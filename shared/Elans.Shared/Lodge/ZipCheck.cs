using System;
using System.IO;
using System.IO.Compression;

namespace Elans.Shared
{
    // The zip listing both sides use for shared files and chat uploads (server: FileRules.CheckZip, Outpost: LodgeFiles.CheckZip).
    // Lists entries only; nothing is written to disk. A zip inside the zip is opened in memory and listed the same way (Explorer opens
    // nested zips like folders, so a program one level down is as easy to start as one at the top). A zip inside that one is refused:
    // there is no reason to share three levels deep. A nested zip larger than MaxNestedBytes unpacked can't be checked and is refused.
    // Tests: server/Lodge.Tests/FilesTests.cs (nested zips) and the Outpost's --selftest lodgefiles.
    public static class ZipCheck
    {
        public const int MaxEntries = 20000;
        public const long MaxNestedBytes = 64L * 1024 * 1024;

        // null = fine, otherwise why the zip is refused (plain words, no file names from inside the zip)
        public static string Check(Stream seekable) => Check(seekable, 0);

        static string Check(Stream seekable, int depth)
        {
            try
            {
                using var zip = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: true);
                if (zip.Entries.Count > MaxEntries) return "The zip has too many entries";
                foreach (var e in zip.Entries)
                {
                    if (UnsafeEntryPath(e.FullName)) return "The zip contains an unsafe path";
                    if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue; // folder entry
                    if (DangerousFiles.IsDangerous(e.Name)) return "The zip contains a program or script file (." + DangerousFiles.Extension(e.Name) + ")";
                    if (DangerousFiles.Extension(e.Name) != "zip") continue;
                    if (depth >= 1) return "The zip contains zips inside zips";
                    var why = CheckNested(e, depth + 1);
                    if (why != null) return why;
                }
                return null;
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException)
            {
                return depth == 0 ? "That is not a valid zip file" : "The zip contains a zip that is not valid";
            }
        }

        static string CheckNested(ZipArchiveEntry e, int depth)
        {
            if (e.Length > MaxNestedBytes) return "The zip contains a zip too large to check";
            using var ms = new MemoryStream();
            using (var s = e.Open())
            {
                // the header's size can lie: copy at most one byte past the limit
                var buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > MaxNestedBytes) return "The zip contains a zip too large to check";
                }
            }
            ms.Position = 0;
            return Check(ms, depth);
        }

        // an entry path that would escape the folder it is unpacked into (or write to an alternate stream / another drive)
        public static bool UnsafeEntryPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (path.Contains('\0') || path.Contains(':')) return true;
            var p = path.Replace('\\', '/');
            if (p.StartsWith('/')) return true;
            foreach (var seg in p.Split('/'))
                if (seg == "..") return true;
            return false;
        }
    }
}
