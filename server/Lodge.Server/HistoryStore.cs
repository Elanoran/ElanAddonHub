using System.Text;
using System.Text.Json.Nodes;

namespace Lodge;

// One text channel's history on disk: data/history/<channel>.jsonl, one message per line.
//  - Loading reads only the tail of the file (at most MaxBytes), so an oversized file can't blow up memory.
//  - New messages are appended; when the file passes Keep*2 records or MaxBytes it is compacted to the newest Keep
//    records: written to <file>.compact.tmp, flushed, then renamed over the file (atomic). A crash mid-compaction
//    leaves either the old file or the new one - and at startup a leftover tmp is used only if the main file is missing.
//  - Write failures are logged (channel only, never message text) and reported to the caller.
public sealed class HistoryStore
{
    public const int Keep = 200;
    readonly string path, tmp;
    readonly long maxBytes;
    readonly ILogger log;
    readonly string channel;
    int recordsOnDisk;
    long bytesOnDisk;

    public List<JsonObject> Messages { get; } = new();

    public HistoryStore(string dir, string channel, long maxBytes, ILogger log)
    {
        this.channel = channel;
        this.maxBytes = Math.Max(16 * 1024, maxBytes);
        this.log = log;
        path = Path.Combine(dir, channel + ".jsonl");
        tmp = path + ".compact.tmp";
        Recover();
        Load();
    }

    void Recover()
    {
        try
        {
            if (File.Exists(tmp))
            {
                if (!File.Exists(path)) File.Move(tmp, path); // crashed after deleting... never happens with rename, but be safe
                else File.Delete(tmp);                       // crashed before the rename: the old file is complete
            }
        }
        catch (Exception e) { log?.LogWarning("History {Channel}: recovery failed: {Error}", channel, e.GetType().Name); }
    }

    void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            bytesOnDisk = fs.Length;
            long start = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var tail = new byte[fs.Length - start];
            int read = 0;
            while (read < tail.Length) { var n = fs.Read(tail, read, tail.Length - read); if (n <= 0) break; read += n; }
            var text = Encoding.UTF8.GetString(tail, 0, read);
            var lines = text.Split('\n');
            int first = start > 0 ? 1 : 0; // the first line of a mid-file tail is cut off
            var parsed = new List<JsonObject>();
            for (int i = first; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                try { var m = JsonNode.Parse(lines[i])?.AsObject(); if (m != null) { m["channel"] = channel; parsed.Add(m); } } catch { }
            }
            recordsOnDisk = parsed.Count + (start > 0 ? Keep * 2 : 0); // a cut file: compact on the next write
            Messages.AddRange(parsed.Count > Keep ? parsed.GetRange(parsed.Count - Keep, Keep) : parsed);
        }
        catch (Exception e) { log?.LogWarning("History {Channel}: load failed: {Error}", channel, e.GetType().Name); }
    }

    // false = not saved (the message is still delivered live)
    public bool Append(JsonObject msg)
    {
        Messages.Add(msg);
        if (Messages.Count > Keep) Messages.RemoveRange(0, Messages.Count - Keep);
        try
        {
            var line = msg.ToJsonString() + "\n";
            if (recordsOnDisk + 1 > Keep * 2 || bytesOnDisk + line.Length > maxBytes) return Compact();
            File.AppendAllText(path, line);
            recordsOnDisk++;
            bytesOnDisk += Encoding.UTF8.GetByteCount(line);
            return true;
        }
        catch (Exception e)
        {
            log?.LogWarning("History {Channel}: write failed: {Error}", channel, e.GetType().Name);
            return false;
        }
    }

    // after an edit or delete, or when the file got too big: the newest Keep records, written atomically
    public bool Compact()
    {
        try
        {
            var sb = new StringBuilder();
            var keep = Messages.Count > Keep ? Messages.GetRange(Messages.Count - Keep, Keep) : Messages;
            foreach (var m in keep) sb.Append(m.ToJsonString()).Append('\n');
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            while (bytes.Length > maxBytes && keep.Count > 1) // very long messages: drop the oldest until it fits
            {
                keep = keep.GetRange(1, keep.Count - 1);
                sb.Clear();
                foreach (var m in keep) sb.Append(m.ToJsonString()).Append('\n');
                bytes = Encoding.UTF8.GetBytes(sb.ToString());
            }
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            recordsOnDisk = keep.Count;
            bytesOnDisk = bytes.Length;
            return true;
        }
        catch (Exception e)
        {
            log?.LogWarning("History {Channel}: compaction failed: {Error}", channel, e.GetType().Name);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    public static string FileOf(string dir, string channel) => Path.Combine(dir, channel + ".jsonl");
}
