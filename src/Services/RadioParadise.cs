using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services
{
    // --selftest "radio": the Radio Paradise mini player in the title bar (Outpost 2.72). This file holds the rules that come from Radio Paradise's own
    // pages (radioparadise.com/listen/stream-links), the ICY stream reader, the reconnect backoff and the ducking curve; RadioPlayer.cs plays.
    //   - only the published stream links: https://stream.radioparadise.com/<path>, nothing else (no get_block, no audio.radioparadise.stream); the
    //     track title comes from the stream's own ICY metadata (Icy-MetaData: 1, icy-metaint, StreamTitle='...';). The cover, album and year (2.72.2)
    //     come from api.radioparadise.com/api/now_playing, once per song and never polled: see RadioNowPlaying.cs
    //   - one connection, opened only on Play, closed on Pause / Stop / exit; reconnect only while the user wants playback (2, 4, 8 ... s up to 5 min,
    //     with jitter, 10 tries, then Retry)
    //   - attribution "Radio Paradise · <channel>" linked to radioparadise.com and "Support Radio Paradise" (donate); no logo, never called official
    public sealed class RadioChannel
    {
        public string Id, Name, Path;      // Path = the stream link the player uses (MP3 192k; Serenity only has its 64k stream and FLAC)
        public string[] Published;         // every stream link Radio Paradise publishes for this channel
        public int? ApiChan;               // the channel number of api.radioparadise.com (its list_chan); null = no song details for this channel
        public override string ToString() => Name;
    }

    public static class RadioParadise
    {
        public const string StreamHost = "stream.radioparadise.com";
        public const string HomeUrl = "https://radioparadise.com";
        public const string DonateUrl = "https://radioparadise.com/donate";
        public const string ProjectUrl = "https://github.com/Elanoran/ElanAddonHub";
        public const string DefaultChannel = "main";

        static RadioChannel Ch(string id, string name, int? api, string path, params string[] published) => new RadioChannel { Id = id, Name = name, ApiChan = api, Path = path, Published = published };

        // the channels and their published links (radioparadise.com/listen/stream-links); the player uses MP3 192k because Windows decodes it without
        // extra components (see RadioPlayer); AAC / FLAC links are listed so the allow-list matches the page exactly.
        // The API channel numbers are the ones radioparadise.com's own player uses (its list_chan answer, checked 2026-10-16: 0 main-mix, 1 mellow,
        // 2 rock, 3 global, 5 beyond, 42 serenity, 945 kfat). Radio 2050 ("2050" in the site's code) is a talk-and-music show whose now_playing has
        // no artist or title, so it gets no song details.
        public static readonly RadioChannel[] Channels =
        {
            Ch("main", "Main Mix", 0, "mp3-192", "aac-128", "mp3-192", "aac-320", "flacbg"),
            Ch("mellow", "Mellow", 1, "mellow-192", "mellow-128", "mellow-192", "mellow-320", "mellow-flacbg"),
            Ch("rock", "Rock", 2, "rock-192", "rock-128", "rock-192", "rock-320", "rock-flacbg"),
            Ch("global", "Global", 3, "global-192", "global-128", "global-192", "global-320", "global-flacbg"),
            Ch("beyond", "Beyond", 5, "beyond-192", "beyond-128", "beyond-192", "beyond-320", "beyond-flacbg"),
            Ch("radio2050", "Radio 2050", null, "radio2050-192", "radio2050-128", "radio2050-192", "radio2050-320"),
            Ch("kfat", "KFAT", 945, "kfat-192", "kfat-128", "kfat-192", "kfat-320", "kfat-flacbg"),
            Ch("serenity", "Serenity", 42, "serenity", "serenity", "serenity-flac"),
        };

        public static RadioChannel Channel(string id) => Channels.FirstOrDefault(c => c.Id == id) ?? Channels[0];

        public static string UserAgent => "ElansOutpost/" + App.Version + " (+" + ProjectUrl + ")";

        public static string StreamUrl(RadioChannel c) => "https://" + StreamHost + "/" + c.Path;

        // the one rule for what the player may open: https, exactly stream.radioparadise.com on the default port, a published path, no query
        public static bool IsAllowedStreamUrl(string url)
        {
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || !string.Equals(u.Host, StreamHost, StringComparison.OrdinalIgnoreCase)) return false;
            if (u.Query.Length > 0 || u.Fragment.Length > 0 || u.UserInfo.Length > 0) return false;
            var path = u.AbsolutePath.TrimStart('/');
            return Channels.Any(c => c.Published.Contains(path));
        }

        // "Artist - Title" from a metadata block: StreamTitle='...'; (a title may itself hold ' or ;, so the value ends at the "';" that is followed by
        // the next key or the end of the block)
        public static string ParseStreamTitle(string meta)
        {
            if (string.IsNullOrEmpty(meta)) return null;
            meta = meta.TrimEnd('\0');
            const string key = "StreamTitle='";
            int s = meta.IndexOf(key, StringComparison.Ordinal);
            if (s < 0) return null;
            s += key.Length;
            int end = -1;
            for (int i = meta.IndexOf("';", s, StringComparison.Ordinal); i >= 0; i = meta.IndexOf("';", i + 1, StringComparison.Ordinal))
            {
                var rest = meta.Substring(i + 2).TrimStart();
                if (rest.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(rest, @"^[A-Za-z]+=")) { end = i; break; }
            }
            if (end < 0) end = meta.EndsWith("'") ? meta.Length - 1 : meta.Length;
            var t = meta.Substring(s, end - s).Trim();
            return t.Length == 0 ? null : t;
        }

        // reconnect waits: 2, 4, 8 ... s, at most 5 minutes, +-20 % jitter so many listeners never come back at the same second
        public const int MaxTries = 10;
        public static TimeSpan Backoff(int attempt, Random rnd)
        {
            double baseSec = Math.Min(300, Math.Pow(2, Math.Max(1, attempt)));
            double jitter = 0.8 + 0.4 * (rnd ?? new Random()).NextDouble();
            return TimeSpan.FromSeconds(Math.Min(300, baseSec * jitter));
        }
    }

    // The audio part of an ICY stream: every icy-metaint bytes of audio the server puts one metadata block (a length byte * 16, then text). Reads
    // pass the audio through and take the blocks out; a new StreamTitle raises TitleChanged (on the reading thread).
    public sealed class IcyStream : Stream
    {
        readonly Stream inner;
        readonly int metaInt;
        int left;
        public event Action<string> TitleChanged;
        public string Title { get; private set; }
        public long AudioBytes { get; private set; }

        public IcyStream(Stream inner, int metaInt) { this.inner = inner; this.metaInt = metaInt; left = metaInt; }

        // fills the whole request (or stops at the end of the stream): the MP3 frame reader treats a short read as the end
        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                if (metaInt > 0 && left == 0) { ReadMeta(); left = metaInt; }
                int want = metaInt > 0 ? Math.Min(count - total, left) : count - total;
                int n = inner.Read(buffer, offset + total, want);
                if (n <= 0) break;
                if (metaInt > 0) left -= n;
                AudioBytes += n;
                total += n;
            }
            return total;
        }

        void ReadMeta()
        {
            int len = inner.ReadByte();
            if (len < 0) throw new EndOfStreamException();
            if (len == 0) return;
            var b = new byte[len * 16];
            int got = 0;
            while (got < b.Length) { int n = inner.Read(b, got, b.Length - got); if (n <= 0) throw new EndOfStreamException(); got += n; }
            var t = RadioParadise.ParseStreamTitle(Encoding.UTF8.GetString(b));
            if (t != null && t != Title) { Title = t; TitleChanged?.Invoke(t); }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => AudioBytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    // The music fades down while people talk in my Lodge voice room (me included) and comes back after they stop. Pure: the caller passes the time
    // and whether anyone is speaking (null = not in a voice room: no effect at all), and gets the factor for the volume.
    public sealed class RadioDucker
    {
        public double Level = 0.3;                 // the music while people talk, as a part of the chosen volume (0 = silent, 1 = no ducking)
        public double DownMs = 250, HoldMs = 800, UpMs = 600;
        public double Factor { get; private set; } = 1;
        public bool Ducked => Factor < 0.999;
        double lastMs = double.NaN, lastSpeechMs = double.NegativeInfinity;

        public double Step(bool? speaking, double nowMs)
        {
            double dt = double.IsNaN(lastMs) ? 0 : Math.Max(0, nowMs - lastMs);
            lastMs = nowMs;
            double level = Math.Max(0, Math.Min(1, Level));
            if (speaking == true) lastSpeechMs = nowMs;
            bool hold = speaking != null && (speaking == true || nowMs - lastSpeechMs < HoldMs);
            double target = hold ? level : 1;
            double span = Math.Max(0.0001, 1 - level);
            if (Factor > target) Factor = Math.Max(target, Factor - dt / DownMs * span);
            else if (Factor < target) Factor = Math.Min(target, Factor + dt / UpMs * span);
            return Factor;
        }

        public void Reset() { Factor = 1; lastMs = double.NaN; lastSpeechMs = double.NegativeInfinity; }
    }
}
