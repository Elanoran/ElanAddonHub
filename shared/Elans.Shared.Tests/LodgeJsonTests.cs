using System.Text;
using System.Text.Json.Nodes;
using Elans.Shared;
using Elans.Shared.Tests.Frozen;

namespace Elans.Shared.Tests;

// The shared Lodge JSON engine (shared/Elans.Shared/Lodge/LodgeJson.cs) beyond the leniency table: the converters between its nodes and the
// plain dictionaries the Outpost's validators read.
public class LodgeJsonTests
{
    // deep equality of two values of the plain model (Dictionary / List / double / string / bool / null), doubles bit for bit (-0 too)
    static bool Same(object a, object b) => (a, b) switch
    {
        (null, null) => true,
        (Dictionary<string, object> x, Dictionary<string, object> y) => x.Count == y.Count && x.Keys.SequenceEqual(y.Keys) && x.All(kv => Same(kv.Value, y[kv.Key])),
        (List<object> x, List<object> y) => x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second)),
        (double x, double y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y),
        (string x, string y) => x == y,
        (bool x, bool y) => x == y,
        _ => false,
    };

    [Fact]
    public void ToPlain_gives_what_the_old_Outpost_reader_gave_for_every_frame_both_read()
    {
        // every input of the leniency table that the old Outpost and the shared engine both read: the same plain data, value for value
        int compared = 0;
        foreach (var (name, frame) in JsonLeniencyTests.Cases())
        {
            if (frame.Length > 150_000) continue;                       // the old reader can't take the very deep one (stack overflow)
            var old = OldFrames.Outpost(frame);
            var now = LodgeJson.Parse(frame);
            if (old == null || now == null) continue;
            object plain;
            try { plain = LodgeJson.ToPlain(now); } catch (Exception) { continue; }   // unreadable in the shared engine (duplicate keys, lone surrogates)
            if (name.StartsWith("duplicate", StringComparison.Ordinal) || name.Contains("lone")) continue;
            // a number too big for a double: null now, not Infinity (JsonLeniencyTests.NumberChanges)
            if (JsonLeniencyTests.NumberChanges.ContainsKey(name)) { Assert.DoesNotContain("Infinity", LodgeJson.WritePlain(plain)); Assert.Contains("null", LodgeJson.WritePlain(plain)); continue; }
            Assert.True(Same(old, plain), name + ": the plain data differs");
            compared++;
        }
        Assert.True(compared > 40, "only " + compared + " inputs compared");
    }

    [Fact]
    public void ToPlain_reads_nodes_built_by_code_like_parsed_ones()
    {
        // a node built in code holds a long / int, not a parsed number: ToPlain still gives the double the old reader would have
        var built = new JsonObject { ["l"] = 5L, ["i"] = 3, ["d"] = 0.5, ["s"] = "x", ["b"] = true, ["n"] = null, ["a"] = new JsonArray(1L, "y", null) };
        var plain = (Dictionary<string, object>)LodgeJson.ToPlain(built);
        Assert.True(Same(plain, OldOutpostJson.Parse("{\"l\":5,\"i\":3,\"d\":0.5,\"s\":\"x\",\"b\":true,\"n\":null,\"a\":[1,\"y\",null]}")));
    }

    static readonly object[] PlainSamples =
    {
        new Dictionary<string, object> { ["a"] = 1, ["b"] = 2L, ["c"] = 2.5, ["d"] = -0.0, ["e"] = "x\"\\\n\u0001é\U0001F600", ["f"] = true, ["g"] = null },
        new Dictionary<string, object> { ["list"] = new List<object> { 1.0, "two", new Dictionary<string, object> { ["k"] = new List<object>() } }, ["empty"] = new Dictionary<string, object>() },
        new Dictionary<string, object> { ["big"] = 9007199254740993L, ["tiny"] = 1e-300, ["huge"] = 1e300, ["neg"] = int.MinValue },
        new List<object> { new List<object> { new List<object> { 1 } } },
        new Dictionary<string, object> { ["ints"] = new[] { 1, 2, 3 }, ["strs"] = new[] { "a" }, ["mixed"] = new object[] { 1, null, "x" } },
    };

    [Fact]
    public void WritePlain_writes_what_the_old_writer_wrote_as_far_as_any_reader_can_tell()
    {
        foreach (var sample in PlainSamples)
        {
            var mine = LodgeJson.WritePlain(sample);
            var old = OldOutpostJson.Write(sample);
            // both parse (with the old reader) to the same data; the bytes differ only in escaping (this engine escapes non-ASCII)
            Assert.True(Same(OldOutpostJson.Parse(old), OldOutpostJson.Parse(mine)), mine + " vs " + old);
            Assert.True(mine.All(c => c < 0x80), "the shared engine writes ASCII only: " + mine);
        }
        // values the old writer could not write as JSON: a non-finite double is null now (it was NaN / Infinity, which no Lodge parses)
        Assert.Equal("{\"x\":null,\"y\":null}", LodgeJson.WritePlain(new Dictionary<string, object> { ["x"] = double.NaN, ["y"] = double.PositiveInfinity }));
        // a lone surrogate goes out as U+FFFD (the old writer wrote it raw, which UTF-8 turned into U+FFFD on the wire anyway)
        Assert.Equal("\"x\\uFFFD\"", LodgeJson.WritePlain("x\ud800"));
        // anything else as its text, as the old writer did
        Assert.Equal("\"7\"", LodgeJson.WritePlain(7m));
    }

    // ---------------------------------------------------------------- the parser's rules (LodgeJson.Rules)

    static byte[] U(string s) => Encoding.UTF8.GetBytes(s);
    static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    static bool Reads(byte[] b) => LodgeJson.Parse(b) != null;
    static bool Reads(string s) => LodgeJson.Parse(s) != null;

    [Fact]
    public void Depth_is_limited_to_MaxDepth_levels_the_frame_included()
    {
        Assert.Equal(64, LodgeJson.MaxDepth);
        string Frame(int levels) => "{\"a\":" + new string('[', levels - 1) + "1" + new string(']', levels - 1) + "}";     // the object + levels - 1 arrays
        Assert.True(Reads(U(Frame(64))));
        Assert.False(Reads(U(Frame(65))));
        Assert.False(Reads(Frame(65)));
        Assert.False(Reads(U("{\"a\":" + new string('[', 200_000) + new string(']', 200_000) + "}")));    // no stack overflow, just skipped
        // the same for objects
        var deepObj = new StringBuilder(); for (int i = 0; i < 70; i++) deepObj.Append("{\"a\":"); deepObj.Append('1').Append('}', 70);
        Assert.False(Reads(deepObj.ToString()));
    }

    [Fact]
    public void A_key_twice_skips_the_frame_at_any_level()
    {
        Assert.True(Reads("{\"a\":1,\"b\":2}"));
        Assert.False(Reads("{\"a\":1,\"a\":1}"));                              // even with the same value
        Assert.False(Reads("{\"a\":{\"b\":1,\"b\":2}}"));
        Assert.False(Reads("{\"a\":[0,[{\"b\":1,\"c\":{\"d\":1,\"d\":1}}]]}"));
        Assert.False(Reads("{\"a\":1,\"\\u0061\":2}"));                       // the same key, once escaped
        Assert.True(Reads("{\"a\":1,\"A\":2,\"a \":3}"));                      // keys are compared exactly
    }

    [Fact]
    public void Invalid_UTF8_and_lone_surrogates_skip_the_frame()
    {
        Assert.True(Reads(U("{\"a\":\"\u00e9\U0001F600\",\"\u00e9\":1}")));                       // real UTF-8, a pair
        Assert.True(Reads("{\"a\":\"\\ud83d\\ude00\",\"\\ud83d\\ude00\":1}"));                     // an escaped pair, in a value and a key
        foreach (var bad in new[]
        {
            Cat(U("{\"a\":\"x"), new byte[] { 0xFF }, U("\"}")),                 // not UTF-8 at all
            Cat(U("{\"a\":\"x"), new byte[] { 0xC3 }, U("\"}")),                 // a sequence cut short
            Cat(U("{\"a\":\""), new byte[] { 0xC0, 0xAF }, U("\"}")),            // overlong
            Cat(U("{\"a\":\""), new byte[] { 0xED, 0xA0, 0x80 }, U("\"}")),      // a surrogate encoded as UTF-8
            Cat(U("{\"a"), new byte[] { 0xFF }, U("\":1}")),                     // in a key
            Cat(U("{\"a\":[\"ok\",{\"b\":\""), new byte[] { 0xFE }, U("\"}]}")), // deep inside
        })
            Assert.False(Reads(bad), BitConverter.ToString(bad));
        foreach (var bad in new[] { "{\"a\":\"\\ud800\"}", "{\"a\":\"x\\udc00\"}", "{\"a\":\"\\ude00\\ud83d\"}", "{\"a\":\"\\ud83d\"}", "{\"\\ud800\":1}", "{\"a\":[{\"b\":\"\\udfff\"}]}" })
            Assert.False(Reads(bad), bad);
        Assert.False(Reads("{\"a\":\"x\ud800\"}"));                            // a lone surrogate in the text itself
    }

    [Fact]
    public void Only_strict_JSON_with_an_object_at_the_top_is_read()
    {
        Assert.True(Reads(" \r\n\t{\"a\":1} \n"));
        foreach (var bad in new[] { "{\"a\":1,}", "{\"a\":[1,]}", "{\"a\":1/*c*/}", "// c\n{\"a\":1}", "{\"a\":1} x", "{\"a\":1}{}", "[{\"a\":1}]", "\"a\"", "null", "", "{\"a\":NaN}", "{'a':1}", "{\"a\":01}" })
            Assert.False(Reads(bad), bad);
        Assert.False(Reads(Cat(new byte[] { 0xEF, 0xBB, 0xBF }, U("{\"a\":1}"))));                      // a BOM
        Assert.Null(LodgeJson.Parse((byte[])null)); Assert.Null(LodgeJson.Parse((string)null));
    }

    [Fact]
    public void A_frame_that_parses_reads_every_field_without_an_exception()
    {
        // every input of the leniency table: either skipped, or each member of the adapter answers (no "throws")
        foreach (var (name, frame) in JsonLeniencyTests.Cases())
        {
            var got = JsonLeniencyTests.Describe(JsonLeniencyTests.Shared, frame);
            Assert.True(got.All(x => !x.Value.StartsWith("throws", StringComparison.Ordinal)), name + ": " + string.Join("  ", got.Select(x => x.Member + "=" + x.Value)));
        }
    }

    [Fact]
    public void Both_sides_take_the_frame_limits_from_here()
    {
        Assert.Equal(64 * 1024, LodgeJson.MaxFrameToLodge);
        Assert.Equal(8 * 1024 * 1024, LodgeJson.MaxFrameFromLodge);
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "server"))) root = Path.GetDirectoryName(root);
        var hub = File.ReadAllText(Path.Combine(root, "server", "Lodge.Server", "Hub.cs"));
        var client = File.ReadAllText(Path.Combine(root, "src", "Services", "LodgeClient.cs"));
        Assert.Contains("msg.Length > LodgeJson.MaxFrameToLodge", hub);
        Assert.Contains("LodgeJson.Parse(msg.GetBuffer()", hub);
        Assert.Contains("LodgeJson.Parse(data, 0, len)", client);
        Assert.DoesNotContain("JsonNode.Parse", client);
    }

    [Fact]
    public void FromPlain_gives_nodes_that_read_like_a_received_frame()
    {
        // built from plain data with longs, ints and doubles, the node reads exactly like the same text received
        var plain = new Dictionary<string, object> { ["n"] = 5L, ["i"] = 7, ["d"] = 2.0, ["ids"] = new List<object> { 1L, 2, 3.0 }, ["c"] = new Dictionary<string, object> { ["x"] = 1 } };
        var node = (JsonObject)LodgeJson.FromPlain(plain);
        var parsed = LodgeJson.Parse(Encoding.UTF8.GetBytes(LodgeJson.WritePlain(plain)));
        foreach (var (name, read) in new (string, Func<byte[], ILodgeFields>)[] { ("node", _ => new LodgeJsonFields(node)), ("parsed", _ => new LodgeJsonFields(parsed)) })
        {
            var f = read(null);
            Assert.Equal(5.0, f.Num("n")); Assert.Equal(5L, f.Long("n")); Assert.Equal(7, f.Int("i")); Assert.Equal(2.0, f.Num("d"));
            Assert.Equal(new long[] { 1, 2, 3 }, f.Longs("ids"));          // 3.0 is written 3, as this engine writes a whole double
            Assert.NotNull(f.Child("c"));
        }
        // a code-built node (no FromPlain) would not: its long is no double to the strict number reader
        Assert.Null(new LodgeJsonFields(new JsonObject { ["n"] = 5L }).Num("n"));
        Assert.Equal(5.0, new LodgeJsonFields(LodgeJson.FromPlain(new Dictionary<string, object> { ["n"] = 5L }) as JsonObject).Num("n"));
        Assert.Null(LodgeJson.FromPlain(null));
    }
}
