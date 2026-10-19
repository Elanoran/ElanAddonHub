using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elans.Shared;
using Elans.Shared.Tests.Frozen;

namespace Elans.Shared.Tests;

// How lenient each side is with a Lodge frame, pinned: a table of tricky inputs (missing key, null, wrong types, odd numbers, unicode,
// duplicate keys, deep nesting, long strings, BOM, invalid JSON ...) read through every ILodgeFields member, on each side.
//
// The two sides as they were before step 7 (one JSON engine) are frozen copies in Frozen/: the Lodge server's frame parse + LodgeFieldsJson
// + its J readers, and the Outpost's frame parse + LodgeFieldsDict + its hand-written Services/Json.cs reader. The Outpost project is WPF
// and can't be referenced from here, so its reader is copied - which also keeps the old behaviour available as a reference after the
// Outpost has moved on. The expected table is JsonLeniency.golden.txt: per input, what both sides give alike ("both") and where they
// differ ("server" / "outpost"). A change of behaviour fails here with the difference; to accept a deliberate one, regenerate the file
// (set ELANS_WRITE_GOLDEN=1 and run the tests once) and review the diff.
//
// Where the two sides already differed before step 7 (the "server:" / "outpost:" lines of the golden file):
//   - whole numbers beyond 2^53 (Long, LongOrNull, Longs): the server reads them exactly, the Outpost through a double (rounded);
//   - Longs of whole numbers written as fractions or exponents ([1.0, 2e0]): the server gives 0, the Outpost the number;
//   - a string or key with an escaped lone surrogate ("\ud800"): the server throws when it is read (a key: when anything is read), the
//     Outpost keeps the lone surrogate;
//   - duplicate keys: the server throws when the object is read (Int / Long swallow it and give 0), the Outpost keeps the last value;
//   - nesting: the server skips a frame deeper than 64 levels, the Outpost reads any depth - and a very deep one ends the process (stack
//     overflow, can't be caught);
//   - invalid JSON the Outpost read anyway and the server skips: non-JSON whitespace (NBSP), anything after the object (garbage, a second
//     object), +1, 01, .5, 1., raw control characters and newlines inside strings, unknown escapes ("\x" read as "x").
// Alike on both: a BOM, trailing commas, comments, NaN / Infinity and single quotes skip the frame; invalid UTF-8 becomes U+FFFD; 1e400 is
// read as Infinity by Num / Nums; numbers beyond int / long saturate; a fraction is cut toward zero by Int / Long.
public class JsonLeniencyTests
{
    // ---------------------------------------------------------------- the inputs

    static byte[] U(string s) => Encoding.UTF8.GetBytes(s);
    static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    static string Nest(int depth) => new string('[', depth) + "1" + new string(']', depth);

    // the value of "a" in {"a": <value>, "z": 1}: one row per value
    static readonly (string Name, string Value)[] Values =
    {
        ("null", "null"), ("string", "\"x\""), ("empty string", "\"\""), ("number as string", "\"123\""), ("bool string", "\"true\""),
        ("true", "true"), ("false", "false"), ("zero", "0"), ("minus zero", "-0"), ("minus zero fraction", "-0.0"), ("int", "3"),
        ("whole fraction", "3.0"), ("fraction", "3.7"), ("negative fraction", "-3.7"), ("exponent", "1e2"), ("exponent fraction", "2.5e1"),
        ("int max + 1", "2147483648"), ("int min - 1", "-2147483649"), ("2^53 + 1", "9007199254740993"), ("long max", "9223372036854775807"),
        ("long max + 1", "9223372036854775808"), ("30 digits", "123456789012345678901234567890"), ("1e300", "1e300"), ("-1e300", "-1e300"),
        ("1e400 (beyond double)", "1e400"), ("1e-400 (underflow)", "1e-400"), ("empty object", "{}"), ("object", "{\"b\":1,\"c\":\"x\"}"),
        ("empty array", "[]"), ("array of ints", "[1,2,3]"), ("array of whole fractions", "[1.0,2e0,-3.0]"), ("array mixed", "[1,2.5,\"3\",null,true,{},[]]"),
        ("array with null", "[null,1.5]"), ("array of strings", "[\"x\",\"y\",1]"), ("array of objects", "[{\"b\":1},2,{\"c\":2},null]"),
        ("array of huge numbers", "[1e300,9007199254740993,1e400]"), ("escaped e-acute", "\"\\u00e9\""), ("escaped pair", "\"\\ud83d\\ude00\""),
        ("raw emoji", "\"\U0001F600\""), ("escaped lone high", "\"x\\ud800y\""), ("escaped lone low", "\"\\udc00\""), ("escaped NUL", "\"a\\u0000b\""),
        ("escapes", "\"\\\"\\\\\\/\\b\\f\\n\\r\\t\""), ("long string", "\"" + new string('x', 100_000) + "\""),
        ("deep 10", Nest(10)), ("deep 62", Nest(62)), ("deep 63", Nest(63)), ("deep 64", Nest(64)), ("deep 1000", Nest(1000)),
    };

    // whole frames (parse-level cases)
    static readonly (string Name, byte[] Frame)[] Frames =
    {
        ("missing key", U("{\"z\":1}")),
        ("duplicate key", U("{\"a\":1,\"a\":2}")),
        ("duplicate key, different types", U("{\"a\":\"x\",\"a\":{\"b\":1}}")),
        ("duplicate key nested", U("{\"a\":{\"b\":1,\"b\":2}}")),
        ("duplicate key in an array entry", U("{\"a\":[{\"b\":1,\"b\":2}]}")),
        ("BOM", Cat(new byte[] { 0xEF, 0xBB, 0xBF }, U("{\"a\":1}"))),
        ("whitespace around", U(" \r\n\t{\"a\":1} \n")),
        ("non-JSON whitespace (NBSP)", U("\u00A0{\"a\":1}")),
        ("trailing comma in object", U("{\"a\":1,}")),
        ("trailing comma in array", U("{\"a\":[1,]}")),
        ("block comment", U("{\"a\":1/*c*/}")),
        ("line comment", U("// c\n{\"a\":1}")),
        ("trailing garbage", U("{\"a\":1} x")),
        ("two objects", U("{\"a\":1}{\"a\":2}")),
        ("truncated", U("{\"a\":")),
        ("empty frame", U("")),
        ("array root", U("[{\"a\":1}]")),
        ("string root", U("\"a\"")),
        ("null root", U("null")),
        ("plus sign", U("{\"a\":+1}")),
        ("leading zero", U("{\"a\":01}")),
        ("leading dot", U("{\"a\":.5}")),
        ("trailing dot", U("{\"a\":1.}")),
        ("NaN", U("{\"a\":NaN}")),
        ("Infinity", U("{\"a\":Infinity}")),
        ("single quotes", U("{'a':1}")),
        ("unquoted key", U("{a:1}")),
        ("raw control character in a string", U("{\"a\":\"x\u0001y\"}")),
        ("raw newline in a string", U("{\"a\":\"x\ny\"}")),
        ("unknown escape", U("{\"a\":\"\\x\"}")),
        ("short unicode escape", U("{\"a\":\"\\u12\"}")),
        ("invalid UTF-8 in a string", Cat(U("{\"a\":\"x"), new byte[] { 0xFF }, U("y\"}"))),
        ("invalid UTF-8 in a key", Cat(U("{\"a"), new byte[] { 0xC3 }, U("\":1}"))),
        ("overlong UTF-8", Cat(U("{\"a\":\""), new byte[] { 0xC0, 0xAF }, U("\"}"))),
        ("UTF-8 encoded lone surrogate", Cat(U("{\"a\":\""), new byte[] { 0xED, 0xA0, 0x80 }, U("\"}"))),
        ("escaped lone surrogate in a key", U("{\"\\ud800\":1,\"a\":1}")),
        ("key with an escaped quote", U("{\"a\\\"\":1,\"a\":2}")),
        ("very deep (100000)", U("{\"a\":" + new string('[', 100_000) + new string(']', 100_000) + "}")),
    };

    public static IEnumerable<(string Name, byte[] Frame)> Cases() =>
        Values.Select(v => ("a = " + v.Name, U("{\"a\":" + v.Value + ",\"z\":1}"))).Concat(Frames);

    // ---------------------------------------------------------------- the sides

    // name -> how a frame becomes fields (null = the frame is skipped)
    public static readonly (string Name, Func<byte[], ILodgeFields> Read)[] OldSides =
    {
        ("server", b => OldFrames.Server(b) is JsonObject o ? new OldServerFields(o) : null),
        // the old Outpost reader recurses once per level: 100000 levels overflow the stack and end the process (it can't be caught), so
        // that one input is not given to it - recorded as "crashes the process"
        ("outpost", b => b.Length > 150_000 ? throw new InsufficientExecutionStackException("crashes the process (stack overflow)") : OldFrames.Outpost(b) is Dictionary<string, object> d ? new OldOutpostFields(d) : null),
    };

    // ---------------------------------------------------------------- describing what a side read

    static readonly string[] Members = { "Has", "IsNull", "Str", "Int", "Long", "Bool", "BoolOrNull", "IsFalse", "IntOrNull", "LongOrNull", "Num", "Nums", "Longs", "Strs", "Child", "List", "RawChild", "RawList", "Keys" };

    static string Esc(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        var shown = s.Length > 40 ? s[..20] : s;
        foreach (var c in shown)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        sb.Append('"');
        if (shown.Length < s.Length) sb.Append("..(").Append(s.Length).Append(" chars)");
        return sb.ToString();
    }

    static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);
    static string Num(double? d) => d is double v ? Num(v) : "null";
    static string Seq<T>(IEnumerable<T> xs, Func<T, string> f) => xs == null ? "null" : "[" + string.Join(",", xs.Select(f)) + "]";

    // a raw part in either model, written the same way: numbers as doubles, strings escaped as above, cut at 80 characters
    static string Raw(object v)
    {
        var sb = new StringBuilder();
        void W(object x)
        {
            if (sb.Length > 80) return;
            switch (x)
            {
                case null: sb.Append("null"); break;
                case JsonObject o: sb.Append('{'); bool f1 = true; foreach (var kv in o) { if (!f1) sb.Append(','); f1 = false; sb.Append(Esc(kv.Key)).Append(':'); W(kv.Value); } sb.Append('}'); break;
                case JsonArray a: sb.Append('['); bool f2 = true; foreach (var e in a) { if (!f2) sb.Append(','); f2 = false; W(e); } sb.Append(']'); break;
                case JsonValue jv:
                    switch (jv.GetValueKind())
                    {
                        case JsonValueKind.String: sb.Append(Esc(jv.GetValue<string>())); break;
                        case JsonValueKind.Number: sb.Append(jv.TryGetValue<double>(out var d) ? Num(d) : "number?" + jv.ToJsonString()); break;
                        case JsonValueKind.True: sb.Append("true"); break;
                        case JsonValueKind.False: sb.Append("false"); break;
                        default: sb.Append("?" + jv.GetValueKind()); break;
                    }
                    break;
                case Dictionary<string, object> d2: sb.Append('{'); bool f3 = true; foreach (var kv in d2) { if (!f3) sb.Append(','); f3 = false; sb.Append(Esc(kv.Key)).Append(':'); W(kv.Value); } sb.Append('}'); break;
                case List<object> l: sb.Append('['); bool f4 = true; foreach (var e in l) { if (!f4) sb.Append(','); f4 = false; W(e); } sb.Append(']'); break;
                case string s: sb.Append(Esc(s)); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case double n: sb.Append(Num(n)); break;
                case long n: sb.Append(Num(n)); break;
                default: sb.Append("?" + x.GetType().Name); break;
            }
        }
        W(v);
        return sb.Length > 80 ? sb.ToString(0, 80) + ".." : sb.ToString();
    }

    static string Read(ILodgeFields f, string member) => member switch
    {
        "Has" => f.Has("a") ? "true" : "false",
        "IsNull" => f.IsNull("a") ? "true" : "false",
        "Str" => Esc(f.Str("a")),
        "Int" => f.Int("a").ToString(CultureInfo.InvariantCulture),
        "Long" => f.Long("a").ToString(CultureInfo.InvariantCulture),
        "Bool" => f.Bool("a") ? "true" : "false",
        "BoolOrNull" => f.BoolOrNull("a") is bool b ? (b ? "true" : "false") : "null",
        "IsFalse" => f.IsFalse("a") ? "true" : "false",
        "IntOrNull" => f.IntOrNull("a")?.ToString(CultureInfo.InvariantCulture) ?? "null",
        "LongOrNull" => f.LongOrNull("a")?.ToString(CultureInfo.InvariantCulture) ?? "null",
        "Num" => Num(f.Num("a")),
        "Nums" => Seq(f.Nums("a"), Num),
        "Longs" => Seq(f.Longs("a"), x => x.ToString(CultureInfo.InvariantCulture)),
        "Strs" => Seq(f.Strs("a"), Esc),
        "Child" => f.Child("a") is ILodgeFields c ? "{" + string.Join(",", c.Keys()) + "}" : "null",
        "List" => Seq(f.List("a"), x => "{" + string.Join(",", x.Keys()) + "}"),
        "RawChild" => f.RawChild("a") is object rc ? Raw(rc) : "null",
        "RawList" => f.RawList("a") is object rl ? Raw(rl) : "null",
        "Keys" => Seq(f.Keys(), Esc),
        _ => throw new ArgumentException(member),
    };

    // member -> what a side gives (an exception is a result too: "throws <Type>"), or one entry "frame" when the frame is skipped
    public static List<(string Member, string Value)> Describe(Func<byte[], ILodgeFields> side, byte[] frame)
    {
        ILodgeFields f;
        try { f = side(frame); }
        catch (Exception e) { return new() { ("frame", e is InsufficientExecutionStackException ? e.Message : "throws " + e.GetType().Name) }; }
        if (f == null) return new() { ("frame", "skipped") };
        var r = new List<(string, string)> { ("frame", "read") };
        foreach (var m in Members)
        {
            string v;
            try { v = Read(f, m); } catch (Exception e) { v = "throws " + e.GetType().Name; }
            r.Add((m, v));
        }
        return r;
    }

    // ---------------------------------------------------------------- the table

    // one block per input: the members both sides agree on, then each side's own values where they differ
    public static string Table(IEnumerable<(string Name, Func<byte[], ILodgeFields> Read)> sides)
    {
        var sb = new StringBuilder();
        var list = sides.ToList();
        foreach (var (name, frame) in Cases())
        {
            sb.Append("## ").Append(name).Append('\n');
            var all = list.Select(s => (s.Name, Got: Describe(s.Read, frame).ToDictionary(x => x.Member, x => x.Value))).ToList();
            var keys = all.SelectMany(a => a.Got.Keys).Distinct().ToList();
            var same = keys.Where(k => all.All(a => a.Got.TryGetValue(k, out var v) && v == all[0].Got[k])).ToList();
            if (same.Count > 0) sb.Append("  both: ").Append(string.Join("  ", same.Select(k => k + "=" + all[0].Got[k]))).Append('\n');
            foreach (var (side, got) in all)
            {
                var own = keys.Where(k => !same.Contains(k)).ToList();
                if (own.Count == 0) continue;
                sb.Append("  ").Append(side).Append(": ").Append(string.Join("  ", own.Select(k => k + "=" + (got.TryGetValue(k, out var v) ? v : "-")))).Append('\n');
            }
        }
        return sb.ToString();
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "shared")) && Directory.Exists(Path.Combine(d.FullName, "server"))) return d.FullName;
        throw new DirectoryNotFoundException("no folder above " + AppContext.BaseDirectory + " holds both shared/ and server/");
    }

    static void Golden(string file, string actual)
    {
        var path = Path.Combine(RepoRoot(), "shared", "Elans.Shared.Tests", file);
        if (Environment.GetEnvironmentVariable("ELANS_WRITE_GOLDEN") == "1") { File.WriteAllText(path, actual, new UTF8Encoding(false)); return; }
        Assert.True(File.Exists(path), file + " is missing - run the tests once with ELANS_WRITE_GOLDEN=1 and review it");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        if (expected == actual) return;
        var e = expected.Split('\n'); var a = actual.Split('\n');
        var diff = new List<string>();
        string block = null;
        for (int i = 0; i < Math.Max(e.Length, a.Length) && diff.Count < 60; i++)
        {
            var el = i < e.Length ? e[i] : "(end)"; var al = i < a.Length ? a[i] : "(end)";
            if (al.StartsWith("## ")) block = al;
            if (el != al) diff.Add((block ?? "") + "\n  expected: " + el + "\n  actual:   " + al);
        }
        Assert.Fail(file + " differs (a deliberate change: regenerate with ELANS_WRITE_GOLDEN=1 and review the diff):\n" + string.Join("\n", diff));
    }

    [Fact]
    public void The_old_sides_read_every_tricky_input_as_pinned()
    {
        Golden("JsonLeniency.golden.txt", Table(OldSides));
    }

    // the shared engine (LodgeJson.Parse + LodgeJsonFields): one engine, the same on both sides of the Lodge link
    public static ILodgeFields Shared(byte[] b) => LodgeJson.Parse(b) is JsonObject o ? new LodgeJsonFields(o) : null;

    // Deliberate differences of the shared engine from the old SERVER: input -> why. Everything else reads exactly as before.
    // (the parser's rules, LodgeJson.Rules: these frames are now skipped as a whole, on both sides)
    static readonly Dictionary<string, string> ServerChanges = new()
    {
        ["duplicate key"] = "a key twice: the frame is skipped (it threw when read - for \"t\" in the hub's dispatch, outside the per-message catch)",
        ["duplicate key, different types"] = "the same",
        ["duplicate key nested"] = "the same, at any level (a handler threw when it reached that object)",
        ["duplicate key in an array entry"] = "the same",
        ["a = escaped lone high"] = "a lone surrogate in a string: skipped (reading that string threw in the handler)",
        ["a = escaped lone low"] = "the same",
        ["escaped lone surrogate in a key"] = "the same, in a key (any read of the object threw)",
        ["invalid UTF-8 in a string"] = "invalid UTF-8: skipped (it was decoded with U+FFFD in its place and read)",
        ["invalid UTF-8 in a key"] = "the same, in a key",
        ["overlong UTF-8"] = "the same (an overlong form is invalid UTF-8)",
        ["UTF-8 encoded lone surrogate"] = "the same (a surrogate encoded as UTF-8 is invalid UTF-8)",
    };

    // Deliberate differences in what a frame that still parses READS as (both old sides read these alike): input -> why. The members named
    // here (Num, Nums) are compared as null; every other member of the line still reads exactly as before.
    internal static readonly Dictionary<string, string> NumberChanges = new()
    {
        ["a = 1e400 (beyond double)"] = "a number too big for a double is no number for Num (it read as Infinity, which no JSON writer can write "
            + "back: a message that kept it - PositionMsg.Fx - couldn't be sent again); Int / Long still clamp, IsFalse stays false",
        ["a = array of huge numbers"] = "the same inside an array: Nums refuses the list as a whole (strict, as for any entry that is no number)",
    };

    static List<(string Member, string Value)> WithoutNumbers(List<(string Member, string Value)> d) => d.Where(x => x.Member is not ("Num" or "Nums")).ToList();

    // Where the old Outpost read differently, the shared engine reads as the old server did (or as ServerChanges says): input -> why that is
    // the Outpost's behaviour now. The rule was to keep the Outpost's own leniency unless the server's is clearly safer - none of the old
    // Outpost's leniencies is kept, for these reasons:
    static readonly Dictionary<string, string> OutpostChanges = new()
    {
        ["a = 1e400 (beyond double)"] = "as on the server now (NumberChanges): no number for Num",
        ["a = 2^53 + 1"] = "whole numbers beyond 2^53 are read exactly (they were rounded through a double); every id and time is far below that",
        ["a = array of huge numbers"] = "the same, inside an array (Longs)",
        ["a = array of whole fractions"] = "Longs takes whole JSON numbers only (1.0 / 2e0 = 0, as on the server): the Outpost's one Longs reading "
            + "(armory.crafters spells) is not used - OnArmoryCrafters reads those numbers itself, strictly",
        ["a = escaped lone high"] = "a lone surrogate is no text: the frame is skipped (one rule for both sides, LodgeJson.Rules) instead of kept",
        ["a = escaped lone low"] = "the same",
        ["escaped lone surrogate in a key"] = "the same, in a key",
        ["a = deep 64"] = "at most 64 levels, as on the server: the old reader took any depth and a very deep frame ended the process",
        ["a = deep 1000"] = "the same",
        ["very deep (100000)"] = "the same: skipped instead of a stack overflow",
        ["duplicate key"] = "a key twice skips the frame (one rule for both sides, LodgeJson.Rules) instead of the Outpost's last-wins",
        ["duplicate key, different types"] = "the same",
        ["duplicate key nested"] = "the same",
        ["duplicate key in an array entry"] = "the same",
        ["non-JSON whitespace (NBSP)"] = "invalid JSON is skipped, as on the server: every Lodge writes valid JSON, so leniency here only helps a forged frame",
        ["trailing garbage"] = "the same",
        ["two objects"] = "the same",
        ["plus sign"] = "the same",
        ["leading zero"] = "the same",
        ["leading dot"] = "the same",
        ["trailing dot"] = "the same",
        ["raw control character in a string"] = "the same",
        ["raw newline in a string"] = "the same",
        ["unknown escape"] = "the same",
        ["invalid UTF-8 in a string"] = "invalid UTF-8 skips the frame (one rule for both sides, LodgeJson.Rules) instead of U+FFFD in its place",
        ["invalid UTF-8 in a key"] = "the same",
        ["overlong UTF-8"] = "the same",
        ["UTF-8 encoded lone surrogate"] = "the same",
    };

    static string Line(List<(string Member, string Value)> d) => string.Join("  ", d.Select(x => x.Member + "=" + x.Value));

    [Fact]
    public void The_shared_engine_reads_every_tricky_input_as_the_server_did()
    {
        var bad = new List<string>();
        foreach (var (name, frame) in Cases())
        {
            if (ServerChanges.ContainsKey(name)) continue;
            var old = Describe(OldSides[0].Read, frame); var now = Describe(Shared, frame);
            if (NumberChanges.ContainsKey(name))
            {
                if (!WithoutNumbers(old).SequenceEqual(WithoutNumbers(now)) || now.Where(x => x.Member is "Num" or "Nums").Any(x => x.Value != "null"))
                    bad.Add(name + " (NumberChanges)\n  old:    " + Line(old) + "\n  shared: " + Line(now));
                continue;
            }
            if (!old.SequenceEqual(now)) bad.Add(name + "\n  old:    " + Line(old) + "\n  shared: " + Line(now));
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
        Assert.True(ServerChanges.Keys.All(k => Cases().Any(c => c.Name == k)), "ServerChanges names an input that isn't in the table");
        // and each of those is skipped as a whole now
        foreach (var (name, frame) in Cases().Where(c => ServerChanges.ContainsKey(c.Name)))
            Assert.True(Describe(Shared, frame).SequenceEqual(new[] { ("frame", "skipped") }), name + " is not skipped: " + Line(Describe(Shared, frame)));
    }

    [Fact]
    public void The_Outpost_reads_like_before_except_where_a_decision_says_otherwise()
    {
        // the old Outpost against the shared engine: they differ exactly on the inputs OutpostChanges explains (and agree on every other one)
        var differ = new List<string>();
        foreach (var (name, frame) in Cases())
        {
            var old = Describe(OldSides[1].Read, frame); var now = Describe(Shared, frame);
            if (!old.SequenceEqual(now)) differ.Add(name);
        }
        var unexplained = differ.Where(n => !OutpostChanges.ContainsKey(n)).ToList();
        var stale = OutpostChanges.Keys.Where(n => !differ.Contains(n)).ToList();
        Assert.True(unexplained.Count == 0, "the Outpost now reads these differently, with no decision in OutpostChanges:\n" + string.Join("\n", unexplained));
        Assert.True(stale.Count == 0, "OutpostChanges explains inputs that read alike (remove them):\n" + string.Join("\n", stale));
    }
}
