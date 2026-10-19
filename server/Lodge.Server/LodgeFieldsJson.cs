using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// The Lodge server's side of the shared typed messages (shared/Elans.Shared/Lodge/LodgeFields.cs): ILodgeFields / ILodgeWriter over a
// JsonObject. Reading goes through the same lenient J helpers as the rest of the server (a field of the wrong type is "missing").
public sealed class LodgeFieldsJson : ILodgeFields, ILodgeWriter
{
    public JsonObject Obj { get; }
    public LodgeFieldsJson(JsonObject o) { Obj = o ?? new JsonObject(); }
    public LodgeFieldsJson() : this(new JsonObject()) { }

    // read
    public bool Has(string key) => Obj.ContainsKey(key);
    public bool IsNull(string key) => Obj[key] == null;
    public string Str(string key) => J.Str(Obj, key);
    public int Int(string key) => J.Int(Obj, key);
    public long Long(string key) => J.Long(Obj, key);
    public bool Bool(string key) => J.Bool(Obj, key);
    public ILodgeFields Child(string key) => J.Obj(Obj, key) is JsonObject o ? new LodgeFieldsJson(o) : null;
    public IReadOnlyList<ILodgeFields> List(string key) =>
        Obj[key] is JsonArray a ? a.OfType<JsonObject>().Select(o => (ILodgeFields)new LodgeFieldsJson(o)).ToList() : Array.Empty<ILodgeFields>();
    public IReadOnlyList<long> Longs(string key) =>
        Obj[key] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0).ToList() : Array.Empty<long>();
    public IReadOnlyList<string> Strs(string key) =>
        Obj[key] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => s != null).ToList() : Array.Empty<string>();
    public object RawChild(string key) => J.Obj(Obj, key);

    // write
    public void Set(string key, string value) => Obj[key] = value;
    public void Set(string key, long value) => Obj[key] = value;
    public void Set(string key, int value) => Obj[key] = value;
    public void Set(string key, bool value) => Obj[key] = value;
    ILodgeWriter ILodgeWriter.Child(string key) { var o = new JsonObject(); Obj[key] = o; return new LodgeFieldsJson(o); }
    ILodgeListWriter ILodgeWriter.List(string key) { var a = new JsonArray(); Obj[key] = a; return new ListWriter(a); }
    public void SetRawChild(string key, object value) => Obj[key] = (JsonObject)value;

    sealed class ListWriter : ILodgeListWriter
    {
        readonly JsonArray a;
        public ListWriter(JsonArray a) { this.a = a; }
        public ILodgeWriter Add() { var o = new JsonObject(); a.Add(o); return new LodgeFieldsJson(o); }
        public void Add(long value) => a.Add(value);
        public void Add(string value) => a.Add(value);
    }

    // a typed message as the JsonObject the hub sends
    public static JsonObject Build(Action<ILodgeWriter> write) { var w = new LodgeFieldsJson(); write(w); return w.Obj; }
}
