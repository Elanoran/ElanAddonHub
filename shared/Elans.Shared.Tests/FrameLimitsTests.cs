using System.Diagnostics;
using System.Text;
using Elans.Shared;
using Xunit.Abstractions;

namespace Elans.Shared.Tests;

// LodgeJson's counts: at most MaxItems per array / object, MaxValues values and MaxContainers objects / arrays in a frame. A frame over
// them is refused by Parse as a whole (like one nested too deep), fast and without building anything; what Parse takes costs a bounded
// amount to read. The biggest real frames the Lodge builds are checked against the same counts in Lodge.Tests (FrameLimitsTests).
public class FrameLimitsTests
{
    readonly ITestOutputHelper output;
    public FrameLimitsTests(ITestOutputHelper output) { this.output = output; }

    static string Arr(int n, string item) => "[" + string.Join(",", Enumerable.Repeat(item, n)) + "]";

    // { "k0": [item x n0], "k1": ... } - the root, one array per key, the items
    static string Frame(IEnumerable<int> sizes, string item) => "{" + string.Join(",", sizes.Select((n, i) => "\"k" + i + "\":" + Arr(n, item))) + "}";

    static void Both(bool want, string text)
    {
        Assert.Equal(want, LodgeJson.Parse(text) != null);
        var b = Encoding.UTF8.GetBytes(text);
        Assert.Equal(want, LodgeJson.Parse(b) != null);
        Assert.Equal(want, LodgeJson.Parse(b, 0, b.Length) != null);
    }

    [Fact]
    public void Each_count_at_its_limit_parses_and_one_more_is_refused()
    {
        int max = LodgeJson.MaxItems;
        // items in one array, keys in one object
        Both(true, "{\"a\":" + Arr(max, "0") + "}");
        Both(false, "{\"a\":" + Arr(max + 1, "0") + "}");
        Both(true, "{" + string.Join(",", Enumerable.Range(0, max).Select(i => "\"k" + i + "\":0")) + "}");
        Both(false, "{" + string.Join(",", Enumerable.Range(0, max + 1).Select(i => "\"k" + i + "\":0")) + "}");
        // the limit is per array: two full ones side by side are fine
        Both(true, Frame(new[] { max, max }, "0"));

        // values in all: the root + 49 arrays of (1 + 19,999) + one of (1 + 19,998) = 1,000,000 exactly
        var full = Enumerable.Repeat(max - 1, 49).Append(max - 2).ToArray();
        Assert.Equal(LodgeJson.MaxValues, 1 + full.Sum(n => n + 1));
        Both(true, Frame(full, "0"));
        full[^1]++;
        Both(false, Frame(full, "0"));

        // objects / arrays in all: the root + 4 arrays of (1 + 19,999 empty ones) + one of (1 + 19,998) = 100,000 exactly
        var many = Enumerable.Repeat(max - 1, 4).Append(max - 2).ToArray();
        Assert.Equal(LodgeJson.MaxContainers, 1 + many.Sum(n => n + 1));
        Both(true, Frame(many, "{}"));
        many[^1]++;
        Both(false, Frame(many, "[]"));
    }

    // the fuzz finding (F1): an 8 MB frame of empty objects cost ~1.1 GB to read; now it is refused at once, without building anything
    [Fact]
    public void Regression_an_8_MB_frame_of_empty_objects_is_refused_fast_with_little_allocation()
    {
        var shapes = new[]
        {
            // one array
            "{\"t\":\"armory.chars\",\"chars\":" + Arr((LodgeJson.MaxFrameFromLodge - 40) / 3, "{}") + "}",
            // arrays under the limit each, under keys a typed Read walks (welcome.history: channel -> messages)
            "{\"t\":\"welcome\",\"history\":{" + string.Join(",", Enumerable.Range(0, 270).Select(i => "\"c" + i + "\":" + Arr(10_000, "{}"))) + "}}",
        };
        foreach (var text in shapes)
        {
            var b = Encoding.UTF8.GetBytes(text);
            Assert.True(b.Length > 7_800_000 && b.Length <= LodgeJson.MaxFrameFromLodge, b.Length + " bytes");
            LodgeJson.Parse(new byte[] { (byte)'{', (byte)'}' });                      // warm up
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var o = LodgeJson.Parse(b, 0, b.Length);
            sw.Stop();
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            output.WriteLine(b.Length + " bytes: refused in " + sw.ElapsedMilliseconds + " ms, " + alloc + " bytes allocated");
            Assert.Null(o);
            Assert.True(alloc < 64 * 1024, alloc + " bytes allocated");
            Assert.True(sw.ElapsedMilliseconds < 2000, sw.ElapsedMilliseconds + " ms");
            Assert.Null(LodgeJson.Parse(text));
        }
    }

    // what Parse takes costs a bounded amount to read, whatever the frame: the worst shapes at the caps
    [Fact]
    public void At_the_caps_a_read_costs_a_bounded_amount()
    {
        int max = LodgeJson.MaxItems;
        // 100,000 empty objects where a typed Read makes a message of each (the most per byte); 1,000,000 numbers in route lines
        var objects = "{\"t\":\"welcome\",\"history\":{" + string.Join(",", Enumerable.Range(0, 5).Select(i => "\"c" + i + "\":" + Arr(max - 2, "{}"))) + "}}";
        var numbers = "{\"t\":\"flights.routes.data\",\"routes\":{" + string.Join(",", Enumerable.Range(0, 49).Select(i => "\"r" + i + "\":{\"m\":1,\"p\":" + Arr(max - 6, "0") + "}")) + "}}";
        foreach (var (text, read, limit) in new (string, Func<ILodgeFields, object>, long)[]
        {
            (objects, f => WelcomeMsg.Read(f), 80L << 20),
            (numbers, f => FlightsRoutesDataMsg.Read(f), 160L << 20),
        })
        {
            var b = Encoding.UTF8.GetBytes(text);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var o = LodgeJson.Parse(b);
            Assert.NotNull(o);
            read(new LodgeJsonFields(o));
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            output.WriteLine(b.Length + " bytes: " + (alloc >> 20) + " MB to parse and read");
            Assert.True(alloc < limit, (alloc >> 20) + " MB");
            Assert.True(alloc < FuzzTests.AllocCeiling);
        }
    }

    // the realistic welcome and a history page are far inside the counts (the flights / atlas frames: Lodge.Tests FrameLimitsTests)
    [Fact]
    public void The_realistic_welcome_and_history_page_parse()
    {
        Assert.NotNull(LodgeJson.Parse(Encoding.UTF8.GetBytes(LodgeJsonPerfTests.Welcome())));
        Assert.NotNull(LodgeJson.Parse(Encoding.UTF8.GetBytes(LodgeJsonPerfTests.HistoryPage())));
    }

    // a file the program wrote itself (the Outpost's merged Atlas cache) may hold more than a frame; every other rule still holds
    [Fact]
    public void ParseStored_takes_more_than_a_frame_but_keeps_the_other_rules()
    {
        var big = Encoding.UTF8.GetBytes("{\"a\":" + Arr(LodgeJson.MaxItems + 1, "0") + "}");
        Assert.Null(LodgeJson.Parse(big));
        Assert.NotNull(LodgeJson.ParseStored(big));
        Assert.Null(LodgeJson.ParseStored(Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}")));
        Assert.Null(LodgeJson.ParseStored(Encoding.UTF8.GetBytes("{\"a\":\"\\ud800\"}")));
        Assert.Null(LodgeJson.ParseStored(Encoding.UTF8.GetBytes("[1]")));
        Assert.Null(LodgeJson.ParseStored(new byte[] { (byte)'{', 0xff, (byte)'}' }));
        Assert.Null(LodgeJson.ParseStored(null));
    }
}
