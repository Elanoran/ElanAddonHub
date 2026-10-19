using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The receiving side of the Lodge link against a hostile Lodge (LodgeFrames, LodgeInbox, LodgeJson.Parse): a fake WebSocket plays the server.
public class LodgeFramesTests
{
    // a server that sends the given messages, each cut into pieces of `chunk` bytes; a message can be produced lazily (a 100 MB one is
    // never held in memory) - `Delivered` counts what the reader actually took
    sealed class FakeServer : WebSocket
    {
        readonly Queue<(long Length, Func<long, byte> ByteAt, WebSocketMessageType Type)> queue = new();
        readonly int chunk;
        long pos; public long Delivered;
        public FakeServer(int chunk = 16 * 1024) { this.chunk = chunk; }
        public FakeServer Text(string s) { var b = Encoding.UTF8.GetBytes(s); queue.Enqueue((b.Length, i => b[i], WebSocketMessageType.Text)); return this; }
        public FakeServer Bytes(byte[] b) { queue.Enqueue((b.Length, i => b[i], WebSocketMessageType.Text)); return this; }
        public FakeServer Huge(long length, Func<long, byte> at) { queue.Enqueue((length, at, WebSocketMessageType.Text)); return this; }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (queue.Count == 0) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            var (len, at, type) = queue.Peek();
            int n = (int)Math.Min(Math.Min(chunk, buffer.Count), len - pos);
            for (int i = 0; i < n; i++) buffer.Array[buffer.Offset + i] = at(pos + i);
            pos += n; Delivered += n;
            bool end = pos >= len;
            if (end) { queue.Dequeue(); pos = 0; }
            return Task.FromResult(new WebSocketReceiveResult(n, type, end));
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool end, CancellationToken ct) => Task.CompletedTask;
    }

    // the Outpost's receive loop in short (src/Services/LodgeClient.cs ReceiveFrames): read, parse, skip what doesn't parse, stop when too big
    static async Task<(List<JsonObject> Got, LodgeFrames.Kind Last)> Receive(FakeServer ws, int max = LodgeJson.MaxFrameFromLodge)
    {
        var got = new List<JsonObject>(); var buf = new byte[16 * 1024]; var msg = new MemoryStream();
        while (true)
        {
            var kind = await LodgeFrames.ReadAsync(ws, buf, msg, max, CancellationToken.None);
            if (kind is LodgeFrames.Kind.Close or LodgeFrames.Kind.TooBig) return (got, kind);
            Assert.True(msg.Length <= max);
            if (LodgeJson.Parse(msg.GetBuffer(), 0, (int)msg.Length) is JsonObject o) got.Add(o);
        }
    }

    [Fact]
    public async Task A_100_MB_frame_is_never_read_to_its_end()
    {
        long hundredMb = 100L * 1024 * 1024;
        var ws = new FakeServer().Text("{\"t\":\"ping\"}").Huge(hundredMb, i => i == 0 ? (byte)'"' : (byte)'x').Text("{\"t\":\"after\"}");
        var (got, last) = await Receive(ws);
        Assert.Equal(LodgeFrames.Kind.TooBig, last);                               // the caller drops the connection here
        Assert.Single(got);                                                        // the frame before it was handled
        Assert.True(ws.Delivered <= 12 + LodgeJson.MaxFrameFromLodge + 16 * 1024, "read " + ws.Delivered + " bytes of a 100 MB frame");
    }

    [Fact]
    public async Task A_200k_deep_frame_is_skipped_and_the_next_one_still_read()
    {
        var deep = "{\"t\":\"x\",\"a\":" + new string('[', 200_000) + new string(']', 200_000) + "}";
        var ws = new FakeServer().Text(deep).Text("{\"t\":\"ok\"}");
        var (got, last) = await Receive(ws);
        Assert.Equal(LodgeFrames.Kind.Close, last);
        Assert.Equal("ok", LodgeJson.Str(Assert.Single(got), "t"));
    }

    [Fact]
    public async Task A_duplicate_key_frame_is_refused_cleanly_and_the_loop_goes_on()
    {
        // L11: a key twice used to throw only when the object was first read (J.Str(m, "t") in the hub, outside its per-message catch).
        // Now the parse refuses it, nothing is left to throw later, and the following frames are read as usual.
        var ws = new FakeServer(chunk: 7).Text("{\"t\":\"msg\",\"t\":\"admin.purge\"}").Text("{\"t\":\"a\",\"x\":{\"y\":1,\"y\":2}}").Text("{\"t\":\"ok\",\"n\":1}");
        var (got, last) = await Receive(ws);
        Assert.Equal(LodgeFrames.Kind.Close, last);
        var only = Assert.Single(got);
        Assert.Equal("ok", LodgeJson.Str(only, "t"));
        Assert.Equal(1, new LodgeJsonFields(only).Int("n"));
        // the same through the text overload and with the frame already split by the reader
        Assert.Null(LodgeJson.Parse("{\"t\":\"msg\",\"t\":\"x\"}"));
    }

    [Fact]
    public async Task Frames_in_pieces_arrive_whole_and_a_cap_is_exact()
    {
        var text = "{\"t\":\"msg\",\"text\":\"" + new string('a', 50_000) + "\"}";
        var ws = new FakeServer(chunk: 1000).Text(text);
        var (got, _) = await Receive(ws);
        Assert.Equal(50_000, LodgeJson.Str(Assert.Single(got), "text").Length);
        var exact = Encoding.UTF8.GetBytes(text).Length;
        Assert.Equal(LodgeFrames.Kind.Close, (await Receive(new FakeServer(chunk: 1000).Text(text), exact)).Last);
        Assert.Equal(LodgeFrames.Kind.TooBig, (await Receive(new FakeServer(chunk: 1000).Text(text), exact - 1)).Last);
    }

    [Fact]
    public void The_inbox_refuses_once_too_much_waits()
    {
        var box = new LodgeInbox(maxFrames: 3, maxBytes: 100);
        Assert.True(box.Add(10)); Assert.True(box.Add(10)); Assert.True(box.Add(10));
        Assert.False(box.Add(10));                                                 // a fourth frame waiting: drop the connection
        Assert.Equal(3, box.Frames); Assert.Equal(30, box.Bytes);                  // the refused one is not counted
        box.Done(10);
        Assert.True(box.Add(10));
        var bytes = new LodgeInbox(maxFrames: 100, maxBytes: 100);
        Assert.True(bytes.Add(60)); Assert.False(bytes.Add(41)); Assert.True(bytes.Add(40));
        Assert.Equal(2000, LodgeInbox.DefaultMaxFrames);
    }

    // a stream of `length` bytes that never holds them; Read counts what was taken
    sealed class Endless : Stream
    {
        readonly long length; long pos; readonly byte fill;
        public Endless(long length, byte fill) { this.length = length; this.fill = fill; }
        public long Taken => pos;
        public override int Read(byte[] buffer, int offset, int count) { int n = (int)Math.Min(count, length - pos); for (int i = 0; i < n; i++) buffer[offset + i] = fill; pos += n; return n; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task An_HTTP_answer_is_read_to_at_most_its_cap()
    {
        Assert.Equal(64 * 1024, LodgeJson.MaxHttpAnswer);
        var huge = new Endless(100L * 1024 * 1024, (byte)' ');
        Assert.Null(await LodgeFrames.ReadAnswerAsync(huge, LodgeJson.MaxHttpAnswer));
        Assert.True(huge.Taken <= LodgeJson.MaxHttpAnswer + 8192, "read " + huge.Taken + " bytes");
        var ok = await LodgeFrames.ReadAnswerAsync(new MemoryStream(Encoding.UTF8.GetBytes("{\"token\":\"t\",\"guest\":false}")), LodgeJson.MaxHttpAnswer);
        Assert.Equal("t", LodgeJson.Str(ok, "token"));
        Assert.Null(await LodgeFrames.ReadAnswerAsync(new MemoryStream(Encoding.UTF8.GetBytes("<html>502</html>")), LodgeJson.MaxHttpAnswer));
        Assert.Null(await LodgeFrames.ReadAnswerAsync(new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":" + new string('[', 100_000) + new string(']', 100_000) + "}")), 1024 * 1024));
    }

    [Fact]
    public void The_Outpost_uses_the_capped_reader_the_inbox_and_the_capped_answers()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "server"))) root = Path.GetDirectoryName(root);
        var client = File.ReadAllText(Path.Combine(root, "src", "Services", "LodgeClient.cs"));
        Assert.Contains("LodgeFrames.ReadAsync(ws, buf, msg, LodgeJson.MaxFrameFromLodge, ct)", client);
        Assert.Contains("inbox.Add(len)", client);
        Assert.Contains("inbox.Done(len)", client);
        Assert.DoesNotContain("ReadAsStringAsync", client);
        Assert.DoesNotContain("ReadAsStringAsync", File.ReadAllText(Path.Combine(root, "src", "Lodge", "LodgeInvites.cs")));
        Assert.Contains("LodgeJson.Parse(m.Body)", File.ReadAllText(Path.Combine(root, "src", "Services", "Atlas", "CairnData.cs")));      // the manifest, before its signature check
    }
}
