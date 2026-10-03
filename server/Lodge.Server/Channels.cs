using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lodge;

public class Channel
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Type { get; set; } = "text";   // text | voice
    public string MinRole { get; set; } = "guest";
    public int Max { get; set; }                  // voice rooms: 0 = no limit

    public bool VisibleTo(string role) => Roles.AtLeast(role, MinRole);

    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["name"] = Name, ["type"] = Type, ["minRole"] = MinRole, ["max"] = Max,
    };
}

// data/channels.json - edit by hand or from the hub's Guild Master panel.
public class Channels
{
    readonly string path;
    readonly object gate = new();
    List<Channel> list;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public Channels(string dataDir)
    {
        path = Path.Combine(dataDir, "channels.json");
        if (File.Exists(path))
        {
            try { list = JsonSerializer.Deserialize<List<Channel>>(File.ReadAllText(path), Json); } catch { list = null; }
        }
        if (list == null || list.Count(c => c.Type == "text") == 0)
        {
            list = new List<Channel>
            {
                new() { Id = "general", Name = "General" },
                new() { Id = "loot", Name = "Loot & trades" },
                new() { Id = "officers", Name = "Officers", MinRole = "officer" },
                new() { Id = "hangout", Name = "Hangout", Type = "voice" },
                new() { Id = "dungeon", Name = "Dungeon group", Type = "voice", Max = 5 },
            };
            Save();
        }
        foreach (var c in list) { c.MinRole = Roles.Normalize(c.MinRole); if (c.Type != "voice") c.Type = "text"; }
    }

    void Save() => File.WriteAllText(path, JsonSerializer.Serialize(list, Json));

    public IEnumerable<Channel> All { get { lock (gate) return list.ToList(); } }
    public IEnumerable<Channel> VisibleTo(string role) => All.Where(c => c.VisibleTo(role));
    public Channel Get(string id) { lock (gate) return list.FirstOrDefault(c => c.Id == id); }

    // where messages from old hubs (no channel) and the first voice join go
    public Channel DefaultText => All.FirstOrDefault(c => c.Type == "text" && c.MinRole == "guest") ?? All.First(c => c.Type == "text");
    public Channel DefaultVoice(string role) => VisibleTo(role).FirstOrDefault(c => c.Type == "voice");

    public Channel Add(string name, string type, string minRole, int max)
    {
        lock (gate)
        {
            var slug = new string(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
            if (slug.Length == 0) slug = "channel";
            if (slug.Length > 24) slug = slug[..24];
            var id = slug;
            for (int i = 2; list.Any(c => c.Id == id); i++) id = $"{slug}-{i}";
            var ch = new Channel
            {
                Id = id, Name = name, Type = type == "voice" ? "voice" : "text",
                MinRole = Roles.Normalize(minRole), Max = Math.Clamp(max, 0, 99),
            };
            list.Add(ch);
            Save();
            return ch;
        }
    }

    public bool Remove(string id)
    {
        lock (gate)
        {
            var ch = list.FirstOrDefault(c => c.Id == id);
            if (ch == null) return false;
            if (ch.Type == "text" && list.Count(c => c.Type == "text") <= 1) return false; // keep one text channel
            list.Remove(ch);
            Save();
            return true;
        }
    }
}
