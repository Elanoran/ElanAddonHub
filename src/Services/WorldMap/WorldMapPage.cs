using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace ElansAddonHub.Services.WorldMap
{
    // The viewer's own files (the 2D page, the 3D page, their scripts and style, Leaflet 1.9.4 and three.js r149 with their licences): embedded in the exe, written next to the pictures under
    // <data>\atlas\worldmap\_page so the page is served from the same private virtual host. Rewritten when a file is missing or differs.
    public static class WorldMapPage
    {
        public const string Host = "worldmap.outpost.local";
        public const string PageUrl = "https://" + Host + "/" + WorldMapCache.PageFolder + "/index.html";
        public const string Page3dUrl = "https://" + Host + "/" + WorldMapCache.PageFolder + "/view3d.html";
        public static readonly string[] Files = { "index.html", "worldmap.js", "worldmap.css", "leaflet.js", "leaflet.css", "LICENSE-leaflet.txt", "view3d.html", "view3d.js", "three.min.js", "LICENSE-three.txt" };

        public static string Dir => Path.Combine(WorldMapCache.Root, WorldMapCache.PageFolder);

        public static byte[] Resource(string file)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("WorldMap." + file))
            {
                if (s == null) return null;
                var b = new byte[s.Length]; int n = 0;
                while (n < b.Length) { int r = s.Read(b, n, b.Length - n); if (r <= 0) break; n += r; }
                return b;
            }
        }

        // writes what is missing or different; throws when a resource is not in the exe (a broken build)
        public static string Ensure()
        {
            var dir = Dir;
            Directory.CreateDirectory(dir);
            foreach (var f in Files)
            {
                var data = Resource(f);
                if (data == null) throw new FileNotFoundException("the world map page is not in this build: " + f);
                var path = Path.Combine(dir, f);
                bool same = false;
                try { same = File.Exists(path) && new FileInfo(path).Length == data.Length && SameBytes(File.ReadAllBytes(path), data); } catch (Exception) { }
                if (same) continue;
                var tmp = path + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
                File.WriteAllBytes(tmp, data);
                try { if (File.Exists(path)) File.Delete(path); File.Move(tmp, path); }
                catch (Exception) { try { File.Delete(tmp); } catch (Exception) { } if (!File.Exists(path)) throw; }
            }
            return dir;
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
