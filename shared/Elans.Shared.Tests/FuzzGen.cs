#nullable disable
using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The fuzzers' tools, shared by shared/Elans.Shared.Tests/FuzzTests.cs and server/Lodge.Tests/FuzzHandlerTests.cs (linked there): the typed
// message classes found by reflection, a filled-in sample frame of each, the keys each Read asks for, a deterministic generator of
// random JSON text (every kind of value, edge numbers, unicode, depth around the parser's limit, 1 MB strings) and a mutator of real
// frames. ELANS_FUZZ=deep turns the iteration counts up (N).
internal static class FuzzGen
{
    internal static readonly bool Deep = string.Equals(Environment.GetEnvironmentVariable("ELANS_FUZZ"), "deep", StringComparison.OrdinalIgnoreCase);
    internal static int N(int normal, int deep) => Deep ? deep : normal;

    // ================================================================ the classes under test

    internal sealed record Msg(Type Class, string Type, MethodInfo Read, MethodInfo Write);

    // the same classes LodgeMsgTests.TypedClasses finds: a public const string Type declared on the class itself
    internal static List<Msg> Messages() => typeof(LodgeMsg).Assembly.GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic)
        .Select(t => (t, f: t.GetField("Type", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)))
        .Where(x => x.f != null && x.f.IsLiteral && x.f.FieldType == typeof(string))
        .Select(x => new Msg(x.t, (string)x.f.GetRawConstantValue(),
            x.t.GetMethod("Read", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy, new[] { typeof(ILodgeFields) }),
            x.t.GetMethod("Write", new[] { typeof(ILodgeWriter) })))
        .OrderBy(m => m.Class.Name, StringComparer.Ordinal)
        .ToList();

    // ================================================================ samples: every class filled in by reflection and written

    // a value for every public field (strings, numbers, bools, lists of two, nested classes), so Write produces a frame with every part
    internal static object Fill(Type t, int depth, Random r)
    {
        if (t == typeof(string)) return "s" + r.Next(1000);
        if (t == typeof(int)) return r.Next(1, 100);
        if (t == typeof(long)) return (long)r.Next(1, 1000) * 1_000_000_000L;
        if (t == typeof(bool)) return true;
        if (t == typeof(double)) return 1.5 + r.Next(10);
        if (Nullable.GetUnderlyingType(t) is Type u) return Fill(u, depth, r);
        if (t == typeof(object)) return null;                      // a raw child / list: its kind can't be guessed (left out)
        if (depth > 4) return null;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var e = t.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(t);
            for (int i = 0; i < 2; i++) { var v = Fill(e, depth + 1, r); if (v != null || !e.IsValueType) list.Add(v); }
            return list;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            var a = t.GetGenericArguments();
            return Activator.CreateInstance(t, "k" + r.Next(1000), Fill(a[1], depth + 1, r));
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>)) return Activator.CreateInstance(t);
        if (t.IsClass && t.GetConstructor(Type.EmptyTypes) != null && t.Assembly == typeof(LodgeMsg).Assembly)
        {
            var o = Activator.CreateInstance(t);
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => !f.IsInitOnly))
                try { f.SetValue(o, Fill(f.FieldType, depth + 1, r)); } catch (ArgumentException) { }
            return o;
        }
        return null;
    }

    // the class's sample frame: a filled instance written through the real engine (or a plain one / just the type when that can't be written)
    internal static JsonObject Sample(Msg msg, int seed = 1)
    {
        foreach (var make in new Func<object>[] { () => Fill(msg.Class, 0, new Random(seed)), () => Activator.CreateInstance(msg.Class) })
        {
            try
            {
                var w = new LodgeJsonFields();
                msg.Write.Invoke(make(), new object[] { w });
                return w.Obj;
            }
            catch (TargetInvocationException) { }
        }
        return new JsonObject { ["t"] = msg.Type };
    }

    // every key Read asks for (at every depth reached by the sample): the field names a fuzzer should aim at
    sealed class Spy : ILodgeFields
    {
        readonly ILodgeFields inner; readonly HashSet<string> keys;
        public Spy(ILodgeFields inner, HashSet<string> keys) { this.inner = inner; this.keys = keys; }
        T K<T>(string k, T v) { keys.Add(k); return v; }
        public bool Has(string key) => K(key, inner.Has(key));
        public bool IsNull(string key) => K(key, inner.IsNull(key));
        public string Str(string key) => K(key, inner.Str(key));
        public int Int(string key) => K(key, inner.Int(key));
        public long Long(string key) => K(key, inner.Long(key));
        public bool Bool(string key) => K(key, inner.Bool(key));
        public ILodgeFields Child(string key) { keys.Add(key); var c = inner.Child(key); return c == null ? null : new Spy(c, keys); }
        public IReadOnlyList<ILodgeFields> List(string key) { keys.Add(key); return inner.List(key).Select(x => (ILodgeFields)new Spy(x, keys)).ToList(); }
        public IReadOnlyList<long> Longs(string key) => K(key, inner.Longs(key));
        public IReadOnlyList<string> Strs(string key) => K(key, inner.Strs(key));
        public object RawChild(string key) => K(key, inner.RawChild(key));
        public object RawList(string key) => K(key, inner.RawList(key));
        public IReadOnlyList<string> Keys() => inner.Keys();
        public double? Num(string key) => K(key, inner.Num(key));
        public IReadOnlyList<double?> Nums(string key) => K(key, inner.Nums(key));
    }

    internal static List<string> KeysOf(Msg msg, JsonObject sample)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode n) { if (n is JsonObject o) foreach (var kv in o) { keys.Add(kv.Key); Walk(kv.Value); } else if (n is JsonArray a) foreach (var x in a) Walk(x); }
        Walk(sample);
        try { msg.Read.Invoke(null, new object[] { new Spy(new LodgeJsonFields((JsonObject)sample.DeepClone()), keys) }); } catch (TargetInvocationException) { }
        try { msg.Read.Invoke(null, new object[] { new Spy(new LodgeJsonFields(new JsonObject()), keys) }); } catch (TargetInvocationException) { }
        return keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    // a stable hash (string.GetHashCode is randomized per process), so a seed printed in a failure reproduces
    internal static int Stable(string s) { unchecked { int h = 17; foreach (var c in s) h = h * 31 + c; return h; } }

    // ================================================================ what the parser refuses (LodgeJson.Rules)

    // Three kinds of valid-looking JSON the fuzzers keep sending, because they once broke the engine: a lone UTF-16 surrogate as an escape
    // (below), a key twice in one object, and a number too big for a double (1e309). Since step 7 LodgeJson.Parse refuses the first two
    // (the frame is skipped as a whole), and LodgeJson.Number reads the third as no number - so nothing that parsed may throw when it is
    // read or written again, and the fuzzers excuse no exception at all.
    internal static readonly string[] LoneSurrogates = { @"\ud800", @"\udfff", @"\udc00\ud800", @"x\ud800y", @"\ud83d" };

    // ================================================================ random JSON text

    internal sealed class Gen
    {
        public readonly Random R;
        readonly bool big;
        public Gen(int seed, bool big = true) { R = new Random(seed); this.big = big; }

        static readonly string[] Numbers =
        {
            "0", "-0", "1", "-1", "0.5", "-0.5", "1.0", "1e2", "1E+2", "1e-2", "3.141592653589793", "2147483647", "2147483648", "-2147483648", "-2147483649",
            "4294967296", "9007199254740993", "9223372036854775807", "9223372036854775808", "-9223372036854775808", "-9223372036854775809",
            "18446744073709551616", "123456789012345678901234567890", "1e308", "-1e308", "1.7976931348623157e308", "1e309", "-1e309", "1e400",
            "5e-324", "1e-400", "-1e-400", "0.000000000000000000000000000001", "1.5e10", "99999999999999999999.5",
        };

        // the contents of JSON string literals (escaped as JSON allows; bidi controls, zero-width, controls, surrogate pairs, look-alikes)
        static readonly string[] Strings =
        {
            "", " ", "general", "t", "msg", "ping", "12", "-5", "1e400", "true", "false", "null", "NaN", "Infinity", "-Infinity", "0x10", "[]", "{}",
            @"\u202e\u202dname", @"\u2066x\u2069", @"\u200b", @"\u200c\u200d", @"\ufeff", @"\u0000", @"\u001b[31m", @"\u2028\u2029", @"a\nb\r\tc",
            @"\ud83d\ude00", "😀", @"e\u0301", "שלום", "مرحبا", @"\\", @"\""", @"\/",
            "Ｅｌａｎ", "<script>alert(1)</script>", "../../etc/passwd", "%2f..%2f", "https://example.com/x", "i1.AAAA", "t1.BBBB", @"\u0085\u00a0",
        };

        // a piece of a string: now and then a lone surrogate (the parser refuses the whole frame for it, so not too often)
        string Piece() => R.Next(40) == 0 ? LoneSurrogates[R.Next(LoneSurrogates.Length)] : Strings[R.Next(Strings.Length)];

        public string Num() => R.Next(4) switch
        {
            0 => R.Next(int.MinValue, int.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture),
            1 => (R.NextDouble() * Math.Pow(10, R.Next(-30, 30)) * (R.Next(2) == 0 ? -1 : 1)).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _ => Numbers[R.Next(Numbers.Length)],
        };

        // a JSON string literal (quotes included)
        public string Str()
        {
            int k = R.Next(10);
            string body;
            if (k < 4) body = Piece();
            else if (k < 7)
            {
                var sb = new StringBuilder();
                int n = R.Next(0, 40);
                for (int i = 0; i < n; i++)
                {
                    if (R.Next(3) == 0) { sb.Append(Piece()); continue; }
                    char c = (char)R.Next(0x20, 0x7f);
                    if (c == '\\' || c == '"') sb.Append('\\');
                    sb.Append(c);
                }
                body = sb.ToString();
            }
            else if (k < 9) body = new string((char)R.Next('a', 'z' + 1), R.Next(100, 5000));
            else body = big && R.Next(Deep ? 4 : 40) == 0 ? Long() : new string('x', R.Next(5000, 70000));
            return "\"" + body + "\"";
        }

        // a 1 MB string (plain, or unicode / escapes repeated)
        public string Long()
        {
            var unit = R.Next(3) switch { 0 => "a", 1 => @"\u202e😀", _ => @"\ud800" };
            var sb = new StringBuilder(1 << 20);
            while (sb.Length < 1 << 20) sb.Append(unit);
            return sb.ToString();
        }

        public string Value(int depth = 0)
        {
            int k = R.Next(depth > 6 ? 7 : 10);
            switch (k)
            {
                case 0: return "null";
                case 1: return R.Next(2) == 0 ? "true" : "false";
                case 2: case 3: return Num();
                case 4: case 5: case 6: return Str();
                case 7: return Nest(R.Next(1, 80));
                case 8:
                {
                    var sb = new StringBuilder("[");
                    int n = R.Next(0, 6);
                    for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); sb.Append(Value(depth + 1)); }
                    return sb.Append(']').ToString();
                }
                default:
                {
                    var keys = Enumerable.Range(0, R.Next(0, 5)).Select(_ => R.Next(3) == 0 ? Str() : "\"k" + R.Next(5) + "\"").ToList();
                    return Obj(keys.Select(key => (key, Value(depth + 1))).ToList(), dupes: R.Next(10) == 0);
                }
            }
        }

        // nested arrays / objects to a depth around the parser's limit (64)
        public string Nest(int depth)
        {
            bool arr = R.Next(2) == 0;
            var sb = new StringBuilder();
            for (int i = 0; i < depth; i++) sb.Append(arr ? "[" : "{\"a\":");
            sb.Append(R.Next(2) == 0 ? "1" : "\"x\"");
            for (int i = 0; i < depth; i++) sb.Append(arr ? "]" : "}");
            return sb.ToString();
        }

        // an object from (key, value) pairs; a key is a JSON string literal or a bare name; dupes: some keys twice
        public string Obj(IEnumerable<(string Key, string Value)> fields, bool dupes = false)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var (key, value) in fields)
            {
                int times = dupes && R.Next(3) == 0 ? 2 : 1;
                for (int i = 0; i < times; i++)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(key.StartsWith('"') ? key : "\"" + key + "\"").Append(':').Append(i == 0 ? value : Value(3));
                }
            }
            return sb.Append('}').ToString();
        }
    }

    // a real frame mutated: fields replaced by random values, removed, duplicated, renamed, moved; children mutated the same way
    internal static string Mutate(JsonNode n, Gen g, int depth = 0)
    {
        var r = g.R;
        if (depth > 0 && r.Next(6) == 0) return g.Value(depth);
        switch (n)
        {
            case JsonObject o:
            {
                var fields = new List<(string, string)>();
                foreach (var kv in o)
                {
                    int k = r.Next(10);
                    var key = JsonSerializer.Serialize(kv.Key);
                    if (k == 0) continue;                                                              // removed
                    if (k == 1) { fields.Add((key, g.Value(depth + 1))); continue; }                   // another value
                    if (k == 2) { fields.Add((g.Str(), Mutate(kv.Value, g, depth + 1))); continue; }   // another name
                    fields.Add((key, Mutate(kv.Value, g, depth + 1)));
                    if (k == 3 && r.Next(4) == 0) fields.Add((key, g.Value(depth + 1)));               // duplicated (rarely: see the known problems)
                }
                if (r.Next(4) == 0) fields.Add(("\"unknown" + r.Next(100) + "\"", g.Value(depth + 1)));   // an extra unknown field
                if (r.Next(8) == 0 && fields.Count > 1) { var i = r.Next(fields.Count); var f = fields[i]; fields.RemoveAt(i); fields.Insert(0, f); }
                return g.Obj(fields);
            }
            case JsonArray a:
            {
                var items = a.Select(x => Mutate(x, g, depth + 1)).ToList();
                if (r.Next(4) == 0) items.Add(g.Value(depth + 1));
                if (r.Next(4) == 0 && items.Count > 0) items.Insert(r.Next(items.Count), items[r.Next(items.Count)]);
                if (r.Next(6) == 0) items.Clear();
                return "[" + string.Join(",", items) + "]";
            }
            default:
                return r.Next(3) == 0 ? g.Value(depth) : (n?.ToJsonString() ?? "null");
        }
    }

}
