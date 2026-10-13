using System;
using System.Collections.Generic;
using System.Text;

namespace ElansAddonHub.Services.Db2
{
    // ============================================================================================================================
    // Atlas layer A3: the client's own "things I have met" caches, Cache\WDB\<locale>\*.wdb. The game writes a record into them every time the server
    // answers a creature / game object / quest query, so they hold exactly what this player has seen on WoW Forever.
    //
    // The container (verified against the real files of build 70338, 1.60.1):
    //   header, 24 bytes   char[4] magic (stored reversed, so "BOMW" in the file is WMOB = creature, WGOB = game object, WQST = quest), u32 build, char[4] locale (reversed: "SUne" = enUS),
    //                      u32 (a size, not used), u32 record version, u32 cache version
    //   records            u32 id, u32 length, <length> bytes (the server's query answer without the id)
    //   end                u32 0, u32 0   (a file the game is still writing, or one that was cut off, simply ends early: the complete records before it are kept)
    // What is inside a record is NOT described by the file (no layout hash like DB2 has). The decoders in WdbSeen.cs therefore check every record against
    // the exact record length they compute; a record that does not fit is skipped, and a file where most records do not fit is reported as "layout not
    // recognised" and not used at all. Nothing is guessed.
    // ============================================================================================================================
    public struct WdbEntry { public int Id, Offset, Length; }

    public sealed class WdbFile
    {
        public string Magic, Locale;                 // as they read (reversed back from the file's byte order)
        public int Build, RecordVersion, CacheVersion;
        public byte[] Data;
        public List<WdbEntry> Entries = new List<WdbEntry>();
        public bool Truncated;                       // the file ended inside a record or without the end marker
        public int Duplicates;                       // ids that appeared more than once (the last one wins)
    }

    public static class Wdb
    {
        public const int HeaderSize = 24;
        public const int MaxRecord = 1 << 20;        // a larger length is a damaged file
        public const string CreatureMagic = "WMOB", GameObjectMagic = "WGOB", QuestMagic = "WQST";

        // reads the container. Throws Db2Exception for something that is not a WDB file of the expected kind (wrong magic, too short).
        public static WdbFile Parse(byte[] data, string expectedMagic)
        {
            if (data == null || data.Length < HeaderSize) throw new Db2Exception("the cache file is too short");
            var f = new WdbFile { Data = data, Magic = Rev(data, 0), Build = BitConverter.ToInt32(data, 4), Locale = Rev(data, 8), RecordVersion = BitConverter.ToInt32(data, 16), CacheVersion = BitConverter.ToInt32(data, 20) };
            if (expectedMagic != null && f.Magic != expectedMagic) throw new Db2Exception("not a " + expectedMagic + " cache file (" + SafeText(f.Magic) + ")");
            long p = HeaderSize;
            var index = new Dictionary<int, int>();
            bool ended = false;
            while (p + 8 <= data.Length)
            {
                uint id = BitConverter.ToUInt32(data, (int)p), len = BitConverter.ToUInt32(data, (int)p + 4);
                if (id == 0 && len == 0) { ended = true; break; }
                if (len > MaxRecord || p + 8 + len > data.Length) break;      // cut off or damaged: stop, keep what is complete
                if (id <= int.MaxValue)
                {
                    var e = new WdbEntry { Id = (int)id, Offset = (int)p + 8, Length = (int)len };
                    if (index.TryGetValue(e.Id, out int at)) { f.Entries[at] = e; f.Duplicates++; }
                    else { index[e.Id] = f.Entries.Count; f.Entries.Add(e); }
                }
                p += 8 + len;
            }
            f.Truncated = !ended;
            return f;
        }

        static string Rev(byte[] d, int at)
        {
            var c = new char[4];
            for (int i = 0; i < 4; i++) c[i] = (char)d[at + 3 - i];
            return new string(c);
        }

        static string SafeText(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s) sb.Append(ch >= 32 && ch < 127 ? ch : '?');
            return sb.ToString();
        }

        public static byte[] ReadShared(string path) => DbCache.ReadAllShared(path);
    }

    // most significant bit first, the way the server's query answers pack their string lengths
    public sealed class WdbBits
    {
        readonly byte[] d; readonly int start, end; int pos;
        public WdbBits(byte[] data, int offset, int length) { d = data; start = offset; end = offset + length; pos = 0; }
        public int Read(int bits)
        {
            int v = 0;
            for (int i = 0; i < bits; i++)
            {
                int at = start + (pos >> 3);
                if (at >= end) throw new Db2Exception("bit header is cut off");
                v = (v << 1) | ((d[at] >> (7 - (pos & 7))) & 1);
                pos++;
            }
            return v;
        }
    }
}
