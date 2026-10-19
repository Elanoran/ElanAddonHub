using System.Collections.Generic;

namespace Elans.Shared
{
    // The two sides of the Lodge socket keep their own JSON models (the Lodge server: System.Text.Json.Nodes.JsonObject; the Outpost:
    // Dictionary<string, object> from src/Services/Json.cs). A typed message class (LfgMessages.cs) reads and writes through these two
    // small interfaces, and each side wraps its own lenient getters in an adapter (server/Lodge.Server/LodgeFieldsJson.cs,
    // src/Lodge/LodgeFieldsDict.cs), so reading behaves exactly as it did before: a missing field or a field of the wrong type is
    // null / 0 / false, never an exception.

    // read side: a received JSON object
    public interface ILodgeFields
    {
        bool Has(string key);                           // the key is there (whatever its value, null included)
        bool IsNull(string key);                        // the key is missing, or its value is JSON null
        string Str(string key);                         // null when missing or not a string
        int Int(string key);                            // 0 when missing or not a number
        long Long(string key);                          // 0 when missing or not a number
        bool Bool(string key);                          // false when missing or not true
        ILodgeFields Child(string key);                 // null when missing or not an object
        IReadOnlyList<ILodgeFields> List(string key);   // the objects in an array (other entries skipped); empty when missing, never null
        IReadOnlyList<long> Longs(string key);          // the entries of an array as whole numbers (0 for one that isn't); empty when missing
        IReadOnlyList<string> Strs(string key);         // the string entries of an array (other entries skipped); empty when missing
        // A free-form payload passed through unread (e.g. a member's "game" presence): the object at key in this side's own JSON model
        // (JsonObject on the server, Dictionary<string, object> in the Outpost); null when missing or not an object.
        object RawChild(string key);
        // the same for a free-form array (e.g. atlas.data's finds): JsonArray on the server, List<object> in the Outpost; null when missing or not an array
        object RawList(string key);
        IReadOnlyList<string> Keys();                   // this object's keys in the order received (a map keyed by ids, e.g. flights.data's legs)
        double? Num(string key);                        // the number at key; null when missing or not a number (strict: "3" or true is no number)
        // the entries of an array of numbers, a JSON null kept as null (e.g. a route's [x, y, z|null, ...]); null when missing, not an array,
        // or holding anything else - strict, for the readers that refuse a malformed list as a whole
        IReadOnlyList<double?> Nums(string key);
    }

    // write side: a JSON object being built for sending. A null string is written as JSON null (as a dictionary/JsonObject literal did).
    public interface ILodgeWriter
    {
        void Set(string key, string value);
        void Set(string key, long value);
        void Set(string key, int value);
        void Set(string key, bool value);
        void Set(string key, double value);
        ILodgeWriter Child(string key);                 // a new object at key
        ILodgeListWriter List(string key);              // a new (empty) array at key
        void SetRawChild(string key, object value);     // a free-form object in this side's own JSON model (see ILodgeFields.RawChild), as it is
        void SetRawList(string key, object value);      // a free-form array in this side's own JSON model (see ILodgeFields.RawList), as it is
    }

    public interface ILodgeListWriter
    {
        ILodgeWriter Add();                             // a new object appended to the array
        void Add(long value);                           // a number appended to the array
        void Add(string value);                         // a string appended to the array
        void Add(double value);                         // a fractional number appended to the array
        void AddNull();                                 // a JSON null appended to the array
    }

    public static class LodgeWriterExtensions
    {
        // a number, or JSON null when there is none (as a JsonObject / dictionary literal wrote a null long?)
        public static void SetOrNull(this ILodgeWriter w, string key, long? value) { if (value is long v) w.Set(key, v); else w.Set(key, (string)null); }
        public static long? LongOrNull(this ILodgeFields f, string key) => f.IsNull(key) ? (long?)null : f.Long(key);
        public static int? IntOrNull(this ILodgeFields f, string key) => f.IsNull(key) ? (int?)null : f.Int(key);
        // the value at key is a JSON false - not missing, null, true, a number, a string, an object or an array (as the server's strict
        // TryGetValue<bool> checks: e.g. "share": only a JSON false stops sharing)
        public static bool IsFalse(this ILodgeFields f, string key) =>
            f.Has(key) && !f.IsNull(key) && !f.Bool(key) && f.Num(key) == null && f.Str(key) == null && f.Child(key) == null && f.RawList(key) == null;
        // true / false for a JSON bool, null for anything else (missing included) - the server's J.BoolOrNull
        public static bool? BoolOrNull(this ILodgeFields f, string key) => f.Bool(key) ? true : f.IsFalse(key) ? false : (bool?)null;
    }

    // A request that carries nothing but its type: { t }.
    //   public sealed class CairnJoinGetMsg : LodgeEmptyMsg<CairnJoinGetMsg> { public const string Type = LodgeMsg.CairnJoinGet; protected override string MsgType => Type; }
    public abstract class LodgeEmptyMsg<TSelf> where TSelf : LodgeEmptyMsg<TSelf>, new()
    {
        protected abstract string MsgType { get; }

        public void Write(ILodgeWriter w) => w.Set("t", MsgType);
        public static TSelf Read(ILodgeFields f) => new TSelf();
    }

    // The boilerplate of a request that carries nothing but the id of the thing it acts on: { t, id }. A message class only names its type:
    //   public sealed class LfgLeaveMsg : LodgeIdMsg<LfgLeaveMsg> { public const string Type = LodgeMsg.LfgLeave; protected override string MsgType => Type; }
    // and is written with new LfgLeaveMsg { Id = .. }.Write and read with LfgLeaveMsg.Read(fields), like any other message class.
    public abstract class LodgeIdMsg<TSelf> where TSelf : LodgeIdMsg<TSelf>, new()
    {
        public string Id;
        protected abstract string MsgType { get; }

        public void Write(ILodgeWriter w) { w.Set("t", MsgType); w.Set("id", Id); }
        public static TSelf Read(ILodgeFields f) => new TSelf { Id = f.Str("id") };
    }
}
