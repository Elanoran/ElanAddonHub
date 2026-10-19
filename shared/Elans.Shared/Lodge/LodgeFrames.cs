using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Elans.Shared
{
    // Reading whole WebSocket messages with a size cap, and bounding what waits to be handled - the receiving side of the Lodge link
    // (the Outpost's LodgeClient). A hostile or broken Lodge must not be able to make the Outpost buffer without end: a message over the
    // cap stops the reading as soon as it passes the cap (the caller drops the connection), and frames that pile up faster than they are
    // handled make the caller drop the connection too (a reconnect starts over with a fresh welcome, so nothing real is lost).
    public static class LodgeFrames
    {
        public enum Kind { Text, Binary, Close, TooBig }

        // One whole message into `msg` (cleared first), read in pieces of `buf`. TooBig as soon as more than `max` bytes arrived - the rest of
        // that message is never read.
        public static async Task<Kind> ReadAsync(WebSocket ws, byte[] buf, MemoryStream msg, int max, CancellationToken ct)
        {
            msg.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return Kind.Close;
                if (msg.Length + r.Count > max) return Kind.TooBig;
                msg.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            return r.MessageType == WebSocketMessageType.Binary ? Kind.Binary : Kind.Text;
        }

        // A JSON answer body from a Lodge (HTTP), read to at most `max` bytes: null when it is longer (or unreadable) - it is not read further
        public static async Task<JsonObject> ReadAnswerAsync(Stream body, int max, CancellationToken ct = default)
        {
            if (body == null) return null;
            try
            {
                var ms = new MemoryStream();
                var buf = new byte[8192];
                int n;
                while ((n = await body.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + n > max) return null;
                    ms.Write(buf, 0, n);
                }
                return LodgeJson.Parse(ms.GetBuffer(), 0, (int)ms.Length);
            }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException) { return null; }
        }
    }

    // What has been received but not handled yet (frames posted to the UI thread). Add before posting, Done when handled; Add says false
    // once more than MaxFrames frames or MaxBytes bytes are waiting - then the connection is dropped instead of queueing more. One per
    // connection (a reconnect starts with an empty one).
    public sealed class LodgeInbox
    {
        // 2000 frames: the busiest real moment (a welcome, then every member's presence and voice state at once in a full Lodge, ~500 frames)
        // fits several times over; a UI that is that far behind is seconds late and better served by a reconnect and a fresh welcome
        public const int DefaultMaxFrames = 2000;
        // 64 MB: a few of the biggest frames at once (LodgeJson.MaxFrameFromLodge), far less than an unbounded queue could take
        public const long DefaultMaxBytes = 64L * 1024 * 1024;

        public int MaxFrames { get; }
        public long MaxBytes { get; }
        int frames; long bytes;

        public LodgeInbox(int maxFrames = DefaultMaxFrames, long maxBytes = DefaultMaxBytes) { MaxFrames = maxFrames; MaxBytes = maxBytes; }

        public int Frames => Volatile.Read(ref frames);
        public long Bytes => Interlocked.Read(ref bytes);

        public bool Add(int size)
        {
            var f = Interlocked.Increment(ref frames);
            var b = Interlocked.Add(ref bytes, size);
            if (f <= MaxFrames && b <= MaxBytes) return true;
            Done(size);
            return false;
        }

        public void Done(int size) { Interlocked.Decrement(ref frames); Interlocked.Add(ref bytes, -size); }
    }
}
