using System.Reflection;
using System.Text.RegularExpressions;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The Lodge's message types: one list (LodgeMsg), described in server/PROTOCOL.md, and no raw type string left in the Lodge server or the
// Outpost's Lodge code - so a new type has to go through the list (and the documentation) first.
public class LodgeMsgTests
{
    static readonly Regex TypeShape = new(@"^[a-z0-9._:-]+$");

    static List<(string Name, string Value)> All() => typeof(LodgeMsg)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (f.Name, (string)f.GetRawConstantValue()))
        .ToList();

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "shared")) && Directory.Exists(Path.Combine(d.FullName, "server"))) return d.FullName;
        throw new DirectoryNotFoundException("no folder above " + AppContext.BaseDirectory + " holds both shared/ and server/");
    }

    // every file that speaks the Lodge protocol: the server, the Outpost's Lodge folder, and the few Outpost services that build Lodge frames
    static readonly string[] Folders = { "server/Lodge.Server", "src/Lodge" };
    static readonly string[] Files = { "src/Services/LodgeClient.cs", "src/Services/Atlas/AtlasObs.cs", "src/Services/FlightShare.cs", "src/Services/WorldMap/LearnedFlights.Lodge.cs" };

    static List<string> Sources(string root)
    {
        static bool Built(string p) => p.Split('/', '\\').Any(s => s is "obj" or "bin");
        var list = Folders.SelectMany(f => Directory.EnumerateFiles(Path.Combine(root, f), "*.cs", SearchOption.AllDirectories)).Where(p => !Built(Path.GetRelativePath(root, p))).ToList();
        list.AddRange(Files.Select(f => Path.Combine(root, f)));
        return list;
    }

    [Fact]
    public void Values_are_unique_non_empty_and_well_formed()
    {
        var all = All();
        Assert.True(all.Count >= 100, "only " + all.Count + " message types found by reflection");
        foreach (var (name, value) in all)
        {
            Assert.False(string.IsNullOrEmpty(value), name + " is empty");
            Assert.True(TypeShape.IsMatch(value), name + " = \"" + value + "\" has characters outside a-z 0-9 . _ : -");
        }
        var dup = all.GroupBy(x => x.Value).Where(g => g.Count() > 1).Select(g => g.Key + " (" + string.Join(", ", g.Select(x => x.Name)) + ")").ToList();
        Assert.True(dup.Count == 0, "the same type twice: " + string.Join("; ", dup));
    }

    [Fact]
    public void Every_type_is_described_in_PROTOCOL_md()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "server", "PROTOCOL.md"));
        var missing = All().Where(x => !doc.Contains("`" + x.Value + "`", StringComparison.Ordinal)).Select(x => x.Value).ToList();
        Assert.True(missing.Count == 0, "not in server/PROTOCOL.md as a `backticked` name: " + string.Join(", ", missing));
    }

    // ["t"] = "...", o["t"] = "..." - a type set as a literal (whatever it is: an unknown one must be added to LodgeMsg first)
    static readonly Regex SetT = new(@"\[""t""\]\s*=\s*""([^""]*)""");
    // "{\"t\":\"...\"}" written by hand
    static readonly Regex RawT = new(@"\\""t\\"":\\""([^\\""]*)\\""");
    // m.Str("t") != "..." - a received type compared with a literal
    static readonly Regex CompareT = new(@"Str\(""t""\)\s*[!=]=\s*""([^""]*)""");
    // case "...": / type == "..." with a literal that is a message type. Another switch or variable may use the same word (a joining state
    // "error", a files action "delete", a channel type "voice"): such a line says so with the marker not-a-lodge-type.
    static readonly Regex CaseLiteral = new(@"\bcase\s+""([^""]*)""\s*:|\btype\s*[!=]=\s*""([^""]*)""");
    const string Marker = "not-a-lodge-type";

    [Fact]
    public void No_raw_type_strings_in_the_Lodge_code()
    {
        var root = RepoRoot();
        var values = All().ToDictionary(x => x.Value, x => x.Name);
        var problems = new List<string>();
        int files = 0, uses = 0;
        foreach (var path in Sources(root))
        {
            Assert.True(File.Exists(path), path + " is gone - update LodgeMsgTests' file list");
            files++;
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                uses += Regex.Matches(line, @"\bLodgeMsg\.\w+").Count;
                string Where() => rel + ":" + (i + 1);
                string Fix(string v) => values.TryGetValue(v, out var n) ? "use LodgeMsg." + n : "not in LodgeMsg - add it there (and to server/PROTOCOL.md)";
                foreach (Match m in SetT.Matches(line)) problems.Add(Where() + " [\"t\"] = \"" + m.Groups[1].Value + "\": " + Fix(m.Groups[1].Value));
                foreach (Match m in RawT.Matches(line)) problems.Add(Where() + " raw \"t\":\"" + m.Groups[1].Value + "\": " + Fix(m.Groups[1].Value));
                foreach (Match m in CompareT.Matches(line)) problems.Add(Where() + " compares the type with \"" + m.Groups[1].Value + "\": " + Fix(m.Groups[1].Value));
                if (!line.Contains(Marker, StringComparison.Ordinal))
                    foreach (Match m in CaseLiteral.Matches(line))
                    {
                        var v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                        if (values.TryGetValue(v, out var n))
                            problems.Add(Where() + " " + m.Value + " : use LodgeMsg." + n + " (or mark the line " + Marker + " if it isn't about a message type)");
                    }
            }
        }
        Assert.True(files > 50, "only " + files + " Lodge source files found under " + root);
        Assert.True(uses > 200, "only " + uses + " LodgeMsg uses found - is the scan looking at the right files?");
        Assert.True(problems.Count == 0, problems.Count + " raw Lodge message types:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Every_type_is_used_by_the_server()
    {
        // a type in the list that the Lodge server neither handles nor sends is dead (the Outpost may still lag behind, which is fine)
        var root = RepoRoot();
        var server = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "server", "Lodge.Server"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(root, p).Split('/', '\\').Any(s => s is "obj" or "bin")).Select(File.ReadAllText));
        var unused = All().Where(x => !Regex.IsMatch(server, @"\bLodgeMsg\." + x.Name + @"\b")).Select(x => x.Name).ToList();
        Assert.True(unused.Count == 0, "in LodgeMsg but not used by the Lodge server: " + string.Join(", ", unused));
    }
}
