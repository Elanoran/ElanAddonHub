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
        string Str(string key);                         // null when missing or not a string
        int Int(string key);                            // 0 when missing or not a number
        long Long(string key);                          // 0 when missing or not a number
        bool Bool(string key);                          // false when missing or not true
        ILodgeFields Child(string key);                 // null when missing or not an object
        IReadOnlyList<ILodgeFields> List(string key);   // the objects in an array (other entries skipped); empty when missing, never null
    }

    // write side: a JSON object being built for sending. A null string is written as JSON null (as a dictionary/JsonObject literal did).
    public interface ILodgeWriter
    {
        void Set(string key, string value);
        void Set(string key, long value);
        void Set(string key, int value);
        void Set(string key, bool value);
        ILodgeWriter Child(string key);                 // a new object at key
        ILodgeListWriter List(string key);              // a new (empty) array at key
    }

    public interface ILodgeListWriter
    {
        ILodgeWriter Add();                             // a new object appended to the array
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
