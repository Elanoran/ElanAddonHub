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
    public string Str(string key) => J.Str(Obj, key);
    public int Int(string key) => J.Int(Obj, key);
    public long Long(string key) => J.Long(Obj, key);
    public bool Bool(string key) => J.Bool(Obj, key);
    public ILodgeFields Child(string key) => J.Obj(Obj, key) is JsonObject o ? new LodgeFieldsJson(o) : null;
    public IReadOnlyList<ILodgeFields> List(string key) =>
        Obj[key] is JsonArray a ? a.OfType<JsonObject>().Select(o => (ILodgeFields)new LodgeFieldsJson(o)).ToList() : Array.Empty<ILodgeFields>();

    // write
    public void Set(string key, string value) => Obj[key] = value;
    public void Set(string key, long value) => Obj[key] = value;
    public void Set(string key, int value) => Obj[key] = value;
    public void Set(string key, bool value) => Obj[key] = value;
    ILodgeWriter ILodgeWriter.Child(string key) { var o = new JsonObject(); Obj[key] = o; return new LodgeFieldsJson(o); }
    ILodgeListWriter ILodgeWriter.List(string key) { var a = new JsonArray(); Obj[key] = a; return new ListWriter(a); }

    sealed class ListWriter : ILodgeListWriter
    {
        readonly JsonArray a;
        public ListWriter(JsonArray a) { this.a = a; }
        public ILodgeWriter Add() { var o = new JsonObject(); a.Add(o); return new LodgeFieldsJson(o); }
    }

    // a typed message as the JsonObject the hub sends
    public static JsonObject Build(Action<ILodgeWriter> write) { var w = new LodgeFieldsJson(); write(w); return w.Obj; }
}
