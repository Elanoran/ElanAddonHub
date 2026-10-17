using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ElansAddonHub.Services.Atlas
{
    // The "About <class>" / "About <race>" card on a class or race page (2.81). Two kinds of text, always told apart on the page:
    //   official  the game's own words: ChrClasses.Description / ChrRaces.LoreDescription, the character creation text of the player's client
    //             (read with the class tables, ClassData; nothing is shipped for it). Labelled "From the game".
    //   notes     written for the Outpost in its own words, like the zone notes (ZoneLore): history, a few facts (capital, leader, homeland ...),
    //             notable figures, "Worth knowing" bullets, what Forever changed, and where the facts come from. Shipped in the exe
    //             (Assets\Data\classlore.json, embedded resource "Data.classlore.json"); nothing is downloaded. Labelled "Outpost notes".
    //             An entry with "basis": "game" (Forever's own Skyborne) was written from the game's own text and the in-game data only,
    //             with no outside source, and says so.
    // Classes are keyed by the client's class file (WARRIOR, PRIEST ...), races by their name in the client (Human, Night Elf, High Order Skyborne ...).
    public sealed class ClassLoreFact
    {
        public string Label = "", Value = "";
    }

    public sealed class ClassLoreFigure
    {
        public string Name = "", Why = "";
    }

    public sealed class ClassLoreEntry
    {
        public string Key = "", Name = "", Kind = "";            // Kind: "class" or "race"
        public string Summary = "", History = "", Forever = "", Basis = "";
        public List<ClassLoreFact> Facts = new List<ClassLoreFact>();
        public List<ClassLoreFigure> Figures = new List<ClassLoreFigure>();
        public List<string> Mentions = new List<string>();
        public List<string> Sources = new List<string>();
        public bool FromGameDataOnly => string.Equals(Basis, "game", StringComparison.OrdinalIgnoreCase);
        public bool HasText => Summary.Length + History.Length + Forever.Length > 0 || Facts.Count + Figures.Count + Mentions.Count > 0;
    }

    // what one class or race page shows in its "About" card: worked out with the page (off the UI thread)
    public sealed class ClassLorePage
    {
        public string Title = "";                    // "Priest", "Night Elf"
        public string Official = "";                 // the game's own words ("" = the client has none)
        public ClassLoreEntry Notes;                 // the Outpost's notes (null = none for this class / race)
        public List<ClassLoreFigureLink> Figures = new List<ClassLoreFigureLink>();
        public List<ClassLoreGlance> Glance = new List<ClassLoreGlance>();     // "At a glance": from the game data of the page (classes, races, armor, racials, start)
        public bool Any => Official.Length > 0 || (Notes != null && Notes.HasText) || Glance.Count > 0;
    }

    public sealed class ClassLoreFigureLink
    {
        public ClassLoreFigure Figure;
        public string Link;                          // npc:<id> when the game data has exactly this creature, else null (plain text)
    }

    // a line of "At a glance": a label and items, each linking to its page when it has one
    public sealed class ClassLoreGlance
    {
        public string Label = "", Source = "";
        public List<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>();   // text, link (null = plain)
        public string Text => string.Join(", ", Items.Select(i => i.Key));
    }

    public static class ClassLore
    {
        public const string Label = ZoneLore.Label;                                  // "Outpost notes": the same words as the zone notes
        public const string GameLabel = "From the game";
        public const string GameDataLabel = "Outpost notes, from the game's data";  // an entry written from the in-game data only
        public const string ResourceName = "Data.classlore.json";
        static readonly object gate = new object();
        static Dictionary<string, ClassLoreEntry> classes, races;
        static string problem;

        public static ClassLoreEntry ForClass(string classFile)
        {
            Ensure();
            lock (gate) return classes.TryGetValue((classFile ?? "").Trim().ToUpperInvariant(), out var e) ? e : null;
        }

        public static ClassLoreEntry ForRace(string raceName)
        {
            Ensure();
            lock (gate) return races.TryGetValue(Norm(raceName), out var e) ? e : null;
        }

        public static IReadOnlyList<ClassLoreEntry> Classes { get { Ensure(); lock (gate) return classes.Values.ToList(); } }
        public static IReadOnlyList<ClassLoreEntry> Races { get { Ensure(); lock (gate) return races.Values.ToList(); } }
        public static string Problem { get { Ensure(); return problem; } }

        static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();

        // ---- loading
        static void Ensure()
        {
            lock (gate)
            {
                if (classes != null) return;
                string json = null;
                try
                {
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                        if (s != null) using (var r = new StreamReader(s)) json = r.ReadToEnd();
                }
                catch (Exception e) { problem = e.Message; }
                Use(json);
            }
        }

        static void Use(string json)
        {
            if (json == null) { classes = new Dictionary<string, ClassLoreEntry>(); races = new Dictionary<string, ClassLoreEntry>(); problem = problem ?? "no class and race notes in this build"; return; }
            if (!Parse(json, out classes, out races, out var why)) { problem = why; Util.Log("class notes: " + why); }
        }

        public static void SetForTest(string json) { lock (gate) { classes = null; problem = null; Use(json); } }
        public static void ResetForTest() { lock (gate) { classes = null; races = null; problem = null; } }

        public static bool Parse(string json, out Dictionary<string, ClassLoreEntry> byClass, out Dictionary<string, ClassLoreEntry> byRace, out string why)
        {
            byClass = new Dictionary<string, ClassLoreEntry>(); byRace = new Dictionary<string, ClassLoreEntry>(); why = null;
            Dictionary<string, object> root;
            try { root = Json.Obj(json); }
            catch (Exception e) { why = "the class notes do not read: " + e.Message; return false; }
            if (root == null) { why = "the class notes are not an object"; return false; }
            foreach (var kind in new[] { "classes", "races" })
                foreach (var o in root.List(kind).OfType<Dictionary<string, object>>())
                {
                    var e = new ClassLoreEntry
                    {
                        Kind = kind == "classes" ? "class" : "race", Name = (o.Str("name") ?? "").Trim(),
                        Summary = (o.Str("summary") ?? "").Trim(), History = (o.Str("history") ?? "").Trim(), Forever = (o.Str("foreverNotes") ?? "").Trim(), Basis = (o.Str("basis") ?? "").Trim(),
                    };
                    e.Key = kind == "classes" ? (o.Str("file") ?? "").Trim().ToUpperInvariant() : Norm(e.Name);
                    if (e.Key.Length == 0 || e.Name.Length == 0) continue;
                    foreach (var f in o.List("facts").OfType<Dictionary<string, object>>())
                    {
                        var fact = new ClassLoreFact { Label = (f.Str("label") ?? "").Trim(), Value = (f.Str("value") ?? "").Trim() };
                        if (fact.Label.Length > 0 && fact.Value.Length > 0) e.Facts.Add(fact);
                    }
                    foreach (var f in o.List("figures").OfType<Dictionary<string, object>>())
                    {
                        var fig = new ClassLoreFigure { Name = (f.Str("name") ?? "").Trim(), Why = (f.Str("why") ?? "").Trim() };
                        if (fig.Name.Length > 0) e.Figures.Add(fig);
                    }
                    e.Mentions = o.List("mentions").OfType<string>().Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    e.Sources = o.List("sources").OfType<string>().Where(x => ZoneLore.SourceName(x).Length > 0).ToList();
                    (e.Kind == "class" ? byClass : byRace)[e.Key] = e;
                }
            if (byClass.Count + byRace.Count == 0) { why = "the class notes hold no class and no race"; return false; }
            return true;
        }
    }
}
