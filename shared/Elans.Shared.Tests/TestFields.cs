using System.Text.Json;
using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// A plain Dictionary-backed ILodgeFields / ILodgeWriter for the typed message tests (numbers stored as written; reading accepts any
// numeric type), plus the two helpers every <Group>MessagesTests uses.
sealed class TestFields : ILodgeFields, ILodgeWriter
{
    public readonly Dictionary<string, object> D;
    public TestFields(Dictionary<string, object> d = null) { D = d ?? new(); }
    object Get(string k) => D.TryGetValue(k, out var v) ? v : null;
    public bool Has(string key) => D.ContainsKey(key);
    public bool IsNull(string key) => Get(key) == null;
    public string Str(string key) => Get(key) as string;
    public int Int(string key) => Get(key) switch { int i => i, long l => (int)l, double d => (int)d, _ => 0 };
    public long Long(string key) => Get(key) switch { int i => i, long l => l, double d => (long)d, _ => 0 };
    public bool Bool(string key) => Get(key) is true;
    public ILodgeFields Child(string key) => Get(key) is Dictionary<string, object> c ? new TestFields(c) : null;
    public IReadOnlyList<ILodgeFields> List(string key) =>
        Get(key) is List<object> l ? l.OfType<Dictionary<string, object>>().Select(x => (ILodgeFields)new TestFields(x)).ToList() : new List<ILodgeFields>();
    public IReadOnlyList<long> Longs(string key) =>
        Get(key) is List<object> l ? l.Select(x => x switch { int i => i, long n => n, double d when d == Math.Floor(d) => (long)d, _ => 0L }).ToList() : new List<long>();
    public IReadOnlyList<string> Strs(string key) => Get(key) is List<object> l ? l.OfType<string>().ToList() : new List<string>();
    public object RawChild(string key) => Get(key) as Dictionary<string, object>;
    public object RawList(string key) => Get(key) as List<object>;
    public IReadOnlyList<string> Keys() => D.Keys.ToList();
    static double? Number(object x) => x switch { int i => i, long l => l, double d => d, _ => null };
    public double? Num(string key) => Number(Get(key));
    public IReadOnlyList<double?> Nums(string key)
    {
        if (Get(key) is not List<object> a) return null;
        var l = new List<double?>();
        foreach (var x in a) { if (x == null) l.Add(null); else if (Number(x) is double d) l.Add(d); else return null; }
        return l;
    }
    public void SetRawChild(string key, object value) => D[key] = (Dictionary<string, object>)value;
    public void SetRawList(string key, object value) => D[key] = (List<object>)value;
    public void Set(string key, string value) => D[key] = value;
    public void Set(string key, long value) => D[key] = value;
    public void Set(string key, int value) => D[key] = value;
    public void Set(string key, bool value) => D[key] = value;
    public void Set(string key, double value) => D[key] = value;
    ILodgeWriter ILodgeWriter.Child(string key) { var c = new Dictionary<string, object>(); D[key] = c; return new TestFields(c); }
    ILodgeListWriter ILodgeWriter.List(string key) { var l = new List<object>(); D[key] = l; return new ListW(l); }
    sealed class ListW : ILodgeListWriter
    {
        readonly List<object> l;
        public ListW(List<object> l) { this.l = l; }
        public ILodgeWriter Add() { var c = new Dictionary<string, object>(); l.Add(c); return new TestFields(c); }
        public void Add(long value) => l.Add(value);
        public void Add(string value) => l.Add(value);
        public void Add(double value) => l.Add(value);
        public void AddNull() => l.Add(null);
    }

    // a JsonObject (e.g. a golden literal) as fields to read: integers as long, other numbers as double
    public static TestFields From(JsonObject o) => new TestFields((Dictionary<string, object>)Plain(o));
    static object Plain(JsonNode n) => n switch
    {
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => Plain(kv.Value)),
        JsonArray a => a.Select(Plain).ToList(),
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<int>(out var i) => (long)i,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        _ => null,
    };

    public static TestFields Written(Action<ILodgeWriter> write) { var w = new TestFields(); write(w); return w; }

    // compared as parsed JSON (key order irrelevant)
    public static void SameJson(object expected, object actual)
    {
        var e = JsonNode.Parse(JsonSerializer.Serialize(expected));
        var a = JsonNode.Parse(JsonSerializer.Serialize(actual));
        Assert.True(JsonNode.DeepEquals(e, a), "expected " + e!.ToJsonString() + "\nactual   " + a!.ToJsonString());
    }
}
