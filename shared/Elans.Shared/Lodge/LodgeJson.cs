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
        // ---------------------------------------------------------------- limits and rules (one place, both sides)

        // the biggest frame the Lodge takes from an Outpost (a bigger one closes the connection: server/Lodge.Server/Hub.cs ReceiveLoop)
        public const int MaxFrameToLodge = 64 * 1024;
        // the biggest frame an Outpost takes from a Lodge; a bigger one drops the connection (src/Services/LodgeClient.cs, LodgeFrames.ReadAsync).
        // The biggest real ones: flights.routes.data at its worst (3000 routes of 64 points, about 4.5 MB), a big welcome (3 channels of 100
        // messages, 20 members ...: well under 1 MB, measured in LodgeJsonPerfTests); atlas.data comes in pages of about 200 KB.
        public const int MaxFrameFromLodge = 8 * 1024 * 1024;
        // the biggest HTTP answer body the Outpost reads as JSON from a Lodge (POST /exchange, an upload's answer, an error's {error, reason})
        public const int MaxHttpAnswer = 64 * 1024;
        // nesting levels, the frame itself included (System.Text.Json's default, which the server always had; the deepest real frame has ~8)
        public const int MaxDepth = 64;

        // How much one frame may hold (checked by Parse before anything is built). A frame's size alone doesn't bound what reading it costs:
        // an 8 MB frame of empty objects ({"history":{"c1":[{},{},..],..}}) read into typed messages took about 1.1 GB (~450 bytes per empty
        // object, ~100 per number). The biggest real frames, measured in FrameLimitsTests (Elans.Shared.Tests and Lodge.Tests):
        //   - flights.routes.data at its worst (FlightRoutes.MaxLegs 3000 routes of MaxPoints 64 points, 4.6 MB): 3000 keys in one object,
        //     594k values, 6k objects / arrays;
        //   - flights.data at its worst (Flights.MaxLegs 5000 legs, MaxNames 3000 names): 5000 keys in one object, 18k values;
        //   - an atlas.data page (Atlas.PageBytes 200 KB) of the smallest records (one-kill counters): 8743 keys in one section, 26k values;
        //   - a welcome with history (100 messages per channel), an armory list (Armory.MaxCharsTotal 1500), the device list (5000): a few
        //     thousand at most.
        // So: 20k items per array / keys per object (2.3x the densest atlas page), 1M values in all (1.7x the routes at their caps) and 100k
        // objects / arrays in all (15x the routes). That bounds what reading any frame can cost to about 100 MB (measured at the caps: 36 MB
        // for 100k empty objects, 95 MB for 1M numbers), where it was about 1.1 GB; a frame over them is refused at once, nothing built.
        public const int MaxItems = 20_000;
        public const int MaxValues = 1_000_000;
        public const int MaxContainers = 100_000;

        // What a frame must be, or it is skipped as a whole (null from Parse) - never half read, and never an exception later when a field is read:
        //   - valid JSON (RFC 8259): no comments, no trailing commas, nothing after the object, no BOM - and an object at the top;
        //   - at most MaxDepth levels;
        //   - at most MaxItems items in any array / keys in any object, MaxValues values and MaxContainers objects / arrays in all;
        //   - no key twice in one object (rejected, not "last wins": two readers must never see two different frames in the same bytes);
        //   - valid UTF-8 everywhere, keys included (no U+FFFD substitution), and no lone surrogate in a string or key (an escaped \uD800 alone):
        //     every Lodge and Outpost writes clean text, so only a forged frame has them.
        public static readonly JsonDocumentOptions Rules = new JsonDocumentOptions
        {
            MaxDepth = MaxDepth, AllowDuplicateProperties = false, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow,
        };

        // ---------------------------------------------------------------- parse / serialize

        // a received frame (its UTF-8 bytes) -> its object, or null when it breaks any of the rules above
        public static JsonObject Parse(byte[] utf8, int offset, int count)
        {
            if (utf8 == null) return null;
            var span = new ReadOnlySpan<byte>(utf8, offset, count);
            if (!System.Text.Unicode.Utf8.IsValid(span)) return null;              // invalid UTF-8 anywhere (keys, strings, overlong, encoded surrogates)
            if (!Within(span)) return null;                                          // too many items: refused before a node is built
            try { return Checked(JsonNode.Parse(span, null, Rules), MayHaveSurrogateEscape(span)); } catch { return null; }
        }

        public static JsonObject Parse(byte[] utf8) => utf8 == null ? null : Parse(utf8, 0, utf8.Length);

        // the same for text (an HTTP answer, a self-test frame); a lone surrogate in the text itself is refused too
        public static JsonObject Parse(string text)
        {
            if (text == null) return null;
            try
            {
                if (!Within(Encoding.UTF8.GetBytes(text))) return null;
                return Checked(JsonNode.Parse(text, null, Rules), text.IndexOf("\\uD", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return null; }
        }

        // A file this program wrote itself (a cache of many merged pages, e.g. the Outpost's Atlas cache): every rule of Parse except the
        // counts - it is no frame from the other side, and the merge of all pages may well hold more than one frame may.
        public static JsonObject ParseStored(byte[] utf8)
        {
            if (utf8 == null || !System.Text.Unicode.Utf8.IsValid(utf8)) return null;
            try { return Checked(JsonNode.Parse(utf8, null, Rules), MayHaveSurrogateEscape(utf8)); } catch { return null; }
        }

        // The counts (MaxItems / MaxValues / MaxContainers), read with a plain reader: nothing is allocated, and a frame over a limit stops
        // the reading where it passes it. Invalid JSON is false here too (the parse would refuse it anyway).
        static bool Within(ReadOnlySpan<byte> utf8)
        {
            var r = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = MaxDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            Span<int> items = stackalloc int[MaxDepth + 1];            // the items so far in each open array / object
            int depth = 0, values = 0, containers = 0;
            try
            {
                while (r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.PropertyName: continue;
                        case JsonTokenType.EndObject: case JsonTokenType.EndArray: depth--; continue;
                    }
                    // a value: one more item of the array / object it is in
                    if (++values > MaxValues) return false;
                    if (depth > 0 && ++items[depth - 1] > MaxItems) return false;
                    if (r.TokenType == JsonTokenType.StartObject || r.TokenType == JsonTokenType.StartArray)
                    {
                        if (++containers > MaxContainers || depth >= MaxDepth) return false;
                        items[depth++] = 0;
                    }
                }
                return true;
            }
            catch (JsonException) { return false; }
        }

        // What the parse itself doesn't check: System.Text.Json takes a duplicate key, the depth and the syntax at parse time (Rules), and
        // valid UTF-8 can't hold a surrogate - so the only thing left is an escaped one (\uD800 alone). Only a frame that has such an escape
        // at all (emoji are escaped pairs) is walked: every key and string read once, a lone surrogate refuses the frame. After this nothing
        // in a frame that parsed can throw when a field is read.
        static JsonObject Checked(JsonNode node, bool walk) => node is JsonObject o && (!walk || Clean(o)) ? o : null;

        static bool MayHaveSurrogateEscape(ReadOnlySpan<byte> s) => s.IndexOf("\\ud"u8) >= 0 || s.IndexOf("\\uD"u8) >= 0;

        static bool Clean(JsonNode n)
        {
            switch (n)
            {
                case null: return true;
                case JsonObject o:
                    foreach (var kv in o) if (!CleanText(kv.Key) || !Clean(kv.Value)) return false;
                    return true;
                case JsonArray a:
                    foreach (var x in a) if (!Clean(x)) return false;
                    return true;
                case JsonValue v:
                    return v.GetValueKind() != JsonValueKind.String || (v.TryGetValue<string>(out var s) && CleanText(s));
                default: return false;
            }
        }

        // no lone surrogate: a high one is followed by a low one, a low one follows a high one
        static bool CleanText(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (char.IsHighSurrogate(c)) { if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return false; i++; }
                else if (char.IsLowSurrogate(c)) return false;
            }
            return true;
        }

        public static string Serialize(JsonNode node) => node?.ToJsonString() ?? "null";
        public static byte[] SerializeUtf8(JsonNode node) => Encoding.UTF8.GetBytes(Serialize(node));

        // a typed message as the JsonObject that is sent: LodgeJson.Build(new PingMsg().Write)
        public static JsonObject Build(Action<ILodgeWriter> write) { var w = new LodgeJsonFields(); write(w); return w.Obj; }

        // ---------------------------------------------------------------- plain .NET data (the Outpost's Dictionary validators)

        // A JSON node as plain .NET data, the model of the Outpost's old hand-written reader (src/Services/Json.cs): an object is a
        // Dictionary<string, object> (keys in the order received), an array a List<object>, every number a double, plus string, bool and
        // null. Used only where a part of a frame feeds a validator written for that model (an Atlas section, a character sheet).
        public static object ToPlain(JsonNode node)
        {
            switch (node)
            {
                case null: return null;
                case JsonObject o:
                    var d = new Dictionary<string, object>(o.Count);
                    foreach (var kv in o) d[kv.Key] = ToPlain(kv.Value);
                    return d;
                case JsonArray a:
                    var l = new List<object>(a.Count);
                    foreach (var x in a) l.Add(ToPlain(x));
                    return l;
                case JsonValue v:
                    switch (v.GetValueKind())
                    {
                        case JsonValueKind.String: return v.GetValue<string>();
                        case JsonValueKind.True: return true;
                        case JsonValueKind.False: return false;
                        case JsonValueKind.Number:
                            if (v.TryGetValue<double>(out var dn)) return double.IsFinite(dn) ? dn : (object)null;   // 1e309: no number (as Number)
                            if (v.TryGetValue<long>(out var ln)) return (double)ln;          // a number written by code, not parsed
                            if (v.TryGetValue<int>(out var i)) return (double)i;
                            if (v.TryGetValue<float>(out var f)) return (double)f;
                            if (v.TryGetValue<decimal>(out var m)) return (double)m;
                            return double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
                        default: return null;
                    }
                default: return null;
            }
        }

        // Plain .NET data (Dictionary / IDictionary<string, object>, any other IEnumerable, string, bool, int, long, double; anything else as
        // its ToString(), a non-finite double as null) as the JSON text the old Outpost writer wrote for it - with this engine's escaping.
        public static string WritePlain(object value)
        {
            var buf = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { SkipValidation = false })) WritePlain(w, value);
            return Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length);
        }

        static void WritePlain(Utf8JsonWriter w, object v)
        {
            switch (v)
            {
                case null: w.WriteNullValue(); break;
                case string s: w.WriteStringValue(s); break;
                case bool b: w.WriteBooleanValue(b); break;
                case int n: w.WriteNumberValue(n); break;
                case long n: w.WriteNumberValue(n); break;
                case double n: if (double.IsFinite(n)) w.WriteNumberValue(n); else w.WriteNullValue(); break;
                case JsonNode node: node.WriteTo(w); break;
                case IDictionary<string, object> d:
                    w.WriteStartObject();
                    foreach (var kv in d) { w.WritePropertyName(kv.Key); WritePlain(w, kv.Value); }
                    w.WriteEndObject();
                    break;
                case System.Collections.IEnumerable e:
                    w.WriteStartArray();
                    foreach (var x in e) WritePlain(w, x);
                    w.WriteEndArray();
                    break;
                default: w.WriteStringValue(v.ToString()); break;
            }
        }

        // Plain .NET data as a JSON node that reads exactly like a received frame (written as text, parsed back): a section built by the
        // Outpost's Dictionary code, sent raw in a typed message. null for null, or when it is no valid frame part (e.g. nested too deep).
        public static JsonNode FromPlain(object value)
        {
            if (value == null) return null;
            try { var node = JsonNode.Parse(WritePlain(value), null, Rules); return Clean(node) ? node : null; } catch { return null; }
        }

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

        // a JSON number as a double (strict: "3" or true is no number); null for anything else - and for a number too big for a double
        // (1e309 reads as Infinity, which no JSON writer can write back: a message that kept it couldn't be sent again)
        public static double? Number(JsonNode n) =>
            n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
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
