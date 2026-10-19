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
    public const int DefaultKeep = 2000;
    public int Keep { get; }   // records kept per channel (LODGE_HISTORY_MAX); the file compacts at twice that
    readonly string path, tmp;
    readonly long maxBytes;
    readonly ILogger log;
    readonly string channel;
    int recordsOnDisk;
    long bytesOnDisk;

    public List<JsonObject> Messages { get; } = new();

    // edits, deletes and reactions change the messages in memory at once but reach the disk later (the owner of the
    // store flushes after a short quiet period, on shutdown and when the file gets too big): a burst of reactions
    // costs one rewrite instead of one each. Callers hold lock(store).
    public bool Dirty { get; private set; }
    public void MarkDirty() => Dirty = true;
    public bool Flush() => !Dirty || Compact();

    public HistoryStore(string dir, string channel, long maxBytes, ILogger log, int keep = DefaultKeep)
    {
        Keep = Math.Max(10, keep);
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
                try { var rec = JsonNode.Parse(lines[i])?.AsObject(); if (rec != null) { rec["channel"] = channel; parsed.Add(rec); } } catch { }
            }
            bool migrated = false;
            foreach (var rec in parsed) migrated |= ChatModule.MigrateReactions(rec); // 2.4: stored emoji reactions become ids
            recordsOnDisk = parsed.Count + (start > 0 || migrated ? Keep * 2 : 0); // a cut or migrated file: compact on the next write
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
            if (Messages.Count > Keep) Messages.RemoveRange(0, Messages.Count - Keep);
            // every record is serialized once; when the total is over the byte cap the oldest go until it is comfortably under (75 %), so the
            // next appends don't each trigger another rewrite. What goes is gone from memory too: memory holds what survives a restart.
            var lines = new List<byte[]>(Messages.Count);
            long total = 0;
            foreach (var m in Messages) { var b = Encoding.UTF8.GetBytes(m.ToJsonString() + "\n"); lines.Add(b); total += b.Length; }
            int first = 0;
            if (total > maxBytes)
            {
                long target = maxBytes / 4 * 3;
                while (first < lines.Count - 1 && total > target) total -= lines[first++].Length;
                Messages.RemoveRange(0, first);
            }
            var bytes = new byte[total];
            long off = 0;
            for (int i = first; i < lines.Count; i++) { Buffer.BlockCopy(lines[i], 0, bytes, (int)off, lines[i].Length); off += lines[i].Length; }
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            recordsOnDisk = Messages.Count;
            bytesOnDisk = bytes.Length;
            Dirty = false;
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
