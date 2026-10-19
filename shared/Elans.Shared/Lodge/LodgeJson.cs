using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Elans.Shared
{
    // The one JSON engine of the Lodge link: System.Text.Json nodes, the same on the Lodge server and in the Outpost.
    //   LodgeJson.Parse        a received frame -> its JsonObject (null when it is no valid frame)
    //   LodgeJson.Serialize    a frame -> the text / UTF-8 bytes that go out
    //   LodgeJson.Build        a typed message -> the JsonObject to send
    //   LodgeJsonFields        ILodgeFields / ILodgeWriter over a JsonObject: the typed message classes read and write through it
    //   LodgeJson.Str/Int/..   the lenient readers under it (the server's J): a field of the wrong type is "missing", never an exception
    public static class LodgeJson
    {
        // ---------------------------------------------------------------- parse / serialize

        // a frame as the Lodge server read it: the UTF-8 text decoded, parsed, an object or nothing
        public static JsonObject Parse(byte[] utf8, int offset, int count)
        {
            try { return JsonNode.Parse(Encoding.UTF8.GetString(utf8, offset, count))?.AsObject(); } catch { return null; }
        }

        public static JsonObject Parse(byte[] utf8) => utf8 == null ? null : Parse(utf8, 0, utf8.Length);

        public static JsonObject Parse(string text)
        {
            if (text == null) return null;
            try { return JsonNode.Parse(text)?.AsObject(); } catch { return null; }
        }

        public static string Serialize(JsonNode node) => node?.ToJsonString() ?? "null";
        public static byte[] SerializeUtf8(JsonNode node) => Encoding.UTF8.GetBytes(Serialize(node));

        // a typed message as the JsonObject that is sent: LodgeJson.Build(new PingMsg().Write)
        public static JsonObject Build(Action<ILodgeWriter> write) { var w = new LodgeJsonFields(); write(w); return w.Obj; }

        // ---------------------------------------------------------------- the lenient readers

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

        // a JSON number as a double (strict: "3" or true is no number); null for anything else
        public static double? Number(JsonNode n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) ? d : null;
    }

    // ILodgeFields / ILodgeWriter over a JsonObject (shared/Elans.Shared/Lodge/LodgeFields.cs), read through the lenient LodgeJson readers.
    public sealed class LodgeJsonFields : ILodgeFields, ILodgeWriter
    {
        public JsonObject Obj { get; }
        public LodgeJsonFields(JsonObject o) { Obj = o ?? new JsonObject(); }
        public LodgeJsonFields() : this(new JsonObject()) { }

        // read
        public bool Has(string key) => Obj.ContainsKey(key);
        public bool IsNull(string key) => Obj[key] == null;
        public string Str(string key) => LodgeJson.Str(Obj, key);
        public int Int(string key) => LodgeJson.Int(Obj, key);
        public long Long(string key) => LodgeJson.Long(Obj, key);
        public bool Bool(string key) => LodgeJson.Bool(Obj, key);
        public ILodgeFields Child(string key) => LodgeJson.Obj(Obj, key) is JsonObject o ? new LodgeJsonFields(o) : null;
        public IReadOnlyList<ILodgeFields> List(string key) =>
            Obj[key] is JsonArray a ? a.OfType<JsonObject>().Select(o => (ILodgeFields)new LodgeJsonFields(o)).ToList() : Array.Empty<ILodgeFields>();
        public IReadOnlyList<long> Longs(string key) =>
            Obj[key] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0).ToList() : Array.Empty<long>();
        public IReadOnlyList<string> Strs(string key) =>
            Obj[key] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => s != null).ToList() : Array.Empty<string>();
        public object RawChild(string key) => LodgeJson.Obj(Obj, key);
        public object RawList(string key) => Obj[key] as JsonArray;
        public IReadOnlyList<string> Keys() => Obj.Select(kv => kv.Key).ToList();
        public double? Num(string key) => LodgeJson.Number(Obj[key]);
        public IReadOnlyList<double?> Nums(string key)
        {
            if (Obj[key] is not JsonArray a) return null;
            var l = new List<double?>(a.Count);
            foreach (var x in a)
            {
                if (x == null) { l.Add(null); continue; }
                if (LodgeJson.Number(x) is not double d) return null;
                l.Add(d);
            }
            return l;
        }

        // write
        public void Set(string key, string value) => Obj[key] = value;
        public void Set(string key, long value) => Obj[key] = value;
        public void Set(string key, int value) => Obj[key] = value;
        public void Set(string key, bool value) => Obj[key] = value;
        public void Set(string key, double value) => Obj[key] = value;
        ILodgeWriter ILodgeWriter.Child(string key) { var o = new JsonObject(); Obj[key] = o; return new LodgeJsonFields(o); }
        ILodgeListWriter ILodgeWriter.List(string key) { var a = new JsonArray(); Obj[key] = a; return new ListWriter(a); }
        public void SetRawChild(string key, object value) => Obj[key] = (JsonObject)value;
        public void SetRawList(string key, object value) => Obj[key] = (JsonArray)value;

        sealed class ListWriter : ILodgeListWriter
        {
            readonly JsonArray a;
            public ListWriter(JsonArray a) { this.a = a; }
            public ILodgeWriter Add() { var o = new JsonObject(); a.Add(o); return new LodgeJsonFields(o); }
            public void Add(long value) => a.Add(value);
            public void Add(string value) => a.Add(value);
            public void Add(double value) => a.Add(value);
            public void AddNull() => a.Add((JsonNode)null);
        }
    }
}
