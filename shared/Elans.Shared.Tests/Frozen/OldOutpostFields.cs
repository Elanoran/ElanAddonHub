using System;
using System.Collections.Generic;
using System.Linq;
using Elans.Shared;

namespace Elans.Shared.Tests.Frozen
{
    // FROZEN COPY of src/Lodge/LodgeFieldsDict.cs as of 3906340 (renamed LodgeFieldsDict -> OldOutpostFields, Json -> OldOutpostJson,
    // the extension calls written out). Never change it: see JsonLeniencyTests.
    // The Outpost's side of the shared typed Lodge messages (shared/Elans.Shared/Lodge/LodgeFields.cs): ILodgeFields / ILodgeWriter over
    // the Dictionary<string, object> of Services/Json.cs. Reading goes through the same lenient Json.Str/Int/Long/Bool/Child/List getters
    // as the rest of the Lodge code; numbers are written as double, as Json.Parse gives them back.
    public sealed class OldOutpostFields : ILodgeFields, ILodgeWriter
    {
        public Dictionary<string, object> Dict { get; }
        public OldOutpostFields(Dictionary<string, object> d) { Dict = d ?? new Dictionary<string, object>(); }
        public OldOutpostFields() : this(new Dictionary<string, object>()) { }

        // read
        public bool Has(string key) => Dict.ContainsKey(key);
        public bool IsNull(string key) => !Dict.TryGetValue(key, out var v) || v == null;
        public string Str(string key) => OldOutpostJson.Str(Dict, key);
        public int Int(string key) => OldOutpostJson.Int(Dict, key);
        public long Long(string key) => OldOutpostJson.Long(Dict, key);
        public bool Bool(string key) => OldOutpostJson.Bool(Dict, key);
        public ILodgeFields Child(string key) => OldOutpostJson.Child(Dict, key) is Dictionary<string, object> c ? new OldOutpostFields(c) : null;
        public IReadOnlyList<ILodgeFields> List(string key) =>
            OldOutpostJson.List(Dict, key).OfType<Dictionary<string, object>>().Select(x => (ILodgeFields)new OldOutpostFields(x)).ToList();
        public IReadOnlyList<long> Longs(string key) =>
            OldOutpostJson.List(Dict, key).Select(x => x is double d && d == Math.Floor(d) && Math.Abs(d) < 9e18 ? (long)d : x is long l ? l : 0L).ToList();
        public IReadOnlyList<string> Strs(string key) => OldOutpostJson.List(Dict, key).OfType<string>().ToList();
        public object RawChild(string key) => OldOutpostJson.Child(Dict, key);
        public object RawList(string key) => Dict.TryGetValue(key, out var v) ? v as List<object> : null;
        public IReadOnlyList<string> Keys() => Dict.Keys.ToList();
        public double? Num(string key) => Dict.TryGetValue(key, out var v) && v is double d ? d : (double?)null;     // Json.Parse gives every number as double
        public IReadOnlyList<double?> Nums(string key)
        {
            if (!Dict.TryGetValue(key, out var v) || !(v is List<object> a)) return null;
            var l = new List<double?>(a.Count);
            foreach (var x in a)
            {
                if (x == null) l.Add(null);
                else if (x is double d) l.Add(d);
                else return null;
            }
            return l;
        }

        // write
        public void Set(string key, string value) => Dict[key] = value;
        public void Set(string key, long value) => Dict[key] = (double)value;
        public void Set(string key, int value) => Dict[key] = (double)value;
        public void Set(string key, bool value) => Dict[key] = value;
        public void Set(string key, double value) => Dict[key] = value;
        ILodgeWriter ILodgeWriter.Child(string key) { var c = new Dictionary<string, object>(); Dict[key] = c; return new OldOutpostFields(c); }
        ILodgeListWriter ILodgeWriter.List(string key) { var l = new List<object>(); Dict[key] = l; return new ListWriter(l); }
        public void SetRawChild(string key, object value) => Dict[key] = (Dictionary<string, object>)value;
        public void SetRawList(string key, object value) => Dict[key] = (List<object>)value;

        sealed class ListWriter : ILodgeListWriter
        {
            readonly List<object> l;
            public ListWriter(List<object> l) { this.l = l; }
            public ILodgeWriter Add() { var c = new Dictionary<string, object>(); l.Add(c); return new OldOutpostFields(c); }
            public void Add(long value) => l.Add(value);       // a long, as the old List<object> { id } literals held it
            public void Add(string value) => l.Add(value);
            public void Add(double value) => l.Add(value);
            public void AddNull() => l.Add(null);
        }

        // a typed message as the dictionary LodgeClient sends
        public static Dictionary<string, object> Build(Action<ILodgeWriter> write) { var w = new OldOutpostFields(); write(w); return w.Dict; }
    }
}
