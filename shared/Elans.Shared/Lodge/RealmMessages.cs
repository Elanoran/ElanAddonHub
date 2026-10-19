using System.Collections.Generic;

namespace Elans.Shared
{
    // Realm status (server/PROTOCOL.md, "Realm status", feature "realms"): the field names live only here. The classes only map fields; how the
    // Lodge asks the Blizzard API (its credentials never leave the server), when it pushes and how the Outpost cleans what it shows stay where
    // they are (Realms.cs; LodgeSession.Realms.cs). Nothing is asked of the server: both messages are pushed.

    // server -> client: the realms' status { available, region, realms: [RealmView] (empty unless available), checkedAt (unix s), why? (only when not available) }
    public sealed class RealmsStatusMsg
    {
        public const string Type = LodgeMsg.RealmsStatus;
        public bool Available;
        public string Region, Why;     // Why: null = not sent
        public List<RealmView> Realms = new List<RealmView>();
        public long CheckedAt;

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("available", Available); w.Set("region", Region);
            var l = w.List("realms"); foreach (var r in Realms) r.Write(l.Add());
            w.Set("checkedAt", CheckedAt);
            if (Why != null) w.Set("why", Why);
        }

        public static RealmsStatusMsg Read(ILodgeFields f)
        {
            var m = new RealmsStatusMsg { Available = f.Bool("available"), Region = f.Str("region"), CheckedAt = f.Long("checkedAt"), Why = f.Str("why") };
            foreach (var r in f.List("realms")) m.Realms.Add(RealmView.Read(r));
            return m;
        }
    }

    // a realm { id (connected realm), name, slug, ruleset, status UP|DOWN|UNKNOWN, namespace, population?, hasQueue? }
    public sealed class RealmView
    {
        public int Id;
        public string Name, Slug, Ruleset, Status, Namespace;
        public string Population;      // null = not sent (written only when not empty)
        public bool? HasQueue;         // null = not sent

        public void Write(ILodgeWriter w)
        {
            w.Set("id", Id); w.Set("name", Name); w.Set("slug", Slug); w.Set("ruleset", Ruleset); w.Set("status", Status); w.Set("namespace", Namespace);
            if (!string.IsNullOrEmpty(Population)) w.Set("population", Population);
            if (HasQueue is bool q) w.Set("hasQueue", q);
        }

        public static RealmView Read(ILodgeFields f) => new RealmView
        {
            Id = f.Int("id"), Name = f.Str("name"), Slug = f.Str("slug"), Ruleset = f.Str("ruleset"), Status = f.Str("status"), Namespace = f.Str("namespace"),
            Population = f.Str("population"), HasQueue = f.Has("hasQueue") ? f.Bool("hasQueue") : (bool?)null,
        };
    }

    // server -> client: realms that were down are all up again { realms: [names], checkedAt (unix s) }
    public sealed class RealmsBackMsg
    {
        public const string Type = LodgeMsg.RealmsBack;
        public List<string> Realms = new List<string>();   // reading: only the string entries
        public long CheckedAt;

        public void Write(ILodgeWriter w) { w.Set("t", Type); var l = w.List("realms"); foreach (var n in Realms) l.Add(n); w.Set("checkedAt", CheckedAt); }
        public static RealmsBackMsg Read(ILodgeFields f) => new RealmsBackMsg { Realms = new List<string>(f.Strs("realms")), CheckedAt = f.Long("checkedAt") };
    }
}
