using System.Text;
using System.Text.Json.Nodes;

namespace Lodge;

// Pinned messages per text channel: data/pins.json = { "<channelId>": [ pin, ... ] }, at most MaxPerChannel each.
// A pin carries its own copy of text/by/at, so it survives the message falling out of the (bounded) history.
// Saved atomically (tmp file, flush, rename); a leftover tmp from a crash is used only if the main file is missing.
// The file is read with a size cap so a bad file can't blow up memory. Failures are logged without any text.
public sealed class PinStore
{
    public const int MaxPerChannel = 25;
    public const int MaxText = 300;
    const long MaxFileBytes = 2 * 1024 * 1024;

    readonly string path, tmp;
    readonly ILogger log;
    readonly object gate = new();
    readonly Dictionary<string, List<JsonObject>> pins = new();

    public PinStore(string dataDir, ILogger log)
    {
        this.log = log;
        path = Path.Combine(dataDir, "pins.json");
        tmp = path + ".tmp";
        try
        {
            if (File.Exists(tmp)) { if (!File.Exists(path)) File.Move(tmp, path); else File.Delete(tmp); }
            if (File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes)
            {
                var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                if (root != null)
                    foreach (var kv in root)
                    {
                        if (kv.Value is not JsonArray arr) continue;
                        var list = new List<JsonObject>();
                        foreach (var p in arr)
                            if (p is JsonObject o && (string)o["id"] is { Length: > 0 } && list.Count < MaxPerChannel) list.Add(o.DeepClone().AsObject());
                        if (list.Count > 0) pins[kv.Key] = list;
                    }
            }
        }
        catch (Exception e) { log?.LogWarning("Pins: load failed: {Error}", e.GetType().Name); }
    }

    public JsonArray For(string channel)
    {
        lock (gate) return new JsonArray(Get(channel).Select(p => (JsonNode)p.DeepClone()).ToArray());
    }

    List<JsonObject> Get(string channel) => pins.TryGetValue(channel, out var l) ? l : new List<JsonObject>();

    public bool Has(string channel, string id) { lock (gate) return Get(channel).Any(p => (string)p["id"] == id); }

    public enum Result { Ok, Full, Already, SaveFailed }

    // saved = false when the write failed (the pin is still active in memory)
    public Result Pin(string channel, JsonObject pin)
    {
        lock (gate)
        {
            var id = (string)pin["id"];
            if (Get(channel).Any(p => (string)p["id"] == id)) return Result.Already;
            if (Get(channel).Count >= MaxPerChannel) return Result.Full;
            if (!pins.TryGetValue(channel, out var l)) pins[channel] = l = new List<JsonObject>();
            l.Add(pin);
            return Save() ? Result.Ok : Result.SaveFailed;
        }
    }

    public bool Unpin(string channel, string id, out bool saved)
    {
        lock (gate)
        {
            saved = true;
            if (!pins.TryGetValue(channel, out var l)) return false;
            var p = l.FirstOrDefault(x => (string)x["id"] == id);
            if (p == null) return false;
            l.Remove(p);
            if (l.Count == 0) pins.Remove(channel);
            saved = Save();
            return true;
        }
    }

    // keep the stored copy in step with an edit; returns the updated pin or null
    public JsonObject UpdateText(string channel, string id, string text)
    {
        lock (gate)
        {
            var p = Get(channel).FirstOrDefault(x => (string)x["id"] == id);
            if (p == null) return null;
            p["text"] = Clip(text);
            Save();
            return (JsonObject)p.DeepClone();
        }
    }

    public void DropChannel(string channel)
    {
        lock (gate) { if (pins.Remove(channel)) Save(); }
    }

    public static string Clip(string text)
    {
        text ??= "";
        return text.Length > MaxText ? text[..MaxText] : text;
    }

    bool Save()
    {
        try
        {
            var root = new JsonObject();
            foreach (var kv in pins) root[kv.Key] = new JsonArray(kv.Value.Select(p => (JsonNode)p.DeepClone()).ToArray());
            var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            log?.LogWarning("Pins: save failed: {Error}", e.GetType().Name);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }
}
