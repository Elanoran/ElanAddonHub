using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services.Talents
{
    // A shareable build code: "ETB1-<base64url>". Binary payload: version(1) classId(1) count(varint) then per talent (node id delta from the previous
    // one, ascending: varint; rank: 1 byte) and a CRC-16 of everything before it. URL-safe (A-Z a-z 0-9 - _, no padding), 60-130 characters for a full tree.
    // A new layout of the payload gets a new prefix number ("ETB2-"); an older Outpost then says "made by a newer version" instead of guessing.
    // The code holds the FINAL ranks, not the order they were bought in (the leveling order belongs to the recommended builds).
    // HOOK for the Lodge: BuildCode.Find(text) returns the first code inside a chat message (or null), so a pasted code can later become a chip
    // that opens this build in the calculator; the Lodge UI is not touched here.
    public static class BuildCode
    {
        public const string Prefix = "ETB";
        public const int Version = 1;
        public const int MaxNodes = 300;

        public sealed class Decoded
        {
            public bool Ok;
            public string Error;               // plain words for the person
            public bool TooNew;
            public TalentTreeDef Tree;
            public TalentState State;
            public int Dropped;                // nodes that no longer exist (a repaired duplicate counts as placed, not dropped)
        }

        public static string Encode(TalentTreeDef tree, TalentState state)
        {
            var ms = new MemoryStream();
            ms.WriteByte((byte)Version);
            ms.WriteByte((byte)Math.Max(0, Math.Min(255, tree.ClassId)));
            var items = state.Ranks.Where(kv => kv.Value > 0 && tree.Node(kv.Key) != null).OrderBy(kv => kv.Key).ToList();
            Var(ms, items.Count);
            int prev = 0;
            foreach (var kv in items)
            {
                Var(ms, kv.Key - prev); prev = kv.Key;
                ms.WriteByte((byte)Math.Min(255, kv.Value));
            }
            var body = ms.ToArray();
            var crc = Crc16(body, body.Length);
            var all = new byte[body.Length + 2];
            Array.Copy(body, all, body.Length); all[body.Length] = (byte)(crc >> 8); all[body.Length + 1] = (byte)crc;
            return Prefix + Version + "-" + B64(all);
        }

        // find(classId) gives the tree of a class (null = not in this game build)
        public static Decoded Decode(string text, Func<int, TalentTreeDef> find)
        {
            var d = new Decoded();
            text = (text ?? "").Trim();
            if (text.Length == 0) { d.Error = "Nothing to import."; return d; }
            var code = Find(text, out int ver);
            if (code == null)
            {
                if (ver > Version) { d.TooNew = true; d.Error = "This build code was made by a newer Elan's Outpost. Update the Outpost to open it."; return d; }
                d.Error = "That is not a talent build code (it should start with " + Prefix + Version + "-)."; return d;
            }
            if (code.Length > 4000) { d.Error = "That build code is too long to be real."; return d; }
            byte[] all;
            try { all = FromB64(code.Substring(code.IndexOf('-') + 1)); } catch { d.Error = "The build code is damaged (it has characters that do not belong)."; return d; }
            if (all == null || all.Length < 5) { d.Error = "The build code is damaged (too short)."; return d; }
            int blen = all.Length - 2;
            var crc = Crc16(all, blen);
            if (all[blen] != (byte)(crc >> 8) || all[blen + 1] != (byte)crc) { d.Error = "The build code is damaged (the check sum does not match). Copy it again."; return d; }
            if (all[0] != Version) { d.Error = "This build code has a layout this Outpost does not know."; d.TooNew = all[0] > Version; return d; }
            var tree = find?.Invoke(all[1]);
            if (tree == null) { d.Error = "This build is for a class that is not in the game data on this PC."; return d; }
            int pos = 2;
            try
            {
                int n = ReadVar(all, ref pos, blen);
                if (n < 0 || n > MaxNodes) { d.Error = "The build code is damaged (too many talents)."; return d; }
                var st = new TalentState();
                int id = 0;
                for (int i = 0; i < n; i++)
                {
                    id += ReadVar(all, ref pos, blen);
                    if (pos >= blen) { d.Error = "The build code is damaged (cut short)."; return d; }
                    int rank = all[pos++];
                    var node = tree.Node(tree.Resolve(id));
                    if (node == null) { d.Dropped++; continue; }
                    if (rank < 1 || rank > node.MaxRanks) { d.Error = "The build code does not fit this game version (" + NodeName(node) + " cannot have rank " + rank + ")."; return d; }
                    st.Set(node.Id, rank);
                }
                if (pos != blen) { d.Error = "The build code is damaged (extra bytes)."; return d; }
                if (d.Dropped > 0) { d.Error = "The build code does not fit this game version (" + d.Dropped + " talent" + (d.Dropped == 1 ? " is" : "s are") + " not in this tree any more)."; return d; }
                if (!TalentRules.Valid(tree, st, out var bad)) { d.Error = "That is not a legal talent layout for this game version."; return d; }
                d.Ok = true; d.Tree = tree; d.State = st;
                return d;
            }
            catch (Exception) { d.Error = "The build code is damaged."; return d; }
        }

        static string NodeName(TalentNode n) => n.Entry != null && !string.IsNullOrEmpty(n.Entry.NameOverride) ? n.Entry.NameOverride : "talent " + n.Id;

        // the first "ETB<digits>-<code>" in a text (chat message, clipboard); ver = the number found (0 = none)
        public static string Find(string text, out int ver)
        {
            ver = 0;
            if (string.IsNullOrEmpty(text)) return null;
            int at = 0;
            while ((at = text.IndexOf(Prefix, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int p = at + Prefix.Length, v = 0, digits = 0;
                while (p < text.Length && char.IsDigit(text[p]) && digits < 4) { v = v * 10 + (text[p] - '0'); p++; digits++; }
                if (digits > 0 && p < text.Length && text[p] == '-')
                {
                    int e = p + 1;
                    while (e < text.Length && (char.IsLetterOrDigit(text[e]) && text[e] < 128 || text[e] == '-' || text[e] == '_')) e++;
                    if (e > p + 1)
                    {
                        ver = v;
                        if (v == Version) return text.Substring(at, e - at);
                    }
                }
                at += Prefix.Length;
            }
            return null;
        }

        // ---- encoding helpers
        static void Var(Stream s, int v)
        {
            uint u = (uint)v;
            while (u >= 0x80) { s.WriteByte((byte)(u | 0x80)); u >>= 7; }
            s.WriteByte((byte)u);
        }

        static int ReadVar(byte[] b, ref int pos, int limit)
        {
            int shift = 0; long v = 0;
            while (true)
            {
                if (pos >= limit || shift > 28) throw new InvalidDataException("varint");
                byte x = b[pos++];
                v |= (long)(x & 0x7F) << shift;
                if ((x & 0x80) == 0) break;
                shift += 7;
            }
            if (v > int.MaxValue) throw new InvalidDataException("varint");
            return (int)v;
        }

        public static ushort Crc16(byte[] b, int len)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < len; i++)
            {
                crc ^= (ushort)(b[i] << 8);
                for (int k = 0; k < 8; k++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
            return crc;
        }

        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        static byte[] FromB64(string s)
        {
            foreach (var c in s) if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_')) return null;
            var t = s.Replace('-', '+').Replace('_', '/');
            if (t.Length % 4 == 1) return null;
            t = t.PadRight((t.Length + 3) / 4 * 4, '=');
            return Convert.FromBase64String(t);
        }
    }
}
