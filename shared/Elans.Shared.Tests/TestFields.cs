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
    public string Str(string key) => Get(key) as string;
    public int Int(string key) => Get(key) switch { int i => i, long l => (int)l, double d => (int)d, _ => 0 };
    public long Long(string key) => Get(key) switch { int i => i, long l => l, double d => (long)d, _ => 0 };
    public bool Bool(string key) => Get(key) is true;
    public ILodgeFields Child(string key) => Get(key) is Dictionary<string, object> c ? new TestFields(c) : null;
    public IReadOnlyList<ILodgeFields> List(string key) =>
        Get(key) is List<object> l ? l.OfType<Dictionary<string, object>>().Select(x => (ILodgeFields)new TestFields(x)).ToList() : new List<ILodgeFields>();
    public void Set(string key, string value) => D[key] = value;
    public void Set(string key, long value) => D[key] = value;
    public void Set(string key, int value) => D[key] = value;
    public void Set(string key, bool value) => D[key] = value;
    ILodgeWriter ILodgeWriter.Child(string key) { var c = new Dictionary<string, object>(); D[key] = c; return new TestFields(c); }
    ILodgeListWriter ILodgeWriter.List(string key) { var l = new List<object>(); D[key] = l; return new ListW(l); }
    sealed class ListW : ILodgeListWriter
    {
        readonly List<object> l;
        public ListW(List<object> l) { this.l = l; }
        public ILodgeWriter Add() { var c = new Dictionary<string, object>(); l.Add(c); return new TestFields(c); }
    }

    public static TestFields Written(Action<ILodgeWriter> write) { var w = new TestFields(); write(w); return w; }

    // compared as parsed JSON (key order irrelevant)
    public static void SameJson(object expected, object actual)
    {
        var e = JsonNode.Parse(JsonSerializer.Serialize(expected));
        var a = JsonNode.Parse(JsonSerializer.Serialize(actual));
        Assert.True(JsonNode.DeepEquals(e, a), "expected " + e!.ToJsonString() + "\nactual   " + a!.ToJsonString());
    }
}
