using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services
{
    // --selftest "radio": the Radio Paradise mini player in the title bar (Outpost 2.72). This file holds the rules that come from Radio Paradise's own
    // pages (radioparadise.com/listen/stream-links), the ICY stream reader, the reconnect backoff and the ducking curve; RadioPlayer.cs plays.
    //   - only the published stream links: https://stream.radioparadise.com/<path>, nothing else (no get_block, no audio.radioparadise.stream, no
    //     api.radioparadise.com); the track title and its cover come from the stream's own ICY metadata (Icy-MetaData: 1, icy-metaint,
    //     StreamTitle='Artist - Title';StreamUrl='http://img.radioparadise.com/covers/l/<id>.jpg';). 2.73: the cover is fetched by RadioNowPlaying.cs
    //   - the metadata text is UTF-8 when it is valid UTF-8, otherwise Windows-1252 (Radio Paradise's streams send Latin-1, e.g. "José González")
    //   - one connection, opened only on Play, closed on Pause / Stop / exit; reconnect only while the user wants playback (2, 4, 8 ... s up to 5 min,
    //     with jitter, 10 tries, then Retry)
    //   - attribution "Radio Paradise · <channel>" linked to radioparadise.com and "Support Radio Paradise" (donate); no logo, never called official
    public sealed class RadioChannel
    {
        public string Id, Name, Path;      // Path = the Standard stream (MP3 192k; Serenity only has its 64k AAC stream and FLAC)
        public string Aac, Flac;           // 2.76: the High stream (AAC 320k; Serenity its 64k AAC) and the Lossless one (Ogg FLAC; null = none, Radio 2050)
        public string[] Published;         // every stream link Radio Paradise publishes for this channel
        public override string ToString() => Name;
    }

    // 2.76: Settings > Radio Paradise > Sound quality. Standard = MP3 192k (default), High = AAC 320k, Lossless = FLAC (in an Ogg stream)
    public enum RadioQuality { Standard, High, Lossless }
    public enum RadioFormat { Mp3, Aac, Flac }

    // the stream the player opens for a channel at a quality: its published path, the format to decode and the quality it really is
    public sealed class RadioStream
    {
        public string Path;
        public RadioFormat Format;
        public RadioQuality Quality;       // what plays (can be lower than asked: Radio 2050 has no FLAC, a decoder is missing)
        public string Note;                // why it is not what was asked (null = it is)
        public string Label => Format == RadioFormat.Flac ? "Lossless" : Format == RadioFormat.Aac && Path.EndsWith("-320", StringComparison.Ordinal) ? "High" : "Standard";   // Serenity's AAC is 64k: Standard
    }

    public static class RadioParadise
    {
        public const string StreamHost = "stream.radioparadise.com";
        public const string HomeUrl = "https://radioparadise.com";
        public const string DonateUrl = "https://radioparadise.com/donate";
        public const string ProjectUrl = "https://github.com/Elanoran/ElanAddonHub";
        public const string DefaultChannel = "main";

        static RadioChannel Ch(string id, string name, string path, string aac, string flac, params string[] published) => new RadioChannel { Id = id, Name = name, Path = path, Aac = aac, Flac = flac, Published = published };

        // the channels and their published links (radioparadise.com/listen/stream-links). Standard plays the MP3 192k stream (Windows decodes it
        // without extra components); High the AAC 320k one and Lossless the FLAC one ("flacbg", Radio Paradise's FLAC for background listening),
        // both decoded with what Windows has (RadioDecoders.cs). Formats as the streams send them (checked 2026-10): mp3-192 audio/mpeg; aac-320 and
        // serenity audio/aac (ADTS; serenity is HE-AAC 64k), both with ICY titles; flacbg and serenity-flac application/ogg (Ogg FLAC 44.1 kHz
        // 16 bit, no icy-metaint: no ICY titles).
        public static readonly RadioChannel[] Channels =
        {
            Ch("main", "Main Mix", "mp3-192", "aac-320", "flacbg", "aac-128", "mp3-192", "aac-320", "flacbg"),
            Ch("mellow", "Mellow", "mellow-192", "mellow-320", "mellow-flacbg", "mellow-128", "mellow-192", "mellow-320", "mellow-flacbg"),
            Ch("rock", "Rock", "rock-192", "rock-320", "rock-flacbg", "rock-128", "rock-192", "rock-320", "rock-flacbg"),
            Ch("global", "Global", "global-192", "global-320", "global-flacbg", "global-128", "global-192", "global-320", "global-flacbg"),
            Ch("beyond", "Beyond", "beyond-192", "beyond-320", "beyond-flacbg", "beyond-128", "beyond-192", "beyond-320", "beyond-flacbg"),
            Ch("radio2050", "Radio 2050", "radio2050-192", "radio2050-320", null, "radio2050-128", "radio2050-192", "radio2050-320"),
            Ch("kfat", "KFAT", "kfat-192", "kfat-320", "kfat-flacbg", "kfat-128", "kfat-192", "kfat-320", "kfat-flacbg"),
            Ch("serenity", "Serenity", "serenity", "serenity", "serenity-flac", "serenity", "serenity-flac"),
        };

        // the stream for a channel at a quality. A quality the channel lacks steps down (Radio 2050 Lossless -> High); a format this PC can't
        // decode (canDecode false) steps down too, never up (more data than asked is never used by itself). null = nothing playable.
        public static RadioStream Resolve(RadioChannel c, RadioQuality asked, Func<RadioFormat, bool> canDecode)
        {
            canDecode = canDecode ?? (f => true);
            string note = null;
            for (var q = asked; q >= RadioQuality.Standard; q--)
            {
                string path; RadioFormat f;
                if (q == RadioQuality.Lossless) { path = c.Flac; f = RadioFormat.Flac; }
                else if (q == RadioQuality.High) { path = c.Aac; f = RadioFormat.Aac; }
                else { path = c.Path; f = IsSerenityAac(c, path) ? RadioFormat.Aac : RadioFormat.Mp3; }
                if (path == null) { note = note ?? c.Name + " has no " + QualityName(q) + " stream"; continue; }
                if (!canDecode(f)) { note = note ?? "this PC can't play " + FormatName(f); continue; }
                return new RadioStream { Path = path, Format = f, Quality = q, Note = q == asked ? null : note };
            }
            return null;
        }

        static bool IsSerenityAac(RadioChannel c, string path) => path == c.Aac;   // Serenity's only lossy stream is AAC (64k)

        public static string QualityName(RadioQuality q) => q == RadioQuality.Lossless ? "lossless" : q == RadioQuality.High ? "high-quality" : "standard";
        public static string FormatName(RadioFormat f) => f == RadioFormat.Flac ? "FLAC" : f == RadioFormat.Aac ? "AAC" : "MP3";

        public static RadioQuality ParseQuality(string s) => s == "high" ? RadioQuality.High : s == "lossless" ? RadioQuality.Lossless : RadioQuality.Standard;
        public static string QualityId(RadioQuality q) => q == RadioQuality.High ? "high" : q == RadioQuality.Lossless ? "lossless" : "standard";

        // the format a response says it is (null = one the player can't play)
        public static RadioFormat? FormatOf(string contentType)
        {
            var t = (contentType ?? "").ToLowerInvariant();
            if (t.Contains("mpeg") || t.Contains("mp3")) return RadioFormat.Mp3;
            if (t.Contains("aac") || t.Contains("mp4a")) return RadioFormat.Aac;
            if (t.Contains("ogg") || t.Contains("flac")) return RadioFormat.Flac;
            return null;
        }

        public static RadioChannel Channel(string id) => Channels.FirstOrDefault(c => c.Id == id) ?? Channels[0];

        public static string UserAgent => "ElansOutpost/" + App.Version + " (+" + ProjectUrl + ")";

        public static string StreamUrl(RadioChannel c) => StreamUrl(c.Path);
        public static string StreamUrl(string path) => "https://" + StreamHost + "/" + path;

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
        public static string ParseStreamTitle(string meta) => ParseField(meta, "StreamTitle");

        // the song's cover: StreamUrl='http://img.radioparadise.com/covers/l/11774.jpg'; (RadioNowPlaying checks it against its allow-list)
        public static string ParseStreamUrl(string meta) => ParseField(meta, "StreamUrl");

        public static string ParseField(string meta, string name)
        {
            if (string.IsNullOrEmpty(meta)) return null;
            meta = meta.TrimEnd('\0');
            string key = name + "='";
            int s = -1;
            // a key starts the block or follows a "';" - the same words inside a title ("... StreamUrl='x' ...") are not a key
            for (int i = meta.IndexOf(key, StringComparison.Ordinal); i >= 0; i = meta.IndexOf(key, i + 1, StringComparison.Ordinal))
                if (i == 0 || meta.Substring(0, i).TrimEnd().EndsWith("';", StringComparison.Ordinal)) { s = i; break; }
            if (s < 0) return null;
            s += key.Length;
            int end = -1;
            for (int i = meta.IndexOf("';", s, StringComparison.Ordinal); i >= 0; i = meta.IndexOf("';", i + 1, StringComparison.Ordinal))
            {
                var rest = meta.Substring(i + 2).TrimStart();
                if (rest.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(rest, @"^[A-Za-z]+=")) { end = i; break; }
            }
            if (end < 0) end = meta.EndsWith("'") ? meta.Length - 1 : meta.Length;
            var t = meta.Substring(s, Math.Max(0, end - s)).Trim();
            return t.Length == 0 ? null : t;
        }

        // the text of a metadata block (its zero padding cut): UTF-8 when the bytes are valid UTF-8, otherwise Windows-1252, a superset of the
        // Latin-1 Radio Paradise sends (read as UTF-8, "José González" came out with replacement characters)
        static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public static string DecodeMeta(byte[] b, int count)
        {
            if (b == null) return null;
            count = Math.Min(count, b.Length);
            while (count > 0 && b[count - 1] == 0) count--;
            try { return StrictUtf8.GetString(b, 0, count); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(b, 0, count); }
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
    // pass the audio through and take the blocks out; a new StreamTitle or a new StreamUrl (the cover) raises MetaChanged (on the reading thread).
    // A block without a title (most of them are empty) keeps the song and its cover.
    public sealed class IcyStream : Stream
    {
        readonly Stream inner;
        readonly int metaInt;
        int left;
        public event Action<string, string> MetaChanged;    // the title, the cover address (null when the block names none)
        public string Title { get; private set; }
        public string CoverUrl { get; private set; }
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
            var text = RadioParadise.DecodeMeta(b, b.Length);
            var t = RadioParadise.ParseStreamTitle(text);
            if (t == null) return;
            var u = RadioParadise.ParseStreamUrl(text);
            if (t != Title || u != CoverUrl) { Title = t; CoverUrl = u; MetaChanged?.Invoke(t, u); }
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
