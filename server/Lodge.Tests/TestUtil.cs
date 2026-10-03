using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lodge.Tests;

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    public void Advance(TimeSpan t) => UtcNow += t;
}

// a temp folder per test, removed afterwards
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lodge-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

// logs captured in memory, to check that no secret ever reaches them
public sealed class CaptureLogger : ILogger
{
    public readonly List<string> Lines = new();
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines) Lines.Add(formatter(state, exception) + (exception != null ? " " + exception : ""));
    }
}

public static class T
{
    public static ILogger Log => NullLogger.Instance;

    // synthetic codes only - never real ones
    public const string ElanCode = "synthetic0elan0code0000000000001";
    public const string BobCode = "synthetic0bob00code0000000000002";

    public static LodgeConfig Cfg(string dir, Action<LodgeConfig>? tweak = null)
    {
        var c = LodgeConfig.ForTests(dir);
        tweak?.Invoke(c);
        return c;
    }

    public static void WriteCodes(LodgeConfig c, params string[] lines) => File.WriteAllText(c.CodesFile, string.Join("\n", lines) + "\n");

    // a stream that hands out data slowly (or fails), to test uploads in flight
    public sealed class SlowStream : Stream
    {
        readonly long length;
        readonly int chunk, delayMs;
        readonly long failAt;
        long pos;
        public SlowStream(long length, int chunk = 64 * 1024, int delayMs = 0, long failAt = -1)
        { this.length = length; this.chunk = chunk; this.delayMs = delayMs; this.failAt = failAt; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            if (failAt >= 0 && pos >= failAt) throw new IOException("synthetic network failure");
            var n = (int)Math.Min(Math.Min(count, chunk), length - pos);
            Array.Fill(buffer, (byte)'x', offset, n);
            pos += n;
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var arr = new byte[buffer.Length];
            var n = await ReadAsync(arr, 0, arr.Length, ct);
            arr.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
