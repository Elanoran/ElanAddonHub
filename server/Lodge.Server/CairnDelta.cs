using System.IO.Compression;

namespace Lodge;

// 2.29: the Cairn's "what changed" delta (PROTOCOL.md "The Cairn", deltas). A delta turns the dataset this Lodge holds into the newest one:
//   "ECDELTA1" | varint oldLength | varint newLength | ops | 0x00;  op 0x01 COPY varint offset, varint length (from the old JSON);
//   op 0x02 ADD varint length, bytes.
// The result is checked against the newest dataset's signed contentSha256 by the caller (CairnReceiver); anything that does not fit -> null,
// and the caller downloads the full file as before. Kept the same as the Cairn's Delta.Apply (both have round-trip tests).
public static class CairnDelta
{
    public static readonly byte[] Magic = "ECDELTA1"u8.ToArray();

    // the new bytes, or null when the delta does not fit these old bytes or is malformed
    public static byte[] Apply(byte[] oldJson, byte[] delta, long maxNew)
    {
        try
        {
            if (delta.Length < Magic.Length + 3 || !delta.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
            int i = Magic.Length;
            var oldLen = ReadVar(delta, ref i);
            var newLen = ReadVar(delta, ref i);
            if (oldLen != (ulong)oldJson.Length || newLen > (ulong)maxNew) return null;
            var outb = new byte[newLen];
            long w = 0;
            while (true)
            {
                if (i >= delta.Length) return null;
                var op = delta[i++];
                if (op == 0) break;
                if (op == 1)
                {
                    var off = ReadVar(delta, ref i); var len = ReadVar(delta, ref i);
                    if (off > (ulong)oldJson.Length || len > (ulong)oldJson.Length - off || (ulong)w + len > newLen) return null;
                    Buffer.BlockCopy(oldJson, (int)off, outb, (int)w, (int)len);
                    w += (long)len;
                }
                else if (op == 2)
                {
                    var len = ReadVar(delta, ref i);
                    if (len > (ulong)(delta.Length - i) || (ulong)w + len > newLen) return null;
                    Buffer.BlockCopy(delta, i, outb, (int)w, (int)len);
                    i += (int)len; w += (long)len;
                }
                else return null;
            }
            return w == (long)newLen && i == delta.Length ? outb : null;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or OverflowException or ArgumentException) { return null; }
    }

    static ulong ReadVar(byte[] b, ref int i)
    {
        ulong v = 0; int shift = 0;
        while (true)
        {
            if (i >= b.Length || shift > 56) throw new IndexOutOfRangeException();
            var x = b[i++];
            v |= (ulong)(x & 0x7F) << shift;
            if (x < 0x80) return v;
            shift += 7;
        }
    }

    // unpacked, capped (a gzip bomb stops at the cap)
    public static byte[] Gunzip(byte[] gz, long cap)
    {
        using var z = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        var buf = new byte[81920]; int n;
        while ((n = z.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > cap) throw new InvalidDataException("too large");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    public static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, true)) gz.Write(data);
        return ms.ToArray();
    }
}
