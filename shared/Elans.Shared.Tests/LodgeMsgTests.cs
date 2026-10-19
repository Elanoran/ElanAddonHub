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
                // a type by name, or through its typed class (JoinMsg.Type, EditedMsg.Read, new EditMsg)
                uses += Regex.Matches(line, @"\bLodgeMsg\.\w+|\b(?:\w+Msg|ChatMessage)\.(?:Type|Read)\b|\bnew \w+Msg\b").Count;
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
        Assert.True(uses > 200, "only " + uses + " LodgeMsg / typed-class uses found - is the scan looking at the right files?");
        Assert.True(problems.Count == 0, problems.Count + " raw Lodge message types:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Every_type_is_used_by_the_server()
    {
        // a type in the list that the Lodge server neither handles nor sends is dead (the Outpost may still lag behind, which is fine)
        var root = RepoRoot();
        var server = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "server", "Lodge.Server"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(root, p).Split('/', '\\').Any(s => s is "obj" or "bin")).Select(File.ReadAllText));
        // a typed message class (shared/Elans.Shared/Lodge: "class X { public const string Type = LodgeMsg.Y;") counts as a use of Y when the server uses X
        var typed = Directory.EnumerateFiles(Path.Combine(root, "shared", "Elans.Shared", "Lodge"), "*.cs")
            .SelectMany(p => Regex.Matches(File.ReadAllText(p), @"class\s+(\w+)\s*(?::[^{]*)?\{\s*public const string Type = LodgeMsg\.(\w+);").Cast<Match>())
            .Where(m => Regex.IsMatch(server, @"\b" + m.Groups[1].Value + @"\b")).Select(m => m.Groups[2].Value).ToHashSet();
        var unused = All().Where(x => !typed.Contains(x.Name) && !Regex.IsMatch(server, @"\bLodgeMsg\." + x.Name + @"\b")).Select(x => x.Name).ToList();
        Assert.True(unused.Count == 0, "in LodgeMsg but not used by the Lodge server: " + string.Join(", ", unused));
    }

    // ================================================================ the rule: a message can only be added the typed way
    // (1) every LodgeMsg value has its typed class in Elans.Shared - one per direction (the index table in server/PROTOCOL.md says which);
    // (2) neither side builds or reads a Lodge frame by field name outside the adapters and the typed classes (the source scan below).

    // the typed message classes: a public const string Type (a LodgeMsg value) declared on the class itself (LodgeIdMsg / LodgeEmptyMsg /
    // LodgeDeviceListMsg subclasses declare it too, and their base writes MsgType)
    static List<(Type Class, string Value)> TypedClasses() => typeof(LodgeMsg).Assembly.GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic)
        .Select(t => (t, f: t.GetField("Type", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)))
        .Where(x => x.f != null && x.f.IsLiteral && x.f.FieldType == typeof(string))
        .Select(x => (x.t, (string)x.f.GetRawConstantValue()))
        .ToList();

    // the index table of server/PROTOCOL.md: | feature | client -> server | server -> client | both |
    static Dictionary<string, string> Directions()
    {
        var doc = File.ReadAllLines(Path.Combine(RepoRoot(), "server", "PROTOCOL.md"));
        int at = Array.FindIndex(doc, l => l.StartsWith("## Every message type (index)", StringComparison.Ordinal));
        Assert.True(at >= 0, "server/PROTOCOL.md has no \"## Every message type (index)\" section");
        var dir = new Dictionary<string, string>();
        foreach (var row in doc.Skip(at).SkipWhile(l => !l.StartsWith("| Feature", StringComparison.Ordinal)).Skip(2).TakeWhile(l => l.StartsWith("|", StringComparison.Ordinal)))
        {
            var cells = row.Split('|');
            string[] names = { "client -> server", "server -> client", "both" };
            for (int c = 0; c < 3; c++)
                foreach (Match m in Regex.Matches(cells[c + 2], "`([^`]+)`")) dir[m.Groups[1].Value] = names[c];
        }
        return dir;
    }

    [Fact]
    public void Every_type_has_its_typed_class_one_per_direction()
    {
        var classes = TypedClasses();
        var values = All().Select(x => x.Value).ToHashSet();
        var dir = Directions();
        var problems = new List<string>();
        foreach (var (cls, v) in classes)
            if (!values.Contains(v)) problems.Add(cls.Name + ".Type = \"" + v + "\" is not a LodgeMsg value");
        foreach (var (name, v) in All())
        {
            var mine = classes.Where(c => c.Value == v).Select(c => c.Class.Name).OrderBy(n => n).ToList();
            if (!dir.TryGetValue(v, out var d)) { problems.Add(v + ": not in the index table of server/PROTOCOL.md"); continue; }
            int want = d == "both" ? 2 : 1;
            if (mine.Count != want)
                problems.Add(v + " (" + d + "): " + (mine.Count == 0 ? "no typed class - write one in shared/Elans.Shared/Lodge (see \"How to add a message\" in server/PROTOCOL.md)"
                    : mine.Count + " classes (" + string.Join(", ", mine) + "), want " + want + (want == 2 ? " - one each way" : "")));
        }
        Assert.True(problems.Count == 0, problems.Count + " problems:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Every_typed_class_writes_its_own_type()
    {
        // a class that can be written as it is (a new instance) writes "t" = its Type first; the generic bases write MsgType, which must be Type
        var problems = new List<string>();
        int written = 0;
        foreach (var (cls, v) in TypedClasses())
        {
            var write = cls.GetMethod("Write", new[] { typeof(ILodgeWriter) });
            if (write == null || cls.GetConstructor(Type.EmptyTypes) == null) { problems.Add(cls.Name + " has no Write(ILodgeWriter) or no new()"); continue; }
            var w = new TestFields();
            try { write.Invoke(Activator.CreateInstance(cls), new object[] { w }); }
            catch (TargetInvocationException e) when (e.InnerException is NullReferenceException) { continue; }   // needs its parts set first (e.g. JoinMsg.User)
            written++;
            if (w.Str("t") != v) problems.Add(cls.Name + " writes t = \"" + w.Str("t") + "\", its Type is \"" + v + "\"");
        }
        Assert.True(written > 100, "only " + written + " typed classes could be written as new instances");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // ---------------------------------------------------------------- the source scan
    // Both sides keep their frames in their own JSON model and touch them only through the typed classes. In the Lodge code (the same files
    // as above) it fails on:
    //   F  a field of the received frame read or written by name: on both sides the frame is called m (JsonObject m on the server, the
    //      Dictionary<string, object> m in the Outpost) - m["x"], m?["x"], J.Str(m, "x"), m.Str("x"), m.Child("x"), m.ContainsKey("x") ...
    //      Reading "t" to route the frame is the one exception (the dispatch);
    //   N  the frame handed to a method under another name (Foo(m) into Foo(JsonObject req)) - it has to stay m, so F sees what is done to it;
    //   A  a field read or written through an adapter (new LodgeJsonFields(m).Str("x"), f.Child("x") on a LodgeFieldsDict) - that is the
    //      typed classes' job, the adapters only carry them;
    //   T  a frame's type set by hand (["t"] = LodgeMsg.X, Set("t", ...)) - a new frame is built by its typed class;
    //   B  a frame built by a typed class and then changed by field name (var o = LodgeJson.Build(..); o["x"] = ..).
    // Not scanned: the adapters themselves (shared/Elans.Shared/Lodge/LodgeJson.cs, LodgeFieldsDict.cs), stored records and free-form children (an Atlas
    // section, a character sheet, a stored chat message) - those are not frames, and the typed classes carry them raw on purpose.
    static readonly string[] Adapters = { "src/Lodge/LodgeFieldsDict.cs" };      // the shared one (LodgeJsonFields) is in shared/, which isn't scanned

    // Where a strict type check of the raw JSON IS the validation, the typed class only carries the envelope and these read the frame raw on
    // purpose (file, method, the keys it may read - or "(frame)" for a method that receives the frame under its own name, rule N - and why).
    // Exactly these: anything else fails.
    static readonly (string File, string Method, string Keys, string Why)[] RawOnPurpose =
    {
        ("server/Lodge.Server/Admin.cs", "Purge", "atlasOnly", "admin.purge: only a JSON false turns atlasOnly off (missing / wrong type = true)"),
        ("server/Lodge.Server/ArmoryModule.cs", "Crafters", "spells,spell", "armory.crafters: the spell ids, strict whole JSON numbers one by one"),
        ("server/Lodge.Server/ArmoryModule.cs", "Standing", "faction", "armory.standing: the faction id, a whole JSON number in range"),
        ("server/Lodge.Server/AtlasModule.cs", "Get", "since", "atlas.get: since is a whole JSON number only"),
        ("server/Lodge.Server/AtlasStore.cs", "HasContent", Dynamic, "atlas.share: a known section that is non-empty, by its JSON kind"),
        ("server/Lodge.Server/AtlasStore.cs", "Share", "vendors,quests,spawns,items,seenNpcs,seenObjects,seenQuests,finds,nodes,fishspots,fieldnotes,entrances,npcspots",
            "atlas.share: every raw section entry is validated as it is merged"),
        ("server/Lodge.Server/BadgesModule.cs", "Switch", "on", "admin.badges: a missing or wrong-typed on means on"),
        ("server/Lodge.Server/Calendar.cs", "Parse", "(frame)", "cal:create / cal:edit: Cal.Parse reads the event fields strictly (a wrong type refuses)"),
        ("server/Lodge.Server/CalendarModule.cs", "Range", "from,to", "cal:range: the bounds are whole JSON numbers only"),
        ("server/Lodge.Server/ChatHistory.cs", "HistoryBefore", "beforeAt", "history:before: beforeAt is a whole JSON number only"),
        ("server/Lodge.Server/FlightModule.cs", "Share", "legs,names", "flights.share: the raw legs go to FlightStore.Share / Flights.TryLeg"),
        ("server/Lodge.Server/FlightModule.cs", "ShareRoutes", "routes", "flights.routes.share: the raw routes go to FlightRoutes.Parse"),
        ("server/Lodge.Server/Profiles.cs", "Read", Dynamic, "profile:set: Field Read(m, key) - present / null / a string / another type"),
        ("server/Lodge.Server/Profiles.cs", "Set", "avatarId,accent,about,playTimes,displayName,main,showChars,visibleTo,hidden",
            "profile:set: a wrong type refuses, an absent field differs from null"),
        ("server/Lodge.Server/Voice.cs", "Clip,Int,Bool,Frac", Dynamic, "game / position: PresenceModule's strict readers for BuildGame / BuildPosition"),
        ("server/Lodge.Server/Voice.cs", "BuildGame", "level,sex,flags,groupSize,inInstance,instanceName,xpPct,playing,name,realm,class,classFile,zone,guild,race,raceFile,rested",
            "game: strict string / number / bool reads, clamps, the flag whitelist"),
        ("server/Lodge.Server/Voice.cs", "Game", "share", "game: only a JSON false stops sharing"),
        ("server/Lodge.Server/Voice.cs", "BuildPosition", "share,valid,scope,mapId,fx,fy,move", "position: share JSON false, valid JSON true, finite numbers, clamps, the scope whitelist"),
        ("src/Lodge/LodgeSession.Armory.cs", "OnArmoryCrafters", "spells", "armory.crafters: only the JSON numbers asked count, one by one"),
    };

    const string FrameGetters = "Str|Int|Long|Bool|Double|Child|List|Obj|ContainsKey|TryGetValue|TryGetPropertyValue|Remove|Add";
    const string AdapterCalls = "Str|Int|Long|Bool|Child|List|Longs|Strs|Num|Nums|Has|IsNull|RawChild|RawList|Keys|Set|SetRawChild|SetRawList|SetOrNull|LongOrNull|IntOrNull|IsFalse|BoolOrNull";
    const string Dynamic = "(dynamic)";                  // a key that is a variable (a helper reading the frame for its caller)
    static readonly Regex FrameIndex = new(@"\bm\s*\??\s*\[\s*""([^""]*)""\s*\](\s*=(?!=))?");
    static readonly Regex FrameIndexDyn = new(@"\bm\s*\??\s*\[\s*[A-Za-z_]\w*\s*\]");
    static readonly Regex FrameHelper = new(@"\b(\w+)\(\s*m\s*,\s*""([^""]*)""");                       // J.Str(m, "x"), Read(m, "x")
    static readonly Regex FrameCall = new(@"\bm\s*\??\.\s*(" + FrameGetters + @")\(\s*""([^""]*)""");
    static readonly Regex FrameCallDyn = new(@"\bm\s*\??\.\s*(?:" + FrameGetters + @")\(\s*[A-Za-z_]\w*\s*[,)]");
    static readonly Regex AdapterInline = new(@"\bnew\s+(?:LodgeJsonFields|LodgeFieldsDict)\s*\([^()]*(?:\([^()]*\)[^()]*)*\)\s*\.\s*(" + AdapterCalls + @")\s*\(");
    static readonly Regex AdapterVar = new(@"\b(?:var|ILodgeFields|ILodgeWriter|LodgeJsonFields|LodgeFieldsDict)\s+(\w+)\s*=\s*new\s+(?:LodgeJsonFields|LodgeFieldsDict)\b");
    static readonly Regex AdapterParam = new(@"\b(?:ILodgeFields|ILodgeWriter|LodgeJsonFields|LodgeFieldsDict)\s+(\w+)\s*[,)=]");
    static readonly Regex SetType = new(@"\[\s*""t""\s*\]\s*=(?!=)\s*(?:LodgeMsg\.|""|\w+Msg\.Type\b)|\bSet\(\s*""t""\s*,");
    static readonly Regex BuiltVar = new(@"\b(\w+)\s*=\s*(?:LodgeJson|LodgeFieldsDict)\.Build\(");
    static readonly Regex BuiltInline = new(@"(?:LodgeJson|LodgeFieldsDict)\.Build\((?:[^()]|\([^()]*\))*\)\s*\[\s*""");
    static readonly Regex TypeDecl = new(@"\b(?:class|struct|record|interface)\s+(\w+)");
    // a method declaration: [modifiers] type Name(params) or [modifiers] Name(params) (a constructor), followed by {, =>, : base(..) or
    // the end of the line; the first word is no statement keyword
    static readonly Regex Decl = new(@"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|async|override|virtual|sealed|readonly|unsafe|extern|partial|new|abstract)\s+)*"
        + @"(?:(?!(?:return|await|new|throw|yield|case|else|using|in|is|out|ref|goto|var|if|for|foreach|while|switch|lock|catch|when|not|and|or|typeof|nameof|default|delegate)\b)"
        + @"(?:[\w.]+(?:<[^()]*?>)?(?:\[\])?\??|\([^()]*\))\s+)?(\w+)\s*(?:<[\w\s,]*>)?\s*\(([^()]*(?:\([^()]*\)[^()]*)*)\)\s*(?:\{|=>|$|where\b|:\s*(?:base|this)\b)");
    static readonly HashSet<string> NotNames = new() { "if", "for", "foreach", "while", "switch", "lock", "catch", "using", "return", "nameof", "typeof", "when", "base", "this", "new", "await", "default", "sizeof", "checked", "unchecked", "fixed" };
    static readonly Regex FrameParam = new(@"^(?:this\s+)?(?:JsonObject|Dictionary<string,\s*object>)\??\s+(\w+)$");

    // one line of C#: the code without comments (strings kept) and the code with string / char contents blanked (for braces and declarations)
    sealed class Lexer
    {
        bool block, verbatim; int raw;
        public (string Code, string Blank) Line(string line)
        {
            var code = new System.Text.StringBuilder(); var blank = new System.Text.StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (block) { if (c == '*' && i + 1 < line.Length && line[i + 1] == '/') { block = false; i++; } continue; }
                if (raw > 0)
                {
                    if (c == '"' && Count(line, i, '"') >= raw) { code.Append('"', raw); blank.Append(' ', raw); i += raw - 1; raw = 0; }
                    else { code.Append(c); blank.Append(' '); }
                    continue;
                }
                if (verbatim)
                {
                    code.Append(c); blank.Append(' ');
                    if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { code.Append('"'); blank.Append(' '); i++; } else verbatim = false; }
                    continue;
                }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*') { block = true; i++; continue; }
                if (c == '"')
                {
                    int q = Count(line, i, '"');
                    if (q >= 3) { raw = q; code.Append('"', q); blank.Append(' ', q); i += q - 1; continue; }
                    if (i > 0 && (line[i - 1] == '@' || (line[i - 1] == '$' && i > 1 && line[i - 2] == '@'))) { verbatim = true; code.Append(c); blank.Append(' '); continue; }
                    code.Append(c); blank.Append('"');
                    for (i++; i < line.Length; i++)
                    {
                        code.Append(line[i]);
                        if (line[i] == '\\' && i + 1 < line.Length) { code.Append(line[++i]); blank.Append("  "); continue; }
                        if (line[i] == '"') { blank.Append('"'); break; }
                        blank.Append(' ');
                    }
                    continue;
                }
                if (c == '\'' && i + 2 < line.Length)
                {
                    int end = line[i + 1] == '\\' ? line.IndexOf('\'', i + 3) : (line[i + 2] == '\'' ? i + 2 : -1);
                    if (end > 0) { code.Append(line, i, end - i + 1); blank.Append(' ', end - i + 1); i = end; continue; }
                }
                code.Append(c); blank.Append(c);
            }
            return (code.ToString(), blank.ToString());
        }
        static int Count(string s, int i, char c) { int n = 0; while (i + n < s.Length && s[i + n] == c) n++; return n; }
    }

    // a call's arguments at the top level (null when the call doesn't close on this line)
    static List<string> Args(string code, int open)
    {
        var args = new List<string>(); int depth = 0, start = open + 1;
        for (int i = open; i < code.Length; i++)
        {
            char c = code[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') { depth--; if (depth == 0) { var last = code[start..i].Trim(); if (last.Length > 0 || args.Count > 0) args.Add(last); return args; } }
            else if (c == ',' && depth == 1) { args.Add(code[start..i].Trim()); start = i + 1; }
        }
        return null;
    }

    // one line of a file as the scan sees it: the code, the type and the methods it is in (outermost first), the method declared on it
    sealed record ScanLine(int No, string Code, string Blank, string Type, List<string> Methods, string Declared, string Params, HashSet<string> Adapters, HashSet<string> Built);

    static List<ScanLine> Walk(string path)
    {
        var lx = new Lexer();
        var result = new List<ScanLine>();
        var scopes = new List<(string Name, int Depth, bool IsType, HashSet<string> Adapters, HashSet<string> Built)>();
        string pendingMethod = null, pendingType = null, exprName = null; int depth = 0;
        var lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            var (code, blank) = lx.Line(lines[i]);
            var d = Decl.Match(blank);
            string declared = d.Success && !NotNames.Contains(d.Groups[1].Value) ? d.Groups[1].Value : null;
            var td = TypeDecl.Match(blank);
            if (td.Success) { pendingType = td.Groups[1].Value; declared = null; }
            else if (declared != null)
            {
                if (blank.Contains("=>", StringComparison.Ordinal) && !blank.Contains('{')) exprName = declared;
                else pendingMethod = declared;
            }
            var type = pendingType ?? scopes.LastOrDefault(s => s.IsType).Name;
            var methods = scopes.Where(s => !s.IsType).Select(s => s.Name).ToList();
            if (exprName != null) methods.Add(exprName); else if (pendingMethod != null) methods.Add(pendingMethod);
            var adapters = new HashSet<string>(scopes.SelectMany(s => s.Adapters));
            var built = new HashSet<string>(scopes.SelectMany(s => s.Built));
            foreach (Match m in AdapterVar.Matches(code)) adapters.Add(m.Groups[1].Value);
            if (declared != null) foreach (Match m in AdapterParam.Matches(d.Groups[2].Value + ")")) adapters.Add(m.Groups[1].Value);
            foreach (Match m in BuiltVar.Matches(code)) built.Add(m.Groups[1].Value);
            result.Add(new ScanLine(i + 1, code, blank, type, methods, declared, declared != null ? d.Groups[2].Value : null, adapters, built));

            foreach (char c in blank)
            {
                if (c == '{')
                {
                    if (pendingType != null) { scopes.Add((pendingType, depth, true, new(), new())); pendingType = null; }
                    else if (pendingMethod != null) { scopes.Add((pendingMethod, depth, false, new(), new())); pendingMethod = null; }
                    depth++;
                }
                else if (c == '}') { depth--; while (scopes.Count > 0 && scopes[^1].Depth >= depth) scopes.RemoveAt(scopes.Count - 1); }
            }
            var inner = scopes.LastOrDefault(s => !s.IsType);
            if (inner.Name != null) { inner.Adapters.UnionWith(adapters); inner.Built.UnionWith(built); }
            var end = blank.TrimEnd();
            if (end.EndsWith(';'))
            {
                exprName = null;
                if (pendingMethod != null && declared == pendingMethod) pendingMethod = null;   // an abstract / interface member or a call
                if (pendingType != null && td.Success) pendingType = null;                    // a positional record
            }
        }
        return result;
    }

    sealed record Hit(string Rule, string File, int Line, string Methods, string Key, string Text);

    // every hit of the rules F, N, A, T, B in the Lodge code (the allow-list not applied yet)
    static List<Hit> ScanFrames(string root)
    {
        var hits = new List<Hit>();
        var files = Sources(root).Select(p => (Path: p, Rel: Path.GetRelativePath(root, p).Replace('\\', '/'))).Where(f => !Adapters.Contains(f.Rel)).ToList();
        var walked = files.ToDictionary(f => f.Rel, f => Walk(f.Path));

        // the methods that take a JsonObject / Dictionary<string, object> parameter: by type and name, with the position and name of it (rule N)
        var frameParams = new List<(string Type, string Method, int Index, int Count, string Param, string Next, string File, int Line)>();
        foreach (var (rel, lines) in walked)
            foreach (var l in lines.Where(l => l.Declared != null))
            {
                var ps = Args("(" + l.Params + ")", 0) ?? new List<string>();
                for (int k = 0; k < ps.Count; k++)
                {
                    var pm = FrameParam.Match(Regex.Replace(ps[k], @"\s*=.*$", "").Trim());
                    if (pm.Success) frameParams.Add((l.Type, l.Declared, k, ps.Count, pm.Groups[1].Value, k + 1 < ps.Count ? ps[k + 1] : "", rel, l.No));
                }
            }

        // the helpers that read a field of the frame m for their caller: Foo(JsonObject m, string key, ..) - J.Str(m, "x"), Read(m, "x")
        var helpers = frameParams.Where(p => p.Index == 0 && p.Param == "m" && Regex.IsMatch(p.Next, @"^string\s+\w+")).Select(p => p.Method).ToHashSet();

        foreach (var (rel, lines) in walked)
            foreach (var l in lines)
            {
                var code = l.Code;
                void Add(string rule, string key) => hits.Add(new Hit(rule, rel, l.No, string.Join("/", l.Methods), key, code.Trim()));

                // F: the frame m by field name (reading "t" is the dispatch)
                foreach (Match m in FrameIndex.Matches(code)) if (m.Groups[1].Value != "t" || m.Groups[2].Success) Add("F", m.Groups[1].Value);
                foreach (Match m in FrameHelper.Matches(code)) if (m.Groups[2].Value != "t" && helpers.Contains(m.Groups[1].Value)) Add("F", m.Groups[2].Value);
                foreach (Match m in FrameCall.Matches(code)) if (m.Groups[2].Value != "t" || m.Groups[1].Value != "Str") Add("F", m.Groups[2].Value);
                if (FrameIndexDyn.IsMatch(l.Blank) || FrameCallDyn.IsMatch(l.Blank)) Add("F", Dynamic);
                // N: m handed to a method of this type (unqualified) or of the type it names (Cal.Parse(m)) that calls it otherwise
                foreach (Match m in Regex.Matches(l.Blank, @"(?:\b(\w+)\s*\.\s*)?\b(\w+)\s*\("))
                {
                    var name = m.Groups[2].Value;
                    if (l.Declared == name && m.Index <= l.Blank.IndexOf(name + "(", StringComparison.Ordinal)) continue;
                    var owner = m.Groups[1].Success ? m.Groups[1].Value : l.Type;
                    var args = Args(l.Blank, m.Index + m.Length - 1);
                    if (args == null || !args.Contains("m")) continue;
                    foreach (var p in frameParams.Where(p => p.Method == name && p.Type == owner && p.Count == args.Count && args[p.Index] == "m" && p.Param != "m"))
                        hits.Add(new Hit("N", p.File, p.Line, p.Method, "(frame)", rel + ":" + l.No + " passes m to " + p.Type + "." + p.Method + "(.. " + p.Param + " ..)"));
                }
                // A: a field through an adapter
                foreach (Match m in AdapterInline.Matches(code)) Add("A", m.Groups[1].Value);
                foreach (var a in l.Adapters)
                    foreach (Match m in Regex.Matches(code, @"(?<![\w.])" + Regex.Escape(a) + @"\s*\??\.\s*(" + AdapterCalls + @")\s*\(")) Add("A", a + "." + m.Groups[1].Value);
                // T: a frame's type set by hand
                if (SetType.IsMatch(code)) Add("T", "t");
                // B: a typed frame changed or read by field name
                if (BuiltInline.IsMatch(code)) Add("B", "Build(..)[..]");
                foreach (var b in l.Built)
                    if (Regex.IsMatch(code, @"(?<![\w.])" + Regex.Escape(b) + @"\s*\??\s*\[\s*""|(?<![\w.])" + Regex.Escape(b) + @"\s*\??\.\s*(" + FrameGetters + @")\(\s*""")) Add("B", b);
            }
        return hits;
    }

    static bool Allowed(Hit h) => h.Rule is "F" or "N" && RawOnPurpose.Any(a => a.File == h.File && a.Method.Split(',').Any(h.Methods.Split('/').Contains) && a.Keys.Split(',').Contains(h.Key));

    [Fact]
    public void No_frame_is_built_or_read_by_field_name_outside_the_typed_classes()
    {
        var root = RepoRoot();
        // where the frames come in, they are called m (so rule F sees every use): every module's Handle on the server, OnReceived in the Outpost
        var handles = Directory.EnumerateFiles(Path.Combine(root, "server", "Lodge.Server"), "*.cs")
            .SelectMany(p => Regex.Matches(File.ReadAllText(p), @"\bHandle\(\s*Member\s+\w+\s*,\s*string\s+\w+\s*,\s*JsonObject\s+(\w+)\s*\)").Select(m => (File: Path.GetFileName(p), Name: m.Groups[1].Value))).ToList();
        Assert.True(handles.Count >= 10, "only " + handles.Count + " module Handle(Member, string, JsonObject) methods found");
        Assert.True(handles.All(h => h.Name == "m"), "a module names the frame otherwise: " + string.Join(", ", handles.Where(h => h.Name != "m")));
        Assert.Contains("void OnReceived(Dictionary<string, object> m)", File.ReadAllText(Path.Combine(root, "src", "Lodge", "LodgeSession.cs")));
        Assert.Matches(@"\bm = LodgeJson\.Parse\(", File.ReadAllText(Path.Combine(root, "server", "Lodge.Server", "Hub.cs")));

        var hits = ScanFrames(root);
        Assert.True(hits.Count(Allowed) > 60, "only " + hits.Count(Allowed) + " raw reads on purpose found - is the scan looking at the right files?");
        var bad = hits.Where(h => !Allowed(h)).Select(h => h.Rule + " " + h.File + ":" + h.Line + " [" + h.Methods + "] " + h.Key + "  " + h.Text).Distinct().ToList();
        Assert.True(bad.Count == 0, bad.Count + " raw frame accesses - build / read the frame with its typed class in shared/Elans.Shared/Lodge "
            + "(or, for a strict validation on purpose, add the exact method and keys to RawOnPurpose with a reason):\n" + string.Join("\n", bad));
        // the allow-list holds nothing that isn't used any more (a stale entry would quietly allow a raw read later)
        var stale = RawOnPurpose.SelectMany(a => a.Method.Split(',').Select(method => (a.File, Method: method, a.Keys)))
            .Where(a => a.Keys.Split(',').Any(k => !hits.Any(h => h.File == a.File && h.Methods.Split('/').Contains(a.Method) && h.Key == k)))
            .Select(a => a.File + " " + a.Method + " " + a.Keys).ToList();
        Assert.True(stale.Count == 0, "RawOnPurpose entries (or keys) no longer used - remove them:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void The_frame_scan_finds_what_it_must()
    {
        // the scanner itself, on made-up lines: each rule fires, and the dispatch read of "t" doesn't
        var dir = Path.Combine(Path.GetTempPath(), "lodge-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "shared")); Directory.CreateDirectory(Path.Combine(dir, "server", "Lodge.Server")); Directory.CreateDirectory(Path.Combine(dir, "src", "Lodge"));
        try
        {
            foreach (var f in Files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, f))!); File.WriteAllText(Path.Combine(dir, f), ""); }
            File.WriteAllText(Path.Combine(dir, "server", "Lodge.Server", "Fake.cs"), """
                public class Fake
                {
                    public async Task<bool> Handle(Member me, string type, JsonObject m)
                    {
                        switch (J.Str(m, "t")) { case LodgeMsg.Msg: break; }
                        var x = J.Str(m, "text");
                        var y = (string)m["channel"];
                        Parse(me, m);
                        var f = new LodgeJsonFields(m);
                        var z = f.Str("id");
                        var q = new LodgeJsonFields(m).Int("n");
                        var o = LodgeJson.Build(new PingMsg().Write);
                        o["extra"] = 1;
                        await hub.Send(me, new JsonObject { ["t"] = LodgeMsg.Msg, ["text"] = x });
                        return true;
                    }
                    static void Parse(Member me, JsonObject req) { }
                }
                public static class J
                {
                    public static string Str(JsonObject m, string key) => null;
                }
                """);
            File.WriteAllText(Path.Combine(dir, "src", "Lodge", "Fake.cs"), """
                namespace X
                {
                    public partial class LodgeSession
                    {
                        void OnFrame(Dictionary<string, object> m)
                        {
                            switch (m.Str("t")) { case LodgeMsg.Msg: break; }
                            if (m.ContainsKey("canPin")) { }
                            _ = Send(new Dictionary<string, object> { ["t"] = LodgeMsg.Msg, ["text"] = "hi" });
                        }
                    }
                }
                """);
            var hits = ScanFrames(dir).Select(h => h.Rule + " " + Path.GetFileName(h.File) + ":" + h.Line + " " + h.Key).ToList();
            Assert.Equal(new[]
            {
                "F Fake.cs:6 text", "F Fake.cs:7 channel", "N Fake.cs:17 (frame)", "A Fake.cs:10 f.Str", "A Fake.cs:11 Int", "B Fake.cs:13 o", "T Fake.cs:14 t",
                "F Fake.cs:8 canPin", "T Fake.cs:9 t",
            }.OrderBy(x => x), hits.OrderBy(x => x));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
