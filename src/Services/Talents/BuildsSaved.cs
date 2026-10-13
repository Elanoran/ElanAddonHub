using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services.Talents
{
    // The builds a person saved in the calculator: name, notes, class and the build code, in the Outpost's own data folder (talentbuilds.json).
    // Local only. Nothing here goes to the WoW folder; a build is shared by copying its code (a Lodge hook for pasted codes: BuildCode.Find).
    public sealed class SavedBuild
    {
        public string Name = "", Notes = "", Code = "", ClassFile = "";
        public long At;
    }

    public sealed class SavedBuilds
    {
        public const int MaxBuilds = 200, MaxName = 40, MaxNotes = 600;
        public static string File = Path.Combine(Util.DataDir, "talentbuilds.json");

        readonly object gate = new object();
        public List<SavedBuild> Items = new List<SavedBuild>();

        public static string CleanName(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s ?? "") if (c >= ' ' && c != '\u007f') sb.Append(c);
            var t = sb.ToString().Trim();
            return t.Length > MaxName ? t.Substring(0, MaxName) : t;
        }

        static string CleanNotes(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s ?? "") if (c >= ' ' || c == '\n') sb.Append(c);
            var t = sb.ToString().Trim();
            return t.Length > MaxNotes ? t.Substring(0, MaxNotes) : t;
        }

        // same name + class replaces the older one
        public SavedBuild Save(string name, string notes, string code, string classFile, DateTime? now = null)
        {
            name = CleanName(name);
            if (name.Length == 0 || string.IsNullOrEmpty(code)) return null;
            lock (gate)
            {
                Items.RemoveAll(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase) && string.Equals(b.ClassFile, classFile, StringComparison.OrdinalIgnoreCase));
                var b2 = new SavedBuild { Name = name, Notes = CleanNotes(notes), Code = code, ClassFile = (classFile ?? "").ToUpperInvariant(), At = new DateTimeOffset(now ?? DateTime.UtcNow).ToUnixTimeSeconds() };
                Items.Insert(0, b2);
                if (Items.Count > MaxBuilds) Items.RemoveRange(MaxBuilds, Items.Count - MaxBuilds);
                return b2;
            }
        }

        public bool Delete(SavedBuild b) { lock (gate) return Items.Remove(b); }

        public List<SavedBuild> For(string classFile)
        {
            lock (gate) return Items.Where(b => string.Equals(b.ClassFile, classFile, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public bool TrySave(string path = null)
        {
            try
            {
                path = path ?? File;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<object> arr;
                lock (gate) arr = Items.Select(b => (object)new Dictionary<string, object> { ["name"] = b.Name, ["notes"] = b.Notes, ["code"] = b.Code, ["class"] = b.ClassFile, ["at"] = b.At }).ToList();
                var json = Json.Write(new Dictionary<string, object> { ["v"] = 1, ["builds"] = arr });
                var tmp = path + ".tmp";
                System.IO.File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { Util.Log("talents: cannot save builds: " + e.Message); return false; }
        }

        public static SavedBuilds Load(string path = null)
        {
            var r = new SavedBuilds();
            try
            {
                path = path ?? File;
                if (!System.IO.File.Exists(path)) return r;
                var o = Json.Obj(System.IO.File.ReadAllText(path, Encoding.UTF8));
                var list = o?.List("builds");
                if (list == null) return r;
                foreach (var x in list)
                {
                    if (!(x is Dictionary<string, object> d)) continue;
                    var name = CleanName(d.Str("name"));
                    var code = d.Str("code");
                    if (name.Length == 0 || string.IsNullOrEmpty(code) || code.Length > 4000) continue;
                    r.Items.Add(new SavedBuild { Name = name, Notes = CleanNotes(d.Str("notes")), Code = code, ClassFile = (d.Str("class") ?? "").ToUpperInvariant(), At = d.Long("at") });
                    if (r.Items.Count >= MaxBuilds) break;
                }
            }
            catch (Exception) { }
            return r;
        }
    }
}
