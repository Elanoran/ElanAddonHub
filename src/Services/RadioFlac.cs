using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ElansAddonHub.Services
{
    // --selftest "radio" (2.75): the Lossless streams. Radio Paradise sends FLAC inside an Ogg stream (application/ogg, "OggS" pages, the FLAC
    // mapping: a 0x7F "FLAC" header packet with STREAMINFO, metadata packets, then one FLAC frame per packet). The Ogg pages are taken apart here
    // and every frame decoded by a small FLAC decoder written for the Outpost (the format is open and simple: fixed / LPC prediction + Rice
    // coded residuals), so nothing has to be installed. The song comes from the stream's own Vorbis comments (ARTIST / TITLE) when a new Ogg
    // stream in the chain carries them. 2.77, checked on the real flacbg stream (one connection, 8 minutes, 55.7 MB, 1336 Ogg pages): one logical
    // stream only (one serial, BOS on the first page, no EOS, no new BOS at song changes), its one VORBIS_COMMENT block empty (vendor "reference
    // libFLAC 1.5.0 20250211", 0 comments), no PICTURE block, and no icy-metaint in the answer even with Icy-MetaData: 1. So Radio Paradise's
    // Lossless streams name no song and no cover; the pop-out says so. A chained stream with per-song comments (other servers) still works here.

    // the pages of an Ogg stream, put back together into packets
    public sealed class OggPacketReader
    {
        readonly Stream src;
        readonly byte[] head = new byte[27];
        readonly byte[] lacing = new byte[255];
        readonly Queue<byte[]> ready = new Queue<byte[]>();
        readonly Queue<bool> readyBos = new Queue<bool>();
        MemoryStream partial = new MemoryStream();
        bool partialBos;
        public uint Serial { get; private set; }
        public int Pages { get; private set; }
        public int Resyncs { get; private set; }

        public OggPacketReader(Stream src) { this.src = src; }

        // the next packet; bos = it is the first packet of a new logical stream (a new chain link). null at the end of the stream.
        public byte[] Next(out bool bos)
        {
            while (ready.Count == 0) if (!ReadPage()) { bos = false; return null; }
            bos = readyBos.Dequeue();
            return ready.Dequeue();
        }

        bool ReadPage()
        {
            // "OggS" (resync byte by byte after damage)
            if (!Fill(head, 0, 4)) return false;
            while (!(head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S'))
            {
                Buffer.BlockCopy(head, 1, head, 0, 3);
                int b = src.ReadByte(); if (b < 0) return false;
                head[3] = (byte)b; Resyncs++;
            }
            if (!Fill(head, 4, 23)) return false;
            int type = head[5];
            uint serial = BitConverter.ToUInt32(head, 14);
            int segs = head[26];
            if (!Fill(lacing, 0, segs)) return false;
            int total = 0; for (int i = 0; i < segs; i++) total += lacing[i];
            var body = new byte[total];
            if (!Fill(body, 0, total)) return false;
            Pages++;
            bool bos = (type & 2) != 0;
            if (bos || serial != Serial) { partial = new MemoryStream(); partialBos = bos; Serial = serial; }
            else if ((type & 1) == 0 && partial.Length > 0) { partial = new MemoryStream(); }   // a lost page: drop the half packet
            int at = 0;
            for (int i = 0; i < segs; i++)
            {
                partial.Write(body, at, lacing[i]); at += lacing[i];
                if (lacing[i] < 255)
                {
                    ready.Enqueue(partial.ToArray()); readyBos.Enqueue(partialBos);
                    partial = new MemoryStream(); partialBos = false;
                }
            }
            return true;
        }

        bool Fill(byte[] b, int off, int n)
        {
            while (n > 0) { int r = src.Read(b, off, n); if (r <= 0) return false; off += r; n -= r; }
            return true;
        }
    }

    // FLAC STREAMINFO (the parts the player needs)
    public sealed class FlacInfo
    {
        public int SampleRate, Channels, BitsPerSample, MaxBlock;

        // a STREAMINFO block body (34 bytes)
        public static FlacInfo Parse(byte[] b, int at)
        {
            if (b == null || b.Length < at + 18) return null;
            var info = new FlacInfo { MaxBlock = (b[at + 2] << 8) | b[at + 3] };
            info.SampleRate = (b[at + 10] << 12) | (b[at + 11] << 4) | (b[at + 12] >> 4);
            info.Channels = ((b[at + 12] >> 1) & 7) + 1;
            info.BitsPerSample = (((b[at + 12] & 1) << 4) | (b[at + 13] >> 4)) + 1;
            return info.SampleRate > 0 ? info : null;
        }
    }

    public sealed class FlacFormatException : Exception { public FlacFormatException(string m) : base(m) { } }

    // one FLAC frame -> interleaved float samples (-1..1). Checks the frame's CRC-8 (header) and CRC-16 (whole frame).
    public sealed class FlacFrameDecoder
    {
        public FlacInfo Info;                  // from STREAMINFO (a frame may also name its own rate / size)
        public int SampleRate, Channels, BitsPerSample, BlockSize;   // of the last frame
        public int Frames, CrcErrors;
        int[][] ch = new int[0][];
        byte[] d; int pos, bit, end;

        public FlacFrameDecoder(FlacInfo info) { Info = info; }

        // decodes one frame from b[off..off+len); writes BlockSize * Channels floats into output (grown when needed); returns the sample count per channel
        public int Decode(byte[] b, int off, int len, ref float[] output)
        {
            d = b; pos = off; bit = 0; end = off + len;
            if (len < 6 || b[off] != 0xFF || (b[off + 1] & 0xFE) != 0xF8) throw new FlacFormatException("no frame sync");
            pos += 2;
            int bsCode = (int)Bits(4), srCode = (int)Bits(4), chCode = (int)Bits(4), ssCode = (int)Bits(3);
            Bits(1);
            ReadUtf8();
            int bs;
            if (bsCode == 0) throw new FlacFormatException("reserved block size");
            else if (bsCode == 1) bs = 192;
            else if (bsCode <= 5) bs = 576 << (bsCode - 2);
            else if (bsCode == 6) bs = (int)Bits(8) + 1;
            else if (bsCode == 7) bs = (int)Bits(16) + 1;
            else bs = 256 << (bsCode - 8);
            int sr;
            switch (srCode)
            {
                case 0: sr = Info?.SampleRate ?? 0; break;
                case 1: sr = 88200; break; case 2: sr = 176400; break; case 3: sr = 192000; break;
                case 4: sr = 8000; break; case 5: sr = 16000; break; case 6: sr = 22050; break; case 7: sr = 24000; break;
                case 8: sr = 32000; break; case 9: sr = 44100; break; case 10: sr = 48000; break; case 11: sr = 96000; break;
                case 12: sr = (int)Bits(8) * 1000; break;
                case 13: sr = (int)Bits(16); break;
                case 14: sr = (int)Bits(16) * 10; break;
                default: throw new FlacFormatException("bad sample rate");
            }
            int bps;
            switch (ssCode)
            {
                case 0: bps = Info?.BitsPerSample ?? 0; break;
                case 1: bps = 8; break; case 2: bps = 12; break; case 4: bps = 16; break;
                case 5: bps = 20; break; case 6: bps = 24; break; case 7: bps = 32; break;
                default: throw new FlacFormatException("bad sample size");
            }
            if (sr <= 0 || bps <= 0) throw new FlacFormatException("no stream info");
            int nch = chCode < 8 ? chCode + 1 : chCode <= 10 ? 2 : throw new FlacFormatException("bad channel assignment");
            int crc8 = (int)Bits(8);
            if (Crc8(b, off, pos - off - 1) != crc8) { CrcErrors++; throw new FlacFormatException("header CRC"); }

            if (ch.Length != nch) ch = new int[nch][];
            for (int c = 0; c < nch; c++)
            {
                if (ch[c] == null || ch[c].Length < bs) ch[c] = new int[bs];
                int extra = (chCode == 8 && c == 1) || (chCode == 9 && c == 0) || (chCode == 10 && c == 1) ? 1 : 0;   // the side channel has one more bit
                Subframe(ch[c], bs, bps + extra);
            }
            // zero padding to the byte, then CRC-16 of everything before it
            if (bit != 0) { bit = 0; pos++; }
            if (pos + 2 > end) throw new FlacFormatException("short frame");
            int crc16 = (d[pos] << 8) | d[pos + 1];
            if (Crc16(b, off, pos - off) != crc16) { CrcErrors++; throw new FlacFormatException("frame CRC"); }

            var l = ch[0]; var r = nch > 1 ? ch[1] : null;
            if (chCode == 8) for (int i = 0; i < bs; i++) r[i] = l[i] - r[i];                 // left / side
            else if (chCode == 9) for (int i = 0; i < bs; i++) l[i] += r[i];                  // side / right
            else if (chCode == 10)                                                            // mid / side
                for (int i = 0; i < bs; i++)
                {
                    int side = r[i], mid = (l[i] << 1) | (side & 1);
                    l[i] = (mid + side) >> 1; r[i] = (mid - side) >> 1;
                }

            if (output == null || output.Length < bs * nch) output = new float[bs * nch];
            float scale = 1f / (1L << (bps - 1));
            for (int c = 0; c < nch; c++) { var s = ch[c]; for (int i = 0; i < bs; i++) output[i * nch + c] = s[i] * scale; }
            SampleRate = sr; Channels = nch; BitsPerSample = bps; BlockSize = bs; Frames++;
            return bs;
        }

        void Subframe(int[] s, int n, int bps)
        {
            if (Bits(1) != 0) throw new FlacFormatException("subframe padding");
            int type = (int)Bits(6);
            int wasted = 0;
            if (Bits(1) != 0) { wasted = 1; while (Bits(1) == 0) wasted++; bps -= wasted; }
            if (type == 0) { int v = Signed(bps); for (int i = 0; i < n; i++) s[i] = v; }
            else if (type == 1) { for (int i = 0; i < n; i++) s[i] = Signed(bps); }
            else if (type >= 8 && type <= 12)
            {
                int order = type - 8;
                for (int i = 0; i < order; i++) s[i] = Signed(bps);
                Residual(s, n, order);
                for (int i = order; i < n; i++)
                    switch (order)
                    {
                        case 1: s[i] += s[i - 1]; break;
                        case 2: s[i] += 2 * s[i - 1] - s[i - 2]; break;
                        case 3: s[i] += 3 * s[i - 1] - 3 * s[i - 2] + s[i - 3]; break;
                        case 4: s[i] += 4 * s[i - 1] - 6 * s[i - 2] + 4 * s[i - 3] - s[i - 4]; break;
                    }
            }
            else if (type >= 32)
            {
                int order = type - 31;
                for (int i = 0; i < order; i++) s[i] = Signed(bps);
                int prec = (int)Bits(4) + 1;
                if (prec == 16) throw new FlacFormatException("bad LPC precision");
                int shift = Signed(5);
                if (shift < 0) throw new FlacFormatException("negative LPC shift");
                var coef = new int[order];
                for (int i = 0; i < order; i++) coef[i] = Signed(prec);
                Residual(s, n, order);
                for (int i = order; i < n; i++)
                {
                    long sum = 0;
                    for (int j = 0; j < order; j++) sum += (long)coef[j] * s[i - j - 1];
                    s[i] += (int)(sum >> shift);
                }
            }
            else throw new FlacFormatException("reserved subframe type");
            if (wasted > 0) for (int i = 0; i < n; i++) s[i] <<= wasted;
        }

        void Residual(int[] s, int n, int order)
        {
            int method = (int)Bits(2);
            if (method > 1) throw new FlacFormatException("reserved residual coding");
            int pbits = method == 0 ? 4 : 5, escape = method == 0 ? 15 : 31;
            int porder = (int)Bits(4);
            int parts = 1 << porder;
            if ((n >> porder) < order) throw new FlacFormatException("bad partition order");
            int at = order;
            for (int p = 0; p < parts; p++)
            {
                int count = (n >> porder) - (p == 0 ? order : 0);
                int k = (int)Bits(pbits);
                if (k == escape)
                {
                    int raw = (int)Bits(5);
                    for (int i = 0; i < count; i++) s[at++] = raw == 0 ? 0 : Signed(raw);
                }
                else
                    for (int i = 0; i < count; i++)
                    {
                        uint q = Unary();
                        uint v = (q << k) | (k > 0 ? Bits(k) : 0);
                        s[at++] = (int)(v >> 1) ^ -(int)(v & 1);
                    }
            }
        }

        // ---- bits, most significant first
        uint Bits(int n)
        {
            uint v = 0;
            while (n > 0)
            {
                if (pos >= end) throw new FlacFormatException("frame ends early");
                int left = 8 - bit, take = Math.Min(left, n);
                v = (v << take) | (uint)((d[pos] >> (left - take)) & ((1 << take) - 1));
                bit += take; n -= take;
                if (bit == 8) { bit = 0; pos++; }
            }
            return v;
        }

        int Signed(int n) { if (n == 0) return 0; uint v = Bits(n); int sh = 32 - n; return (int)(v << sh) >> sh; }

        uint Unary()
        {
            uint q = 0;
            while (true)
            {
                if (pos >= end) throw new FlacFormatException("frame ends early");
                if (bit == 0 && d[pos] == 0) { q += 8; pos++; continue; }
                if (((d[pos] >> (7 - bit)) & 1) != 0) { bit++; if (bit == 8) { bit = 0; pos++; } return q; }
                q++; bit++; if (bit == 8) { bit = 0; pos++; }
            }
        }

        void ReadUtf8()
        {
            uint b0 = Bits(8);
            int more = b0 < 0x80 ? 0 : b0 >= 0xFE ? 6 : b0 >= 0xFC ? 5 : b0 >= 0xF8 ? 4 : b0 >= 0xF0 ? 3 : b0 >= 0xE0 ? 2 : b0 >= 0xC0 ? 1 : -1;
            if (more < 0) throw new FlacFormatException("bad frame number");
            for (int i = 0; i < more; i++) Bits(8);
        }

        // ---- CRCs (FLAC: CRC-8 poly 0x07, CRC-16 poly 0x8005, both from 0)
        static readonly byte[] T8 = MakeT8();
        static readonly ushort[] T16 = MakeT16();
        static byte[] MakeT8() { var t = new byte[256]; for (int i = 0; i < 256; i++) { int c = i; for (int k = 0; k < 8; k++) c = (c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1; t[i] = (byte)c; } return t; }
        static ushort[] MakeT16() { var t = new ushort[256]; for (int i = 0; i < 256; i++) { int c = i << 8; for (int k = 0; k < 8; k++) c = (c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1; t[i] = (ushort)c; } return t; }
        public static int Crc8(byte[] b, int off, int n) { int c = 0; for (int i = 0; i < n; i++) c = T8[c ^ b[off + i]]; return c; }
        public static int Crc16(byte[] b, int off, int n) { int c = 0; for (int i = 0; i < n; i++) c = ((c << 8) ^ T16[(c >> 8) ^ b[off + i]]) & 0xFFFF; return c; }
    }

    // Vorbis comments (the FLAC VORBIS_COMMENT block): KEY=value pairs, little-endian lengths, UTF-8
    public static class VorbisComments
    {
        public static Dictionary<string, string> Parse(byte[] b, int at, int len)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                int end = at + len;
                int vlen = BitConverter.ToInt32(b, at); at += 4 + vlen;
                if (vlen < 0 || at + 4 > end) return map;
                int count = BitConverter.ToInt32(b, at); at += 4;
                for (int i = 0; i < count && at + 4 <= end; i++)
                {
                    int l = BitConverter.ToInt32(b, at); at += 4;
                    if (l < 0 || at + l > end) break;
                    var kv = Encoding.UTF8.GetString(b, at, l); at += l;
                    int eq = kv.IndexOf('=');
                    if (eq > 0 && !map.ContainsKey(kv.Substring(0, eq))) map[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                }
            }
            catch (ArgumentException) { }
            return map;
        }

        // "Artist - Title" (the form the ICY StreamTitle has), the title alone without an artist, null without a title
        public static string StreamTitle(Dictionary<string, string> tags)
        {
            if (tags == null) return null;
            tags.TryGetValue("TITLE", out var t); tags.TryGetValue("ARTIST", out var a);
            t = t?.Trim(); a = a?.Trim();
            if (string.IsNullOrEmpty(t)) return null;
            return string.IsNullOrEmpty(a) ? t : a + " - " + t;
        }
    }
}
