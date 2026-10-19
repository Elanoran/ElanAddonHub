using System.Text.Json;
using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// The lodge's display name and a short welcome text (2.12). The owner sets them from Settings > Guild Master; they are shown on the
// public invite page, in the Outpost's join banner and in the welcome frame. Public by design (an invitee sees them before joining),
// so keep secrets out of them. Stored as <data>/lodge-info.json (atomic replace), validated and length-capped here.
public sealed class LodgeInfoStore
{
    public const int MaxName = 40;
    public const int MaxWelcome = 280;
    public const int MaxWelcomeLines = 4;
    public const string DefaultName = "the lodge";

    readonly string path;
    readonly object gate = new();
    string name = "", welcome = "";

    public LodgeInfoStore(string dataDir)
    {
        path = Path.Combine(dataDir, "lodge-info.json");
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
            {
                name = Clean(J.Str(o, "name"), out _) ?? "";
                welcome = CleanWelcome(J.Str(o, "welcome"), out _) ?? "";
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public string Name { get { lock (gate) return name; } }
    public string Welcome { get { lock (gate) return welcome; } }
    public string DisplayName { get { lock (gate) return name.Length > 0 ? name : DefaultName; } }

    // lodge.info.lodge and welcome.lodge (typed: shared/Elans.Shared/Lodge/AdminMessages.cs)
    public LodgeInfoView View() { lock (gate) return new LodgeInfoView { Name = name, Welcome = welcome }; }
    public JsonObject ToJson() => LodgeFieldsJson.Build(View().Write);

    // one line, cleaned like every other name-like text (control, bidi and format characters out); null + error when too long
    public static string Clean(string s, out string error)
    {
        error = null;
        var r = Names.Full(s ?? "").Trim();
        if (r.Length > MaxName) { error = $"The lodge name can be at most {MaxName} characters"; return null; }
        return r;
    }

    public static string CleanWelcome(string s, out string error)
    {
        error = null;
        var r = Names.MessageText(s ?? "");
        if (r.Length > MaxWelcome) { error = $"The welcome text can be at most {MaxWelcome} characters"; return null; }
        if (r.Split('\n').Length > MaxWelcomeLines) { error = $"The welcome text can have at most {MaxWelcomeLines} lines"; return null; }
        return r;
    }

    // throws InvalidOperationException with a readable message; an empty value clears it
    public void Set(string newName, string newWelcome)
    {
        var n = Clean(newName, out var e1);
        if (e1 != null) throw new InvalidOperationException(e1);
        var w = CleanWelcome(newWelcome, out var e2);
        if (e2 != null) throw new InvalidOperationException(e2);
        lock (gate)
        {
            Auth.AtomicWrite(path, new JsonObject { ["name"] = n, ["welcome"] = w }.ToJsonString());
            name = n; welcome = w;
        }
    }
}
