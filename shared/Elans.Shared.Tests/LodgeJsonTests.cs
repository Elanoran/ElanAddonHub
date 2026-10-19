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
