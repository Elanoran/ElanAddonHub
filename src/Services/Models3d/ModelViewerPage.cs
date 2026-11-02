using System;
using System.IO;
using System.Reflection;

namespace ElansAddonHub.Services.Models3d
{
    // 3.6: Atlas 3D models, step 3 - the model viewer's own files: viewer.html and viewer.js (embedded as ModelViewer.*) and the world map's three.js r149
    // with its licence (the same embedded WorldMap.* resources: one copy of the library in the exe). Written next to the built models under
    // <data>\atlas\models\_page, so the page and the models come from one private virtual host (models.outpost.local, ModelView). Rewritten when a file is
    // missing or differs. CreatureModels.RemoveOtherBuilds leaves _page alone.
    public static class ModelViewerPage
    {
        public const string Host = "models.outpost.local";
        public const string Folder = "_page";
        public const string PageUrl = "https://" + Host + "/" + Folder + "/viewer.html";
        // published file -> embedded resource
        public static readonly (string File, string Resource)[] Files =
        {
            ("viewer.html", "ModelViewer.viewer.html"), ("viewer.js", "ModelViewer.viewer.js"),
            ("three.min.js", "WorldMap.three.min.js"), ("LICENSE-three.txt", "WorldMap.LICENSE-three.txt"),
        };

        public static string Dir => Path.Combine(CreatureModels.Root, Folder);

        public static byte[] Resource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
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
            foreach (var (f, res) in Files)
            {
                var data = Resource(res);
                if (data == null) throw new FileNotFoundException("the model viewer page is not in this build: " + f);
                var path = Path.Combine(dir, f);
                bool same = false;
                try { same = File.Exists(path) && new FileInfo(path).Length == data.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(data); } catch (Exception) { }
                if (same) continue;
                var tmp = path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(tmp, data);
                try { File.Move(tmp, path, true); }
                catch (Exception) { try { File.Delete(tmp); } catch (Exception) { } if (!File.Exists(path)) throw; }
            }
            return dir;
        }
    }
}
