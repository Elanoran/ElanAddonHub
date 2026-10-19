using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elans.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Lodge.Tests;

// The biggest frames a Lodge really sends stay well inside the counts an Outpost's LodgeJson.Parse allows (MaxItems per array / object,
// MaxValues, MaxContainers): built with the server's own caps, so raising a cap past the counts fails here, not in someone's Outpost.
public class FrameLimitsTests
{
    readonly ITestOutputHelper output;
    public FrameLimitsTests(ITestOutputHelper output) { this.output = output; }

    // the largest array / object, the values and the objects / arrays in a frame (counted as LodgeJson counts them)
    static (int Items, int Values, int Containers) Counts(string json)
    {
        var r = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        var open = new Stack<int>();
        int items = 0, values = 0, containers = 0;
        while (r.Read())
        {
            if (r.TokenType == JsonTokenType.PropertyName) continue;
            if (r.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) { items = Math.Max(items, open.Pop()); continue; }
            values++;
            if (open.Count > 0) open.Push(open.Pop() + 1);
            if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) { containers++; open.Push(0); }
        }
        return (items, values, containers);
    }

    void Within(string what, string json, double margin)
    {
        var (items, values, containers) = Counts(json);
        output.WriteLine($"{what}: {json.Length} chars, largest array / object {items}, {values} values, {containers} objects / arrays");
        Assert.NotNull(LodgeJson.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.True(items * margin <= LodgeJson.MaxItems, what + ": " + items + " items in one array / object");
        Assert.True(values * margin <= LodgeJson.MaxValues, what + ": " + values + " values");
        Assert.True(containers * margin <= LodgeJson.MaxContainers, what + ": " + containers + " objects / arrays");
        Assert.True(json.Length <= LodgeJson.MaxFrameFromLodge, what + ": " + json.Length + " chars");
    }

    [Fact]
    public void Flights_routes_at_their_caps()
    {
        // FlightRouteStore.MergedJson: every leg published, each with the longest line (with heights), the biggest numbers
        var msg = new FlightsRoutesDataMsg { At = 1_790_000_000 };
        var pts = Enumerable.Range(0, FlightRoutes.MaxPoints).Select(i => new FlightRoutes.P { X = -12345.6 + i, Y = 23456.7 - i, Z = 123.4, HasZ = true }).ToArray();
        for (int i = 0; i < FlightRoutes.MaxLegs; i++)
            msg.Routes.Add(new("Horde|" + (1000 + i) + ">" + (5000 + i), new FlightRoute { M = 1, P = FlightRoutes.FlatNums(pts), T = 123.4, N = 20, K = 20 }));
        Within("flights.routes.data", LodgeJson.Build(msg.Write).ToJsonString(), 1.5);
    }

    [Fact]
    public void Flights_data_at_its_caps()
    {
        var msg = new FlightsDataMsg { At = 1_790_000_000 };
        for (int i = 0; i < Flights.MaxLegs; i++) msg.Legs.Add(new("Horde|" + (1000 + i) + ">" + (5000 + i), new FlightLeg { T = 123.4, N = 20 }));
        for (int i = 0; i < Flights.MaxNames; i++) msg.Names.Add(new((1000 + i).ToString(), new string('N', Flights.MaxNameLength)));
        Within("flights.data", LodgeJson.Build(msg.Write).ToJsonString(), 3);
    }

    // an atlas.data page of the smallest records there are (a kill counter of one kill, no drops): the most records one page can hold
    [Fact]
    public void Atlas_pages_of_the_smallest_records()
    {
        using var dir = new TempDir();
        var store = new AtlasStore(dir.Path, T.Log, new FakeClock());
        int id = 1, shared = 0;
        lock (store.Gate)
            for (int m = 0; m < 3; m++)
                for (int s = 0; s < Atlas.MaxKillSources / Atlas.MaxKillsPerShare; s++)
                {
                    var kills = string.Join(",", Enumerable.Range(0, Atlas.MaxKillsPerShare).Select(_ => "\"" + id++ + "\":{\"n\":1}"));
                    shared += store.Share("m" + m, "M" + m, JsonNode.Parse("{\"kills\":{" + kills + "}}")!.AsObject()).Accepted;   // as received
                }
        Assert.True(shared > 15_000, shared + " shared");
        long since = 0; string? epoch = null; int pages = 0, records = 0;
        while (true)
        {
            JsonObject page;
            lock (store.Gate) page = store.Get(since, epoch, x => x);
            records += ((JsonObject)page["kills"]!).Count;
            Within("atlas.data page " + ++pages, page.ToJsonString(), 2);
            since = (long)page["rev"]!; epoch = (string)page["epoch"]!;
            if (page["more"] is null) break;
            Assert.True(pages < 50);
        }
        Assert.Equal(shared, records);
    }
}
