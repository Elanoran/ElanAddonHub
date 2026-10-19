using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests.Frozen;

// FROZEN COPY of server/Lodge.Server/LodgeFieldsJson.cs and the J readers of server/Lodge.Server/Text.cs as of 3906340 (renamed
// LodgeFieldsJson -> OldServerFields, J -> OldServerJ). Never change it: see JsonLeniencyTests.
// The Lodge server's side of the shared typed messages (shared/Elans.Shared/Lodge/LodgeFields.cs): ILodgeFields / ILodgeWriter over a
// JsonObject. Reading goes through the same lenient J helpers as the rest of the server (a field of the wrong type is "missing").
public sealed class OldServerFields : ILodgeFields, ILodgeWriter
{
    public JsonObject Obj { get; }
    public OldServerFields(JsonObject o) { Obj = o ?? new JsonObject(); }
    public OldServerFields() : this(new JsonObject()) { }

    // read
    public bool Has(string key) => Obj.ContainsKey(key);
    public bool IsNull(string key) => Obj[key] == null;
    public string Str(string key) => OldServerJ.Str(Obj, key);
    public int Int(string key) => OldServerJ.Int(Obj, key);
    public long Long(string key) => OldServerJ.Long(Obj, key);
    public bool Bool(string key) => OldServerJ.Bool(Obj, key);
    public ILodgeFields Child(string key) => OldServerJ.Obj(Obj, key) is JsonObject o ? new OldServerFields(o) : null;
    public IReadOnlyList<ILodgeFields> List(string key) =>
        Obj[key] is JsonArray a ? a.OfType<JsonObject>().Select(o => (ILodgeFields)new OldServerFields(o)).ToList() : Array.Empty<ILodgeFields>();
    public IReadOnlyList<long> Longs(string key) =>
        Obj[key] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0).ToList() : Array.Empty<long>();
    public IReadOnlyList<string> Strs(string key) =>
        Obj[key] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => s != null).ToList() : Array.Empty<string>();
    public object RawChild(string key) => OldServerJ.Obj(Obj, key);
    public object RawList(string key) => Obj[key] as JsonArray;
    public IReadOnlyList<string> Keys() => Obj.Select(kv => kv.Key).ToList();
    public double? Num(string key) => Number(Obj[key]);
    public IReadOnlyList<double?> Nums(string key)
    {
        if (Obj[key] is not JsonArray a) return null;
        var l = new List<double?>(a.Count);
        foreach (var x in a)
        {
            if (x == null) { l.Add(null); continue; }
            if (Number(x) is not double d) return null;
            l.Add(d);
        }
        return l;
    }
    static double? Number(JsonNode n) => n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && v.TryGetValue<double>(out var d) ? d : null;

    // write
    public void Set(string key, string value) => Obj[key] = value;
    public void Set(string key, long value) => Obj[key] = value;
    public void Set(string key, int value) => Obj[key] = value;
    public void Set(string key, bool value) => Obj[key] = value;
    public void Set(string key, double value) => Obj[key] = value;
    ILodgeWriter ILodgeWriter.Child(string key) { var o = new JsonObject(); Obj[key] = o; return new OldServerFields(o); }
    ILodgeListWriter ILodgeWriter.List(string key) { var a = new JsonArray(); Obj[key] = a; return new ListWriter(a); }
    public void SetRawChild(string key, object value) => Obj[key] = (JsonObject)value;
    public void SetRawList(string key, object value) => Obj[key] = (JsonArray)value;

    sealed class ListWriter : ILodgeListWriter
    {
        readonly JsonArray a;
        public ListWriter(JsonArray a) { this.a = a; }
        public ILodgeWriter Add() { var o = new JsonObject(); a.Add(o); return new OldServerFields(o); }
        public void Add(long value) => a.Add(value);
        public void Add(string value) => a.Add(value);
        public void Add(double value) => a.Add(value);
        public void AddNull() => a.Add((JsonNode)null);
    }

    // a typed message as the JsonObject the hub sends
    public static JsonObject Build(Action<ILodgeWriter> write) { var w = new OldServerFields(); write(w); return w.Obj; }
}

// Safe readers for client JSON: a field of the wrong type is "missing", never an exception.
public static class OldServerJ
{
    public static string Str(JsonObject m, string key) => m != null && m[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool Bool(JsonObject m, string key, bool fallback = false) =>
        m != null && m[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    // null when absent / not a bool
    public static bool? BoolOrNull(JsonObject m, string key) => m != null && m[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static int Int(JsonObject m, string key, int fallback = 0)
    {
        try
        {
            if (m != null && m[key] is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) return i;
                if (v.TryGetValue<double>(out var d) && !double.IsNaN(d)) return (int)Math.Clamp(d, int.MinValue, int.MaxValue);
            }
        }
        catch { }
        return fallback;
    }

    public static long Long(JsonObject m, string key, long fallback = 0)
    {
        try
        {
            if (m != null && m[key] is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<double>(out var d) && !double.IsNaN(d)) return (long)Math.Clamp(d, long.MinValue, long.MaxValue);
            }
        }
        catch { }
        return fallback;
    }

    public static JsonObject Obj(JsonObject m, string key) => m != null && m[key] is JsonObject o ? o : null;
}
