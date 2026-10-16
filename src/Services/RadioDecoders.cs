using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace ElansAddonHub.Services
{
    // --selftest "radio" (2.76): one decoder per stream format, all reading the audio part of the one connection (IcyStream) and giving back PCM.
    //   MP3  NAudio's Mp3Frame + the Windows ACM MP3 decoder (as before)
    //   AAC  ADTS frames -> Media Foundation's AAC decoder (RadioMediaFoundation.cs)
    //   FLAC Ogg pages -> the Outpost's own FLAC decoder (RadioFlac.cs); the song from the stream's Vorbis comments
    // A decoder this PC lacks throws RadioDecoderUnavailableException; the player then steps down a quality (RadioParadise.Resolve).
    public sealed class RadioDecoderUnavailableException : Exception
    {
        public readonly RadioFormat Format;
        public RadioDecoderUnavailableException(RadioFormat f, string why) : base("no " + RadioParadise.FormatName(f) + " decoder on this PC" + (why != null ? ": " + why : "")) { Format = f; }
    }

    public interface IRadioDecoder : IDisposable
    {
        RadioFormat Format { get; }
        WaveFormat OutputFormat { get; }              // null until the first audio is decoded
        // the next piece of audio as PCM in pcm (grown when needed): the byte count (0 = headers only this time), -1 at the end of the stream
        int Decode(ref byte[] pcm);
        event Action<string, string> TagsChanged;     // a song named inside the stream (FLAC's Vorbis comments): title, cover (always null here)
    }

    public static class RadioDecoders
    {
        // the self-test pretends a decoder is missing (as on a Windows "N" edition without Media Foundation)
        public static readonly HashSet<RadioFormat> SimulateMissing = new HashSet<RadioFormat>();
        static readonly HashSet<RadioFormat> failed = new HashSet<RadioFormat>();   // a decoder that failed to start on this PC (this run)

        public static bool CanDecode(RadioFormat f)
        {
            lock (SimulateMissing) if (SimulateMissing.Contains(f) || failed.Contains(f)) return false;
            if (f == RadioFormat.Aac) return RadioMf.Available();
            return true;
        }

        public static void MarkFailed(RadioFormat f) { lock (SimulateMissing) failed.Add(f); }
        public static bool HasFailed(RadioFormat f) { lock (SimulateMissing) return failed.Contains(f); }
        public static void ResetForTest() { lock (SimulateMissing) { SimulateMissing.Clear(); failed.Clear(); } }

        public static IRadioDecoder Create(RadioFormat f, Stream src)
        {
            lock (SimulateMissing) if (SimulateMissing.Contains(f)) throw new RadioDecoderUnavailableException(f, "simulated by the self-test");
            switch (f)
            {
                case RadioFormat.Aac: return new AacRadioDecoder(src);
                case RadioFormat.Flac: return new OggFlacRadioDecoder(src);
                default: return new Mp3RadioDecoder(src);
            }
        }
    }

    sealed class Mp3RadioDecoder : IRadioDecoder
    {
        readonly Stream src;
        IMp3FrameDecompressor decomp;
        public RadioFormat Format => RadioFormat.Mp3;
        public WaveFormat OutputFormat => decomp?.OutputFormat;
        public event Action<string, string> TagsChanged { add { } remove { } }
        public Mp3RadioDecoder(Stream src) { this.src = src; }

        public int Decode(ref byte[] pcm)
        {
            Mp3Frame frame;
            try { frame = Mp3Frame.LoadFromStream(src); }
            catch (EndOfStreamException) { return -1; }
            if (frame == null) return -1;
            if (decomp == null)
            {
                var fmt = new Mp3WaveFormat(frame.SampleRate, frame.ChannelMode == ChannelMode.Mono ? 1 : 2, frame.FrameLength, frame.BitRate);
                try { decomp = new AcmMp3FrameDecompressor(fmt); }
                catch (Exception e) { throw new RadioDecoderUnavailableException(RadioFormat.Mp3, e.Message); }
            }
            if (pcm == null || pcm.Length < 16384 * 4) pcm = new byte[16384 * 4];
            return decomp.DecompressFrame(frame, pcm, 0);
        }

        public void Dispose() { try { decomp?.Dispose(); } catch { } }
    }

    sealed class AacRadioDecoder : IRadioDecoder
    {
        readonly AdtsReader adts;
        MfAacDecoder dec;
        byte[] au;
        int profile = -1, rateIndex = -1, channels = -1;
        public RadioFormat Format => RadioFormat.Aac;
        public WaveFormat OutputFormat => dec?.OutputFormat;
        public event Action<string, string> TagsChanged { add { } remove { } }
        public AacRadioDecoder(Stream src) { adts = new AdtsReader(src); }

        public int Decode(ref byte[] pcm)
        {
            int n = adts.Next(ref au);
            if (n < 0) return -1;
            if (dec == null || adts.Profile != profile || adts.RateIndex != rateIndex || adts.ChannelConfig != channels)
            {
                dec?.Dispose(); dec = null;
                profile = adts.Profile; rateIndex = adts.RateIndex; channels = adts.ChannelConfig;
                try { dec = new MfAacDecoder(profile, rateIndex, channels); }
                catch (Exception e) { throw new RadioDecoderUnavailableException(RadioFormat.Aac, e.Message); }
            }
            try { int got = dec.Decode(au, 0, n, ref pcm); bad = 0; return got; }
            catch (System.Runtime.InteropServices.COMException) when (++bad < 50) { BadFrames++; return 0; }   // one damaged frame: skip it
        }
        int bad;
        public int BadFrames;

        public void Dispose() { dec?.Dispose(); dec = null; }
    }

    // Ogg FLAC: a header packet (0x7F "FLAC" ... "fLaC" STREAMINFO), metadata packets (a VORBIS_COMMENT among them), then one frame per packet.
    // A new Ogg stream in the chain (a BOS page) starts over with its own headers - and maybe the next song's comments.
    sealed class OggFlacRadioDecoder : IRadioDecoder
    {
        readonly OggPacketReader ogg;
        FlacFrameDecoder flac;
        float[] samples;
        WaveFormat format;
        string lastTitle;
        public RadioFormat Format => RadioFormat.Flac;
        public WaveFormat OutputFormat => format;
        public event Action<string, string> TagsChanged;
        public int Frames => flac?.Frames ?? 0;
        public int BadFrames;
        public OggFlacRadioDecoder(Stream src) { ogg = new OggPacketReader(src); }

        public int Decode(ref byte[] pcm)
        {
            var p = ogg.Next(out _);
            if (p == null) return -1;
            if (p.Length == 0) return 0;
            if (p[0] == 0x7F && p.Length >= 13 + 38 && p[1] == 'F' && p[2] == 'L' && p[3] == 'A' && p[4] == 'C')
            {
                // the mapping header: "fLaC" at 9, the STREAMINFO block header at 13, its body at 17
                var info = FlacInfo.Parse(p, 17);
                if (info == null) throw new FlacFormatException("no STREAMINFO");
                flac = new FlacFrameDecoder(info);
                return 0;
            }
            if (p[0] == 0xFF && (p.Length > 1 && (p[1] & 0xFE) == 0xF8))
            {
                if (flac == null) return 0;                    // audio before its headers (joined mid-chain): wait for the next header
                int n;
                try { n = flac.Decode(p, 0, p.Length, ref samples); }
                catch (Exception e) when (e is FlacFormatException || e is IndexOutOfRangeException || e is OverflowException) { BadFrames++; return 0; }   // one damaged frame: skip it
                int ch = flac.Channels;
                if (format == null || format.SampleRate != flac.SampleRate || format.Channels != ch) format = WaveFormat.CreateIeeeFloatWaveFormat(flac.SampleRate, ch);
                int bytes = n * ch * 4;
                if (pcm == null || pcm.Length < bytes) pcm = new byte[Math.Max(bytes, 65536)];
                Buffer.BlockCopy(samples, 0, pcm, 0, bytes);
                return bytes;
            }
            if ((p[0] & 0x7F) == 4 && p.Length > 4)       // VORBIS_COMMENT
            {
                int len = (p[1] << 16) | (p[2] << 8) | p[3];
                var title = VorbisComments.StreamTitle(VorbisComments.Parse(p, 4, Math.Min(len, p.Length - 4)));
                if (title != null && title != lastTitle) { lastTitle = title; TagsChanged?.Invoke(title, null); }
            }
            return 0;
        }

        public void Dispose() { }
    }
}
