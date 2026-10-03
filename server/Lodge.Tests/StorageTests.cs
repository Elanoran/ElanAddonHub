using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Finding 2: bounded uploads and bounded chat history.
public class FileStoreTests
{
    const long MB = 1024 * 1024;

    static LodgeConfig Cfg(TempDir d, int maxFileMb = 25, long storageMb = 2048, int files = 2000, int concurrent = 4, int per10 = 30) =>
        new()
        {
            Code = "", CodesFile = Path.Combine(d.Path, "codes"), Urls = "", DataDir = d.Path, PathBase = "",
            MaxFileMb = maxFileMb, FileDays = 30, MaxUsers = 25, TrustedProxies = Array.Empty<string>(),
            MaxStorageBytes = storageMb * MB, MaxFiles = files, MaxConcurrentUploads = concurrent, UploadsPer10Min = per10,
            HistoryMaxBytes = MB, MaxTrackedIps = 10000, HttpPerMinute = 240, AllowQueryCode = true,
        };

    static Task<FileMeta> Up(FileStore s, long size, long? declared = -1, string who = "p:elan", int delayMs = 0, long failAt = -1) =>
        s.Save(new T.SlowStream(size, delayMs: delayMs, failAt: failAt), declared == -1 ? size : declared, "test.bin", "Elan", who, CancellationToken.None);

    static string[] Leftovers(TempDir d) => Directory.GetFiles(Path.Combine(d.Path, "files"), "*.part")
        .Concat(Directory.GetFiles(Path.Combine(d.Path, "files"), "*.tmp")).ToArray();

    [Fact]
    public async Task Boundary_size_is_accepted_one_byte_more_is_not()
    {
        using var d = new TempDir();
        var s = new FileStore(Cfg(d, maxFileMb: 1), new FakeClock());
        Assert.Equal(MB, (await Up(s, MB)).Size);
        await Assert.ThrowsAsync<FileTooBigException>(() => Up(s, MB + 1));                 // declared too big
        await Assert.ThrowsAsync<FileTooBigException>(() => Up(s, MB + 1, declared: null)); // no length, body too big
        Assert.Empty(Leftovers(d));
        Assert.Equal(MB, s.UsedBytes);
        Assert.Equal(0, s.ReservedBytes);
    }

    [Fact]
    public async Task A_lying_content_length_cant_write_past_its_reservation()
    {
        using var d = new TempDir();
        var s = new FileStore(Cfg(d, maxFileMb: 5), new FakeClock());
        await Assert.ThrowsAsync<FileTooBigException>(() => Up(s, 2 * MB, declared: MB));
        Assert.Equal(0, s.UsedBytes);
        Assert.Empty(Leftovers(d));
    }

    [Fact]
    public async Task Total_storage_limit_refuses_new_uploads_without_deleting_old_ones()
    {
        using var d = new TempDir();
        var s = new FileStore(Cfg(d, maxFileMb: 2, storageMb: 3), new FakeClock());
        var first = await Up(s, 2 * MB);
        var e = await Assert.ThrowsAsync<UploadRefusedException>(() => Up(s, 2 * MB));
        Assert.Equal(507, e.Status);
        Assert.NotNull(s.Get(first.Id));     // nothing deleted to make room
        await Up(s, MB);                     // what fits still fits
        Assert.Equal(3 * MB, s.UsedBytes);
    }

    [Fact]
    public async Task Two_concurrent_uploads_cant_both_take_the_last_space()
    {
        using var d = new TempDir();
        var s = new FileStore(Cfg(d, maxFileMb: 2, storageMb: 3), new FakeClock());
        await Up(s, 2 * MB);                                   // 1 MB left
        var a = Up(s, MB, delayMs: 5);
        var b = Up(s, MB, delayMs: 5);
        var results = await Task.WhenAll(a.ContinueWith(t => t.IsCompletedSuccessfully), b.ContinueWith(t => t.IsCompletedSuccessfully));
        Assert.Equal(1, results.Count(ok => ok));
        Assert.Equal(3 * MB, s.UsedBytes);
        Assert.Equal(0, s.ReservedBytes);
    }

    [Fact]
    public async Task Aborted_upload_leaves_nothing_and_releases_its_reservation()
    {
        using var d = new TempDir();
        var s = new FileStore(Cfg(d), new FakeClock());
        await Assert.ThrowsAsync<IOException>(() => Up(s, 3 * MB, failAt: MB));
        Assert.Empty(Leftovers(d));
        Assert.Equal(0, s.ReservedBytes);
        Assert.Equal(0, s.FileCount);
        using var cts = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            s.Save(new T.SlowStream(3 * MB, chunk: 1024, delayMs: 5), 3 * MB, "a", "Elan", "p:elan", cts.Token));
        Assert.Empty(Leftovers(d));
        Assert.Equal(0, s.ReservedBytes);
    }

    [Fact]
    public async Task Concurrent_upload_slots_are_bounded()
    {
        using var d = new TempDir();
        var s = new FileStore(Cfg(d, concurrent: 1), new FakeClock());
        var slow = Up(s, MB, delayMs: 20);
        await Task.Delay(30);
        var e = await Assert.ThrowsAsync<UploadRefusedException>(() => Up(s, 10));
        Assert.Equal(429, e.Status);
        await slow;
        await Up(s, 10); // free again
    }

    [Fact]
    public async Task Upload_rate_is_per_person_and_recovers()
    {
        using var d = new TempDir();
        var clock = new FakeClock();
        var s = new FileStore(Cfg(d, per10: 2), clock);
        await Up(s, 10); await Up(s, 10);
        Assert.Equal(429, (await Assert.ThrowsAsync<UploadRefusedException>(() => Up(s, 10))).Status);
        await Up(s, 10, who: "p:bob");               // someone else isn't affected
        clock.Advance(TimeSpan.FromMinutes(11));
        await Up(s, 10);
    }

    [Fact]
    public async Task File_count_limit()
    {
        using var d = new TempDir();
        var s = new FileStore(Cfg(d, files: 2), new FakeClock());
        await Up(s, 10); await Up(s, 10);
        Assert.Equal(507, (await Assert.ThrowsAsync<UploadRefusedException>(() => Up(s, 10))).Status);
    }

    [Fact]
    public async Task Restart_reconciles_usage_and_removes_partial_files()
    {
        using var d = new TempDir();
        var s = new FileStore(Cfg(d), new FakeClock());
        await Up(s, MB); await Up(s, 2 * MB);
        var dir = Path.Combine(d.Path, "files");
        File.WriteAllText(Path.Combine(dir, "deadbeefdeadbeefdeadbeefdeadbeef.part"), "crash leftover");
        File.WriteAllText(Path.Combine(dir, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.bin"), "no metadata");
        var again = new FileStore(Cfg(d), new FakeClock());
        Assert.Equal(3 * MB, again.UsedBytes);
        Assert.Equal(2, again.FileCount);
        Assert.Empty(Leftovers(d));
        Assert.False(File.Exists(Path.Combine(dir, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.bin")));
    }

    [Fact]
    public async Task Expiry_frees_capacity()
    {
        using var d = new TempDir();
        var clock = new FakeClock { UtcNow = DateTime.UtcNow };
        var s = new FileStore(Cfg(d, maxFileMb: 2, storageMb: 2), clock);
        var f = await Up(s, 2 * MB);
        await Assert.ThrowsAsync<UploadRefusedException>(() => Up(s, MB));
        clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, s.Expire());
        Assert.Equal(0, s.UsedBytes);
        Assert.Null(s.Get(f.Id));
        await Up(s, MB);
    }

    [Fact]
    public async Task Metadata_write_failure_cleans_up()
    {
        using var d = new TempDir();
        var s = new FileStore(Cfg(d), new FakeClock());
        // a folder where the metadata temp file would go makes the metadata write fail
        var dir = Path.Combine(d.Path, "files");
        var watcher = Task.Run(async () =>
        {
            for (int i = 0; i < 400; i++)
            {
                foreach (var p in Directory.GetFiles(dir, "*.part"))
                    try { Directory.CreateDirectory(Path.ChangeExtension(p, ".json.tmp")); return; } catch { }
                await Task.Delay(1);
            }
        });
        await Assert.ThrowsAnyAsync<Exception>(() => Up(s, 4 * MB, delayMs: 2));
        await watcher;
        Assert.Empty(Directory.GetFiles(dir, "*.part"));
        Assert.Equal(0, s.ReservedBytes);
        Assert.Equal(0, s.FileCount);
    }
}

public class HistoryTests
{
    static JsonObject Msg(int i, bool file = false)
    {
        var m = new JsonObject { ["t"] = "msg", ["id"] = $"m{i:D6}", ["at"] = i, ["from"] = "Elan", ["text"] = $"message {i}" };
        if (file) m["file"] = new JsonObject { ["id"] = "f" + i, ["name"] = $"pic{i}.png", ["size"] = 123, ["mime"] = "image/png" };
        return m;
    }

    [Fact]
    public void Oversized_existing_history_loads_only_a_bounded_tail()
    {
        using var d = new TempDir();
        var path = HistoryStore.FileOf(d.Path, "general");
        using (var w = new StreamWriter(path))
            for (int i = 0; i < 200_000; i++) w.Write(Msg(i).ToJsonString() + "\n"); // ~15 MB
        Assert.True(new FileInfo(path).Length > 10_000_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var h = new HistoryStore(d.Path, "general", 256 * 1024, T.Log);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8_000_000, $"allocated {allocated} bytes");
        Assert.Equal(HistoryStore.Keep, h.Messages.Count);
        Assert.Equal("m199999", (string?)h.Messages[^1]["id"]);                  // newest kept, in order
        Assert.Equal("m199800", (string?)h.Messages[0]["id"]);
        h.Append(Msg(200_000));                                                  // first write compacts
        Assert.True(new FileInfo(path).Length <= 256 * 1024);
    }

    [Fact]
    public void Appends_compact_and_keep_order_and_attachments()
    {
        using var d = new TempDir();
        var h = new HistoryStore(d.Path, "loot", 1024 * 1024, T.Log);
        for (int i = 0; i < 1000; i++) Assert.True(h.Append(Msg(i, file: i % 10 == 0)));
        var again = new HistoryStore(d.Path, "loot", 1024 * 1024, T.Log);
        Assert.Equal(HistoryStore.Keep, again.Messages.Count);
        Assert.Equal(Enumerable.Range(800, 200).Select(i => $"m{i:D6}"), again.Messages.Select(m => (string?)m["id"]));
        Assert.Equal("pic990.png", (string?)again.Messages.First(m => (string?)m["id"] == "m000990")["file"]!["name"]);
        Assert.True(File.ReadAllLines(HistoryStore.FileOf(d.Path, "loot")).Length <= HistoryStore.Keep * 2);
    }

    [Fact]
    public void Byte_limit_holds_with_long_messages()
    {
        using var d = new TempDir();
        var h = new HistoryStore(d.Path, "general", 64 * 1024, T.Log);
        for (int i = 0; i < 300; i++) { var m = Msg(i); m["text"] = new string('x', 2000); h.Append(m); }
        Assert.True(new FileInfo(HistoryStore.FileOf(d.Path, "general")).Length <= 64 * 1024);
    }

    [Fact]
    public void Interrupted_compaction_recovers()
    {
        using var d = new TempDir();
        var path = HistoryStore.FileOf(d.Path, "general");
        File.WriteAllText(path, Msg(1).ToJsonString() + "\n" + Msg(2).ToJsonString() + "\n");
        File.WriteAllText(path + ".compact.tmp", "{\"broken");              // crash before the rename
        var h = new HistoryStore(d.Path, "general", 1024 * 1024, T.Log);
        Assert.Equal(2, h.Messages.Count);
        Assert.False(File.Exists(path + ".compact.tmp"));

        File.Delete(path);                                                     // only the finished tmp is left
        File.WriteAllText(path + ".compact.tmp", Msg(7).ToJsonString() + "\n");
        var h2 = new HistoryStore(d.Path, "general", 1024 * 1024, T.Log);
        Assert.Equal("m000007", (string?)h2.Messages.Single()["id"]);
    }

    [Fact]
    public void Write_failure_is_reported_not_hidden()
    {
        using var d = new TempDir();
        var log = new CaptureLogger();
        var h = new HistoryStore(d.Path, "general", 1024 * 1024, log);
        Assert.True(h.Append(Msg(1)));
        var path = HistoryStore.FileOf(d.Path, "general");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            if (OperatingSystem.IsWindows()) Assert.False(h.Append(Msg(2)));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        Assert.DoesNotContain(log.Lines, l => l.Contains("message 2")); // never the text
    }
}
