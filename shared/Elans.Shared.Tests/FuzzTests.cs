using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elans.Shared;
using Xunit.Abstractions;
using static Elans.Shared.Tests.FuzzGen;

namespace Elans.Shared.Tests;

// Fuzz tests for everything the Lodge link reads from the other side: every typed message class (Read through the shared JSON engine,
// LodgeJson / LodgeJsonFields, then Write and Read again) and the signed formats (B64, Es256, ReleaseSig, PublicUrl, CairnFormats).
// Deterministic: every suite has a fixed seed, so a failure names the seed / iteration that reproduces it. Normal mode is fast enough for
// every test run; ELANS_FUZZ=deep runs many more iterations (and the 1 MB inputs more often).
//
// The boundary: LodgeJson.Parse. It refuses (null, never an exception) what LodgeJson.Rules refuse - invalid JSON, more than 64 levels, a
// key twice, invalid UTF-8, a lone surrogate - so a frame like that is dropped there and no Read ever sees it. Nothing past Parse may throw:
// no exception is excused anywhere in these suites.
public class FuzzTests
{
    readonly ITestOutputHelper output;
    public FuzzTests(ITestOutputHelper output) { this.output = output; }

    [Fact]
    public void Every_typed_class_has_a_Read_and_a_Write()
    {
        var all = Messages();
        Assert.True(all.Count > 100, "only " + all.Count + " typed classes");
        var missing = all.Where(m => m.Read == null || m.Write == null).Select(m => m.Class.Name).ToList();
        Assert.True(missing.Count == 0, "no Read(ILodgeFields) / Write(ILodgeWriter): " + string.Join(", ", missing));
    }

    // the three problems the first fuzz run found in the shared engine (FuzzGen: what the parser refuses), fixed in step 7 and after it

    // a lone surrogate escape threw InvalidOperationException on read (any Read, and the hub's J.Str(m, "t")); now the frame is refused
    [Fact]
    public void Regression_lone_surrogate_escapes_refuse_the_frame()
    {
        foreach (var lone in LoneSurrogates)
        {
            foreach (var text in new[] { @"{""t"":""" + lone + @"""}", @"{""t"":""msg"",""text"":""" + lone + @"""}", @"{""" + lone + @""":1,""t"":""ping""}",
                @"{""t"":""msg"",""o"":{""a"":[""" + lone + @"""]}}" })
            {
                Assert.Null(LodgeJson.Parse(text));
                Assert.Null(LodgeJson.Parse(Encoding.UTF8.GetBytes(text)));
            }
        }
        // a pair is fine
        var ok = LodgeJson.Parse(@"{""t"":""msg"",""text"":""" + (char)92 + "ud83d" + (char)92 + @"ude00""}");
        Assert.NotNull(ok);
        Assert.Equal(char.ConvertFromUtf32(0x1F600), ChatSendMsg.Read(new LodgeJsonFields(ok)).Text);
    }

    // a key twice threw ArgumentException on the first read of the object; now the frame is refused, at any depth
    [Fact]
    public void Regression_a_key_twice_refuses_the_frame()
    {
        foreach (var text in new[] { @"{""t"":""msg"",""t"":""ping""}", @"{""t"":""msg"",""text"":""a"",""text"":1}", @"{""t"":""msg"",""o"":{""a"":1,""a"":1}}",
            @"{""t"":""msg"",""l"":[{""a"":1,""a"":2}]}" })
        {
            Assert.Null(LodgeJson.Parse(text));
            Assert.Null(LodgeJson.Parse(Encoding.UTF8.GetBytes(text)));
        }
    }

    // a number too big for a double read as Infinity, which a message kept (PositionMsg.Fx) and which then couldn't be written: now it is no
    // number (Num / Nums null), everything else reads as before (Int / Long clamp, IsFalse stays false)
    [Fact]
    public void Regression_a_huge_number_is_no_number()
    {
        var o = LodgeJson.Parse(@"{""t"":""position"",""mapId"":1,""fx"":1e309,""fy"":-1e309,""valid"":true,""l"":[1,1e400],""s"":1e400}");
        var f = new LodgeJsonFields(o);
        Assert.Null(f.Num("fx"));
        Assert.Null(f.Num("fy"));
        Assert.Null(f.Nums("l"));
        Assert.Equal(long.MaxValue, f.Long("s"));
        Assert.Equal(int.MinValue, f.Int("fy"));
        Assert.False(f.IsFalse("s"));
        Assert.Null(f.BoolOrNull("s"));
        Assert.Null(LodgeJson.ToPlain(o["s"]));
        var p = PositionMsg.Read(f);
        var again = LodgeJson.Parse(LodgeJson.Serialize(LodgeJson.Build(p.Write)));
        Assert.NotNull(again);
        Assert.Equal(0, again["fx"]!.GetValue<double>());
        var mp = MemberPosMsg.Read(new LodgeJsonFields(LodgeJson.Parse(@"{""t"":""memberpos"",""id"":3,""valid"":true,""fx"":1e400,""fy"":0.5}")));
        Assert.Null(mp.Fx);
        LodgeJson.Serialize(LodgeJson.Build(mp.Write));
    }

    // ================================================================ the harness: parse, read, write, read again

    // writes like LodgeJsonFields, but a raw child that still belongs to the frame it was read from is copied first: a JsonNode has one
    // parent, and the classes pass raw children through as they are (each side clones one where it keeps or forwards it)
    sealed class CloningWriter : ILodgeWriter
    {
        readonly ILodgeWriter w;
        public CloningWriter(ILodgeWriter w) { this.w = w; }
        static object Own(object v) => v is JsonNode n && n.Parent != null ? n.DeepClone() : v;
        public void Set(string key, string value) => w.Set(key, value);
        public void Set(string key, long value) => w.Set(key, value);
        public void Set(string key, int value) => w.Set(key, value);
        public void Set(string key, bool value) => w.Set(key, value);
        public void Set(string key, double value) => w.Set(key, value);
        public ILodgeWriter Child(string key) => new CloningWriter(w.Child(key));
        public ILodgeListWriter List(string key) => new ListW(w.List(key));
        public void SetRawChild(string key, object value) => w.SetRawChild(key, Own(value));
        public void SetRawList(string key, object value) => w.SetRawList(key, Own(value));
        sealed class ListW : ILodgeListWriter
        {
            readonly ILodgeListWriter l;
            public ListW(ILodgeListWriter l) { this.l = l; }
            public ILodgeWriter Add() => new CloningWriter(l.Add());
            public void Add(long value) => l.Add(value);
            public void Add(string value) => l.Add(value);
            public void Add(double value) => l.Add(value);
            public void AddNull() => l.AddNull();
        }
    }

    internal sealed class Report
    {
        public readonly List<string> Problems = new();
        public int Frames, Parsed, Rejected, NeedsParts;
        public double WorstAllocRatio; public string WorstAlloc = "";
        public void Add(string p) { lock (Problems) if (Problems.Count < 40) Problems.Add(p); }
    }

    // a Read may allocate this much: linear in the input, plus a fixed amount. Measured: text costs about 2-6x its UTF-8 size (UTF-16
    // strings, the nodes); the worst case is an array of empty objects ([{},{},..], 3 bytes each), where every entry becomes a typed object
    // with its own lists - about 110x (BadgesMemberMsg, 16 MB for a 150 KB frame). Linear, so bounded by the frame cap (the server takes 64 KB
    // frames: at most ~7 MB), but a large factor: see the report. The bound catches anything worse (quadratic, unbounded).
    internal const long AllocFixed = 256 * 1024;
    internal const int AllocPerByte = 160;

    // the classes whose Write needs a part that a frame may leave out (JoinMsg.User): as LodgeMsgTests.Every_typed_class_writes_its_own_type
    // says, such a class is written only with its parts set (the server builds it from its own state, it never writes back what it read)
    internal static readonly HashSet<Type> NeedsParts = Messages().Where(m =>
    {
        try { m.Write.Invoke(Activator.CreateInstance(m.Class), new object[] { new LodgeJsonFields() }); return false; }
        catch (TargetInvocationException e) when (e.InnerException is NullReferenceException) { return true; }
    }).Select(m => m.Class).ToHashSet();

    static string Clip(string s) => s.Length > 300 ? s[..300] + "... (" + s.Length + " chars)" : s;

    internal static void Run(Msg msg, string text, string where, Report rep)
    {
        rep.Frames++;
        JsonObject obj;
        try { obj = LodgeJson.Parse(text); }
        catch (Exception e) { rep.Add(where + ": LodgeJson.Parse threw " + e.GetType().Name + ": " + e.Message + "\n  " + Clip(text)); return; }
        if (obj == null) { rep.Rejected++; return; }
        rep.Parsed++;
        long inputBytes = Encoding.UTF8.GetByteCount(text);
        object read;
        long before = GC.GetAllocatedBytesForCurrentThread();
        try { read = msg.Read.Invoke(null, new object[] { new LodgeJsonFields(obj) }); }
        catch (TargetInvocationException e) { rep.Add(where + ": " + msg.Class.Name + ".Read threw " + e.InnerException!.GetType().Name + ": " + e.InnerException.Message + "\n  " + Clip(text)); return; }
        long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        double ratio = (double)alloc / Math.Max(1, inputBytes);
        if (alloc > 64 * 1024 && ratio > rep.WorstAllocRatio) { rep.WorstAllocRatio = ratio; rep.WorstAlloc = msg.Class.Name + " " + alloc + " bytes for " + inputBytes; }
        if (alloc > AllocFixed + AllocPerByte * inputBytes)
            rep.Add(where + ": " + msg.Class.Name + ".Read allocated " + alloc + " bytes for a " + inputBytes + "-byte frame\n  " + Clip(text));
        if (read == null) { rep.Add(where + ": " + msg.Class.Name + ".Read returned null"); return; }

        // write it, serialize, parse and read it again
        string again;
        try
        {
            var w = new LodgeJsonFields();
            msg.Write.Invoke(read, new object[] { new CloningWriter(w) });
            again = LodgeJson.Serialize(w.Obj);
        }
        catch (TargetInvocationException e) when (e.InnerException is NullReferenceException && NeedsParts.Contains(msg.Class)) { rep.NeedsParts++; return; }
        catch (TargetInvocationException e) { rep.Add(where + ": " + msg.Class.Name + ".Write of the read message threw " + e.InnerException!.GetType().Name + ": " + e.InnerException.Message + "\n  " + Clip(text)); return; }
        catch (Exception e) { rep.Add(where + ": serializing the written " + msg.Class.Name + " threw " + e.GetType().Name + ": " + e.Message + "\n  " + Clip(text)); return; }
        var obj2 = LodgeJson.Parse(again);
        if (obj2 == null) { rep.Add(where + ": the written " + msg.Class.Name + " does not parse again\n  " + Clip(again)); return; }
        try { msg.Read.Invoke(null, new object[] { new LodgeJsonFields(obj2) }); }
        catch (TargetInvocationException e) { rep.Add(where + ": " + msg.Class.Name + ".Read of its own output threw " + e.InnerException!.GetType().Name + ": " + e.InnerException.Message + "\n  " + Clip(again)); }
    }

    // runs body on a worker with a time limit: a hang fails the test instead of stalling the run
    internal static void WithinTime(TimeSpan limit, string what, Action body)
    {
        var t = Task.Run(body);
        if (!t.Wait(limit)) Assert.Fail(what + " did not finish within " + limit.TotalSeconds + " s (a hang?)");
    }

    static readonly TimeSpan Limit = TimeSpan.FromSeconds(Deep ? 3600 : 120);

    void Check(Report rep, string what)
    {
        output.WriteLine(what + ": " + rep.Frames + " frames, " + rep.Parsed + " parsed, " + rep.Rejected + " rejected by the parser, "
            + rep.NeedsParts + " not writable (a needed part missing); the most allocated per input byte: "
            + rep.WorstAllocRatio.ToString("0.0") + "x (" + rep.WorstAlloc + ")");
        Assert.True(rep.Problems.Count == 0, what + ": " + rep.Problems.Count + (rep.Problems.Count >= 40 ? "+" : "") + " problems in " + rep.Frames + " frames:\n" + string.Join("\n", rep.Problems));
        Assert.True(rep.Parsed > rep.Frames / 3, what + ": only " + rep.Parsed + " of " + rep.Frames + " frames got past the parser");
    }

    // ================================================================ the message suites

    // random frames: the class's own type (or none, or another), every field it reads given a random JSON value of any kind (or left out),
    // unknown fields, some keys twice
    [Fact]
    public void Random_fields_never_break_Read_or_the_round_trip()
    {
        var rep = new Report();
        int per = N(150, 3000);
        WithinTime(Limit, "random fields", () =>
        {
            foreach (var msg in Messages())
            {
                var keys = KeysOf(msg, Sample(msg));
                int seed = Stable(msg.Class.Name);
                var g = new Gen(seed);
                for (int i = 0; i < per; i++)
                {
                    var fields = new List<(string, string)>();
                    int tk = g.R.Next(10);
                    if (tk < 7) fields.Add(("t", "\"" + msg.Type + "\"")); else if (tk < 9) fields.Add(("t", g.Value()));
                    foreach (var k in keys) if (k != "t" && g.R.Next(10) < 7) fields.Add((JsonSerializer.Serialize(k), g.Value()));
                    int extra = g.R.Next(3);
                    for (int e = 0; e < extra; e++) fields.Add(("\"x" + g.R.Next(1000) + "\"", g.Value()));
                    Run(msg, g.Obj(fields, dupes: g.R.Next(8) == 0), msg.Class.Name + " seed " + seed + " #" + i, rep);
                }
            }
        });
        Check(rep, "random fields");
    }

    // real frames (each class's filled-in sample) mutated: fields flipped, removed, duplicated, renamed, at every depth
    [Fact]
    public void Mutated_real_frames_never_break_Read_or_the_round_trip()
    {
        var rep = new Report();
        int per = N(150, 3000);
        WithinTime(Limit, "mutated frames", () =>
        {
            foreach (var msg in Messages())
            {
                var sample = Sample(msg);
                Run(msg, sample.ToJsonString(), msg.Class.Name + " sample", rep);
                int seed = Stable(msg.Class.Name) ^ 0x6d75;
                var g = new Gen(seed);
                for (int i = 0; i < per; i++) Run(msg, Mutate(sample, g), msg.Class.Name + " seed " + seed + " #" + i, rep);
            }
        });
        Check(rep, "mutated frames");
    }

    // every field the class reads, set to every edge value in turn (type confusion, extremes, unicode, depth around the limit, 1 MB)
    [Fact]
    public void Every_field_takes_every_edge_value()
    {
        var edge = new List<string>
        {
            "null", "true", "false", "0", "-0", "-1", "0.5", "-0.5", "1e2", "2147483648", "-2147483649", "9223372036854775808", "-9223372036854775809",
            "18446744073709551616", "123456789012345678901234567890", "1e308", "1e309", "-1e309", "1e-400", "5e-324",
            "\"\"", "\"x\"", "\"12\"", "\"true\"", "\"NaN\"", @"""\u202e\u2066\u200b\ufeff""", @"""\ud83d\ude00""", @"""\u0000\u001b""",
            "[]", "{}", "[null]", "[1,\"a\",true,null,{},[]]", "[{}, {\"t\":1}]", "{\"t\":\"ping\"}", "[1.5, -1e309, 1e400]",
        };
        edge.AddRange(LoneSurrogates.Select(x => "\"" + x + "\""));
        var g = new Gen(0xed6e);
        foreach (var d in new[] { 1, 32, 62, 63, 64, 65, 200, 2000 }) edge.Add(g.Nest(d));
        edge.Add("\"" + new string('a', 1 << 20) + "\"");
        edge.Add("[" + string.Join(",", Enumerable.Repeat("{}", 50_000)) + "]");
        edge.Add("{" + string.Join(",", Enumerable.Range(0, 20_000).Select(i => "\"k" + i + "\":" + i)) + "}");
        var rep = new Report();
        WithinTime(Limit, "edge values", () =>
        {
            foreach (var msg in Messages())
            {
                var sample = Sample(msg);
                var keys = KeysOf(msg, sample);
                for (int ki = 0; ki < keys.Count; ki++)
                    for (int vi = 0; vi < edge.Count; vi++)
                    {
                        var v = edge[vi];
                        // the big values on every third field only in normal mode (they dominate the time)
                        if (!Deep && v.Length > 100_000 && (ki + vi) % 3 != 0) continue;
                        var o = (JsonObject)sample.DeepClone();
                        o.Remove(keys[ki]);
                        var rest = o.ToJsonString();
                        var text = "{" + JsonSerializer.Serialize(keys[ki]) + ":" + v + (rest.Length == 2 ? "}" : "," + rest[1..]);
                        Run(msg, text, msg.Class.Name + "." + keys[ki] + " = edge #" + vi, rep);
                    }
                Run(msg, "{}", msg.Class.Name + " {}", rep);
                Run(msg, "{\"t\":\"" + msg.Type + "\"}", msg.Class.Name + " type only", rep);
            }
        });
        Check(rep, "edge values");
    }

    // ================================================================ the parser boundary

    // what LodgeJson.Parse must take, worked out independently: valid UTF-8 (and for text: valid UTF-16), JSON as LodgeJson.Rules says
    // (strict syntax, MaxDepth 64, no key twice), an object at the top, and no lone surrogate in any string or key
    static bool Acceptable(byte[] utf8)
    {
        if (!System.Text.Unicode.Utf8.IsValid(utf8)) return false;
        try
        {
            using var doc = JsonDocument.Parse(utf8, LodgeJson.Rules);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            return NoLoneSurrogate(doc.RootElement);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }   // InvalidOperationException: the dupe check met a lone surrogate key
    }

    static bool NoLoneSurrogate(JsonElement e)
    {
        static bool Ok(string s) { for (int i = 0; i < s.Length; i++) { if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; } if (char.IsSurrogate(s[i])) return false; } return true; }
        try
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) if (!Ok(p.Name) || !NoLoneSurrogate(p.Value)) return false; return true;
                case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) if (!NoLoneSurrogate(x)) return false; return true;
                case JsonValueKind.String: return Ok(e.GetString()!);
                default: return true;
            }
        }
        catch (InvalidOperationException) { return false; }
    }

    static bool ValidUtf16(string s) { for (int i = 0; i < s.Length; i++) { if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; } if (char.IsSurrogate(s[i])) return false; } return true; }

    [Fact]
    public void Parse_never_throws_and_rejects_exactly_what_the_rules_refuse()
    {
        var g = new Gen(0x9a75e);
        int n = N(3000, 60_000);
        int accepted = 0, refused = 0;
        for (int i = 0; i < n; i++)
        {
            string text = (i % 6) switch
            {
                0 => g.Value(),
                1 => "{\"a\":" + g.Nest(g.R.Next(55, 75)) + "}",
                2 => Garble(g.Obj(new[] { ("t", "\"msg\""), ("text", g.Str()) }), g.R),
                3 => "{\"t\":" + g.Value() + "}",
                4 => g.Obj(new[] { ("t", "\"msg\""), ("k", g.Value()), (g.R.Next(3) == 0 ? "k" : "j", g.Value()) }),   // sometimes a key twice
                _ => new string(Enumerable.Range(0, g.R.Next(0, 200)).Select(_ => (char)g.R.Next(0, 0x80)).ToArray()),
            };
            bool want = ValidUtf16(text) && Acceptable(Encoding.UTF8.GetBytes(text));
            bool got = LodgeJson.Parse(text) != null;
            Assert.True(want == got, "#" + i + " text: rules say " + want + ", Parse " + got + ": " + Clip(text));
            if (got) accepted++; else refused++;
            // the same as bytes, sometimes with a byte changed (invalid UTF-8 included)
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > 0 && g.R.Next(2) == 0) bytes[g.R.Next(bytes.Length)] = (byte)g.R.Next(0x80, 0x100);
            want = Acceptable(bytes);
            Assert.True(want == (LodgeJson.Parse(bytes) != null), "#" + i + " bytes: rules say " + want + ": " + Clip(Encoding.UTF8.GetString(bytes)));
            Assert.Equal(want, LodgeJson.Parse(bytes, 0, bytes.Length) != null);
            // and what parsed reads and writes without an exception
            if (LodgeJson.Parse(bytes) is JsonObject o) { foreach (var k in new LodgeJsonFields(o).Keys()) { new LodgeJsonFields(o).Str(k); new LodgeJsonFields(o).Num(k); } LodgeJson.Serialize(o); }
        }
        Assert.True(accepted > n / 10 && refused > n / 10, accepted + " accepted, " + refused + " refused - the generator should give plenty of both");
        Assert.Null(LodgeJson.Parse((string)null));
        Assert.Null(LodgeJson.Parse((byte[])null));
        Assert.Null(LodgeJson.Parse("[1]"));
        Assert.Null(LodgeJson.Parse("\"x\""));
        // the documented limit: a depth of 64 parses, 65 doesn't
        var at64 = "{\"a\":" + string.Concat(Enumerable.Repeat("[", 63)) + "1" + string.Concat(Enumerable.Repeat("]", 63)) + "}";
        Assert.NotNull(LodgeJson.Parse(at64));
        var at65 = "{\"a\":" + string.Concat(Enumerable.Repeat("[", 64)) + "1" + string.Concat(Enumerable.Repeat("]", 64)) + "}";
        Assert.Null(LodgeJson.Parse(at65));
        // a frame at the limit serializes again (the server writes stored children back out)
        Assert.Equal(at64, LodgeJson.Serialize(LodgeJson.Parse(at64)));
    }

    static string Garble(string s, Random r)
    {
        var c = s.ToCharArray();
        int n = r.Next(1, 4);
        const string junk = "{}[]\",:\\ \u0000x1";
        for (int i = 0; i < n && c.Length > 0; i++) c[r.Next(c.Length)] = junk[r.Next(junk.Length)];
        return new string(c);
    }

    // the lenient readers under every Read: never an exception, whatever the value
    [Fact]
    public void The_lenient_readers_never_throw()
    {
        var g = new Gen(0x1e41);
        var problems = new List<string>();
        int n = N(4000, 80_000);
        for (int i = 0; i < n; i++)
        {
            var text = "{\"v\":" + g.Value() + "}";
            var o = LodgeJson.Parse(text);
            if (o == null) continue;
            var f = new LodgeJsonFields(o);
            try
            {
                f.Has("v"); f.IsNull("v"); f.Str("v"); f.Int("v"); f.Long("v"); f.Bool("v"); f.Child("v"); f.List("v"); f.Longs("v"); f.Strs("v");
                f.RawChild("v"); f.RawList("v"); f.Keys(); f.Num("v"); f.Nums("v"); f.IsFalse("v"); f.BoolOrNull("v"); f.LongOrNull("v"); f.IntOrNull("v");
                LodgeJson.BoolOrNull(o, "v"); LodgeJson.Number(o["v"]);
                if (f.Child("v") is ILodgeFields c) foreach (var k in c.Keys()) { c.Str(k); c.Long(k); c.Child(k); }
            }
            catch (Exception e) { if (problems.Count < 20) problems.Add("#" + i + " " + e.GetType().Name + ": " + e.Message + "\n  " + Clip(text)); }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // ================================================================ the signed formats

    [Fact]
    public void B64_never_throws_and_round_trips()
    {
        var r = new Random(0xb64);
        const string alpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_+/=\u0000 \n😀é";
        int n = N(20_000, 400_000);
        for (int i = 0; i < n; i++)
        {
            var len = r.Next(8) == 0 ? r.Next(4000, 5000) : r.Next(0, 80);
            var s = new string(Enumerable.Range(0, len).Select(_ => alpha[r.Next(alpha.Length)]).ToArray());
            B64.FromUrl(s); B64.FromStd(s); B64.FromUrl(s, 0); B64.FromStd(s, int.MaxValue);
            var bytes = new byte[r.Next(0, 100)]; r.NextBytes(bytes);
            Assert.Equal(bytes, B64.FromUrl(B64.Url(bytes)) ?? Array.Empty<byte>());
            Assert.Equal(bytes, B64.FromStd(Convert.ToBase64String(bytes)) ?? Array.Empty<byte>());
        }
        Assert.Null(B64.FromUrl(null)); Assert.Null(B64.FromStd(null)); Assert.Equal("", B64.Url(null));
        Assert.Null(B64.FromUrl(new string('A', 4097)));
    }

    // flip, cut, extend or replace every part of a valid signature, the data or the key: never an exception, never valid
    [Fact]
    public void Es256_never_accepts_a_mutated_signature_and_never_throws()
    {
        var r = new Random(0xe5256);
        using var key = Es256.NewKey();
        var p = key.ExportParameters(false);
        var spki = key.ExportSubjectPublicKeyInfo();
        using var pub = Es256.FromSpki(B64.Url(spki), out _);
        using var pt = Es256.FromPoint(p.Q.X, p.Q.Y);
        Assert.NotNull(pub); Assert.NotNull(pt);
        var data = Encoding.UTF8.GetBytes("elans-test\nmessage\n" + new string('x', 100));
        var sig = Es256.Sign(key, data);
        Assert.True(Es256.Verify(pub, data, sig));
        int n = N(1500, 30_000);
        for (int i = 0; i < n; i++)
        {
            var s = (byte[])sig.Clone();
            var d = (byte[])data.Clone();
            switch (r.Next(6))
            {
                case 0: s[r.Next(s.Length)] ^= (byte)(1 << r.Next(8)); break;
                case 1: d[r.Next(d.Length)] ^= (byte)(1 << r.Next(8)); break;
                case 2: s = s.Take(r.Next(s.Length)).ToArray(); break;
                case 3: s = s.Concat(new byte[r.Next(1, 10)]).ToArray(); break;
                case 4: r.NextBytes(s); break;
                default: s = new byte[64]; if (r.Next(2) == 0) Array.Fill(s, (byte)0xff); break;
            }
            Assert.False(Es256.Verify(pub, d, s), "mutation #" + i + " verified");
            Assert.False(Es256.Verify(pt, d, s), "mutation #" + i + " verified (point key)");
            Assert.False(Es256.Verify(pub, Encoding.UTF8.GetString(d) + "x", s));
            // keys from mutated / random input: null or a key, never an exception; a changed key never verifies the signature
            var sp = (byte[])spki.Clone();
            sp[r.Next(sp.Length)] ^= (byte)(1 << r.Next(8));
            using (var k = Es256.FromSpki(B64.Url(sp), out _)) if (k != null) Assert.False(Es256.Verify(k, data, sig) && !k.ExportSubjectPublicKeyInfo().SequenceEqual(spki), "a mutated SPKI verified the signature");
            var junk = new byte[r.Next(0, 210)]; r.NextBytes(junk);
            using (Es256.FromSpki(B64.Url(junk), out _)) { }
            using (Es256.FromSpki(r.Next(2) == 0 ? null : "!" + B64.Url(junk), out _)) { }
            var x = (byte[])p.Q.X.Clone(); var y = (byte[])p.Q.Y.Clone();
            if (r.Next(2) == 0) x[r.Next(32)] ^= (byte)(1 << r.Next(8)); else y = new byte[r.Next(0, 40)];
            using (var k = Es256.FromPoint(x, y)) if (k != null) Assert.False(Es256.Verify(k, data, sig), "a mutated point verified the signature");
        }
        Assert.False(Es256.Verify(null, data, sig)); Assert.False(Es256.Verify(pub, (byte[])null, sig)); Assert.False(Es256.Verify(pub, data, null));
        Assert.False(Es256.Verify(pub, (string)null, sig));
        Assert.Null(Es256.FromPoint(null, null));
    }

    [Fact]
    public void ReleaseSig_never_accepts_a_mutated_signature_or_manifest_and_never_throws()
    {
        var r = new Random(0x5e1ea5e);
        using var key = Es256.NewKey();
        var p = key.ExportParameters(false);
        var k = new ReleaseSig.Key { Id = "k1", X = Convert.ToBase64String(p.Q.X), Y = Convert.ToBase64String(p.Q.Y) };
        var manifest = Encoding.UTF8.GetBytes("{\"version\":\"3.1.1\",\"files\":[]}");
        var good = ReleaseSig.Make(key, "k1", manifest);
        var goodSig = good.Split('\n')[2];
        Assert.Equal(ReleaseSig.Result.Valid, ReleaseSig.Check(manifest, good, new[] { k }, true, out _));
        int n = N(3000, 60_000);
        for (int i = 0; i < n; i++)
        {
            var sig = good.ToCharArray();
            var m = (byte[])manifest.Clone();
            var keys = new[] { k };
            switch (r.Next(7))
            {
                case 0: { int at = r.Next(sig.Length); sig[at] = (char)(sig[at] ^ (1 << r.Next(7))); break; }
                case 1: m[r.Next(m.Length)] ^= (byte)(1 << r.Next(8)); break;
                case 2: sig = sig.Take(r.Next(sig.Length)).ToArray(); break;
                case 3: sig = (new string(sig) + (r.Next(2) == 0 ? "extra\n" : "\u0000")).ToCharArray(); break;
                case 4: keys = new[] { new ReleaseSig.Key { Id = "k1", X = k.Y, Y = k.X } }; break;
                case 5: keys = new[] { new ReleaseSig.Key { Id = "k1", X = r.Next(2) == 0 ? null : "!!", Y = "" } }; break;
                default: sig = Enumerable.Range(0, r.Next(0, 300)).Select(_ => (char)r.Next(0, 0x300)).ToArray(); break;
            }
            var text = new string(sig);
            var res = ReleaseSig.Check(m, text, keys, true, out var why);
            Assert.NotNull(why);
            // the same signature in other words (a line break changed into \r\n, a blank line) may still be valid - never with a changed
            // manifest, key or signature
            if (res == ReleaseSig.Result.Valid)
            {
                Assert.True(m.SequenceEqual(manifest) && keys[0] == k, "mutation #" + i + " accepted: " + text.Replace("\n", "\\n"));
                // base64 has spare bits in its last character before "==": another character there can carry the same 64 bytes
                Assert.Equal(Convert.FromBase64String(goodSig), Convert.FromBase64String(text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)[2]));
            }
            ReleaseSig.Check(m, text, null, false, out _);
            ReleaseSig.Check(null, text, keys, false, out _);
        }
        Assert.Equal(ReleaseSig.Result.Missing, ReleaseSig.Check(manifest, null, new[] { k }, false, out _));
        Assert.Equal(ReleaseSig.Result.Invalid, ReleaseSig.Check(manifest, null, new[] { k }, true, out _));
    }

    [Fact]
    public void PublicUrl_never_throws_and_what_it_accepts_is_stable_and_public()
    {
        var r = new Random(0x0b11c);
        string[] parts =
        {
            "https://", "http://", "HTTPS://", "ftp://", "//", "lodge.example.com", "example.com", "localhost", "a.local", "x.internal", "127.0.0.1", "10.0.0.1",
            "[::1]", "[fe80::1]", "[2001:db8::1]", "[::ffff:127.0.0.1]", "[2a00:1450::1]", "8.8.8.8", "0x7f.1", "2130706433", "%6c%6f", "xn--nxasmq6b.com", "bücher.de",
            ":443", ":80", ":8443", ":0", ":99999", ":-1", "/lodge", "/", "//x", "/../", "/%2f", "?q=1", "#f", "user:pw@", "@", "\u0000", " ", "\t", "\u202e",
            "\u200b", "😀", "\\", "..", ".", "a", "-", "_",
        };
        int n = N(20_000, 400_000);
        for (int i = 0; i < n; i++)
        {
            var s = string.Concat(Enumerable.Range(0, r.Next(0, 7)).Select(_ => parts[r.Next(parts.Length)]));
            if (r.Next(20) == 0) s = "https://" + new string('a', r.Next(150, 400)) + ".com";
            bool ok;
            string norm; PublicUrl.Problem why;
            try { ok = PublicUrl.TryNormalize(s, out norm, out why); }
            catch (Exception e) { Assert.Fail("#" + i + " TryNormalize threw " + e.GetType().Name + " on \"" + string.Concat(s.Select(c => c < 0x20 || c > 0x7e ? "\\u" + ((int)c).ToString("x4") : c.ToString())) + "\": " + e); return; }
            if (ok)
            {
                Assert.Equal(PublicUrl.Problem.None, why);
                Assert.StartsWith("https://", norm);
                Assert.True(PublicUrl.TryNormalize(norm, out var again, out _), "not accepted again: " + norm + " (from " + s + ")");
                Assert.Equal(norm, again);
                Assert.False(string.IsNullOrEmpty(PublicUrl.HostOf(norm)));
            }
            else { Assert.Null(norm); Assert.NotEqual(PublicUrl.Problem.None, why); }
        }
        Assert.False(PublicUrl.TryNormalize(null, out _, out _));
        Assert.False(PublicUrl.IsPublic(null));
    }

    // found by the fuzzer: a host IDNA refuses (a zero-width space, a bidi control) made Uri.IdnHost throw UriFormatException out of
    // TryNormalize (the Lodge's admin.cairn address check, the Cairn's join check) and HostOf; and Uri.TryCreate itself threw
    // IndexOutOfRangeException on "//", a bidi control, "//" and a punycode host
    [Theory]
    [InlineData("https://\u200b")]
    [InlineData("HTTPS://\u200b/lodge")]
    [InlineData("https://\u202e")]
    [InlineData(" //\u202e//xxn--nxasmq6b.com")]
    public void Regression_a_host_IDNA_refuses_is_no_host_not_an_exception(string s)
    {
        Assert.False(PublicUrl.TryNormalize(s, out var norm, out var why));
        Assert.Null(norm);
        Assert.NotEqual(PublicUrl.Problem.None, why);
        Assert.Equal("", PublicUrl.HostOf(s));
    }

    // found by the deep fuzz: a zero-width space before 127.0.0.1 made the host a DNS "name" (so the IP rules were skipped) that IDNA then
    // turned into 127.0.0.1 - "https://<ZWSP>127.0.0.1" passed as a public address and normalized to https://127.0.0.1. Full-width digits
    // did the same. The Cairn calls the address it is given: a loopback / private one must never pass. And what passes must pass again as
    // itself ("https://<ZWSP>_xn--nxasmq6b.com" came out as a host the rules refuse).
    [Fact]
    public void Regression_a_name_that_IDNA_turns_into_a_private_IP_is_refused()
    {
        string zw = ((char)0x200B).ToString();
        string full127 = new string(new[] { (char)0xFF11, (char)0xFF12, (char)0xFF17 });   // full-width 1 2 7
        foreach (var s in new[] { "https://" + zw + "127.0.0.1", "https://" + zw + "10.0.0.1/lodge", "https://" + zw + "192.168.1.1:8443", "https://" + full127 + ".0.0.1", "https://127" + zw + ".0.0.1", "https://" + zw + "_xn--nxasmq6b.com" })
        {
            Assert.False(PublicUrl.TryNormalize(s, out var norm, out var why), s.Replace(zw, "<ZWSP>") + " passed as " + norm);
            Assert.Null(norm);
        }
        // a public one stays fine, written as the plain IP
        Assert.True(PublicUrl.TryNormalize("https://" + zw + "8.8.8.8", out var ok, out _));
        Assert.Equal("https://8.8.8.8", ok);
    }

    [Fact]
    public void Cairn_format_checks_never_throw()
    {
        var g = new Gen(0xca1e, big: false);
        int n = N(5000, 100_000);
        for (int i = 0; i < n; i++)
        {
            string s;
            try { s = JsonSerializer.Deserialize<string>(g.Str()); } catch (Exception e) when (e is JsonException or InvalidOperationException) { continue; }
            CairnFormats.ValidNonce(s); CairnFormats.ValidKeyId(s); CairnFormats.TryTime(s, out _);
            CairnFormats.TryTime(g.Num(), out _);
        }
        Assert.False(CairnFormats.ValidNonce(null)); Assert.False(CairnFormats.ValidKeyId(null)); Assert.False(CairnFormats.TryTime(null, out _));
    }
}
