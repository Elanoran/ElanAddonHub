using System;
using System.Collections.Generic;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // World Model Objects (buildings), read for their outside shape only. Written from wowdev.wiki/WMO and checked on the real files of build 1.60.1.70338
    // (chunk tags are stored reversed on disk, as for the WDT and ADT: "DHOM" for MOHD). Two kinds of file:
    //
    //   root   MOHD (64 bytes): nMaterials, nGroups, nPortals, nLights, nDoodadNames, nDoodadDefs, nDoodadSets, ambient colour, wmoId, bounding box (6 floats),
    //          flags (uint16), numLod (uint16)
    //          MOMT (nMaterials x 64 bytes): flags, shader, blend mode, texture1 (a FileDataID in this era), emissive colour, frame colour, texture2, diffuse colour,
    //          ground type, texture3, colour2, flags2, 16 bytes runtime data
    //          MOGI (nGroups x 32 bytes): flags, bounding box (6 floats), name offset - the flags are the group's, so interior groups are skipped without reading them
    //          GFID: the groups' FileDataIDs, numLod lists of nGroups (the first list is the full-detail groups)
    //          MOGN: the groups' names, zero-terminated strings; MOGI's name offset points into it (offset 0 = an empty name)
    //   group  MVER + MOGP: a 68-byte header (name offset, description offset, flags, bounding box, portal start / count, batch counts, fog ids, liquid, unique id,
    //          flags2, unknown) followed by sub-chunks:
    //          MOPY (2 bytes per triangle: flags, material id; 0xFF = no material), MOVI (uint16 indices), MOVT (3 floats per vertex, WoW world axes: x north,
    //          y west, z up), MONR (3 floats per vertex), MOBA (24-byte batches: 6 x int16 bounds, uint32 start index, uint16 count, uint16 min / max index, byte flags,
    //          byte material)
    // Group flags that matter: 0x8 = exterior (seen from outside), 0x2000 = interior. Only exterior groups are drawn.
    // ============================================================================================================================
    public sealed class WmoGroupInfo { public uint Flags; public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ; public int Fdid; public string Name = ""; }

    public sealed class WmoRoot
    {
        public int Materials, Groups, Portals, NumLod = 1;
        public int WmoId;                                      // MOHD's wmoID (offset 32): the key of the model's rows in WMOAreaTable and WMOMinimapTexture
        public ushort Flags;
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;       // the model's bounding box, WoW world axes (x north, y west, z up)
        public uint[] TextureFdid = new uint[0];               // per material: the main texture
        public uint[] MaterialFlags = new uint[0];
        public uint[] MaterialColor = new uint[0];             // diffuse colour, BGRA as stored
        public List<WmoGroupInfo> GroupInfos = new List<WmoGroupInfo>();
        public string Problem;
        public bool Ok => Problem == null;
        public const uint GroupExterior = 0x8, GroupInterior = 0x2000;

        public static WmoRoot Parse(byte[] d)
        {
            var r = new WmoRoot();
            if (d == null || d.Length < 16) { r.Problem = "the building file is empty or cut off"; return r; }
            int mohd = -1, momt = -1, mogi = -1, gfid = -1, mogn = -1, momtSize = 0, mogiSize = 0, gfidSize = 0, mognSize = 0;
            for (int p = 0; p + 8 <= d.Length;)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 0 || (long)p + 8 + size > d.Length) break;
                if (Is(d, p, "MOHD") && size >= 64) mohd = p + 8;
                else if (Is(d, p, "MOMT")) { momt = p + 8; momtSize = size; }
                else if (Is(d, p, "MOGI")) { mogi = p + 8; mogiSize = size; }
                else if (Is(d, p, "GFID")) { gfid = p + 8; gfidSize = size; }
                else if (Is(d, p, "MOGN")) { mogn = p + 8; mognSize = size; }
                p += 8 + size;
            }
            if (mohd < 0) { r.Problem = "this building has no MOHD chunk"; return r; }
            r.Materials = BitConverter.ToInt32(d, mohd); r.Groups = BitConverter.ToInt32(d, mohd + 4); r.Portals = BitConverter.ToInt32(d, mohd + 8);
            r.MinX = F(d, mohd + 36); r.MinY = F(d, mohd + 40); r.MinZ = F(d, mohd + 44); r.MaxX = F(d, mohd + 48); r.MaxY = F(d, mohd + 52); r.MaxZ = F(d, mohd + 56);
            r.WmoId = BitConverter.ToInt32(d, mohd + 32);
            r.Flags = BitConverter.ToUInt16(d, mohd + 60); r.NumLod = Math.Max(1, (int)BitConverter.ToUInt16(d, mohd + 62));
            if (r.Groups < 0 || r.Groups > 4096 || r.Materials < 0 || r.Materials > 8192) { r.Problem = "this building header is not plausible"; return r; }
            if (momt >= 0)
            {
                int n = Math.Min(r.Materials, momtSize / 64);
                r.TextureFdid = new uint[n]; r.MaterialFlags = new uint[n]; r.MaterialColor = new uint[n];
                for (int i = 0; i < n; i++) { int q = momt + i * 64; r.MaterialFlags[i] = BitConverter.ToUInt32(d, q); r.TextureFdid[i] = BitConverter.ToUInt32(d, q + 12); r.MaterialColor[i] = BitConverter.ToUInt32(d, q + 28); }
            }
            int ng = Math.Min(r.Groups, mogi >= 0 ? mogiSize / 32 : 0);
            for (int i = 0; i < r.Groups; i++)
            {
                var g = new WmoGroupInfo();
                if (i < ng)
                {
                    int q = mogi + i * 32;
                    g.Flags = BitConverter.ToUInt32(d, q); g.MinX = F(d, q + 4); g.MinY = F(d, q + 8); g.MinZ = F(d, q + 12); g.MaxX = F(d, q + 16); g.MaxY = F(d, q + 20); g.MaxZ = F(d, q + 24);
                    int no = BitConverter.ToInt32(d, q + 28);
                    if (mogn >= 0 && no > 0 && no < mognSize) g.Name = CString(d, mogn + no, mogn + mognSize);
                }
                if (gfid >= 0 && i * 4 + 4 <= gfidSize) { uint f = BitConverter.ToUInt32(d, gfid + i * 4); g.Fdid = f > int.MaxValue ? 0 : (int)f; }
                r.GroupInfos.Add(g);
            }
            if (gfid < 0) r.Problem = "this building lists no group files (GFID)";
            return r;
        }

        // the groups to draw: exterior ones; when a model has none (a dungeon shell), the ones that are not flagged interior
        public List<WmoGroupInfo> OutsideGroups()
        {
            var res = new List<WmoGroupInfo>();
            foreach (var g in GroupInfos) if (g.Fdid != 0 && (g.Flags & GroupExterior) != 0) res.Add(g);
            if (res.Count == 0) foreach (var g in GroupInfos) if (g.Fdid != 0 && (g.Flags & GroupInterior) == 0) res.Add(g);
            return res;
        }

        internal static bool Is(byte[] d, int p, string want)
        {
            bool fwd = true, rev = true;
            for (int i = 0; i < 4; i++) { if (d[p + i] != want[i]) fwd = false; if (d[p + 3 - i] != want[i]) rev = false; }
            return fwd || rev;
        }
        internal static float F(byte[] d, int o) => BitConverter.ToSingle(d, o);
        static string CString(byte[] d, int from, int end)
        {
            int e = from;
            while (e < end && d[e] != 0) e++;
            return System.Text.Encoding.UTF8.GetString(d, from, Math.Min(e - from, 128));
        }
    }

    // The renderable triangles of one group, in scene axes relative to the model's origin ((x, y, z) = (WoW y, WoW z, WoW x): an even permutation, so nothing is mirrored; the placement then turns it by Ry(rotY) Rx(rotX) Rz(rotZ)), one material per triangle.
    public sealed class WmoGroupMesh
    {
        public uint Flags;
        public float[] Pos = new float[0];          // 3 per vertex, scene axes
        public float[] Nor = new float[0];          // 3 per vertex, scene axes (empty = none)
        public ushort[] Idx = new ushort[0];        // triangles
        public byte[] Mat = new byte[0];            // per triangle
        public string Problem;
        public bool Ok => Problem == null;

        public static WmoGroupMesh Parse(byte[] d)
        {
            var m = new WmoGroupMesh();
            if (d == null || d.Length < 16) { m.Problem = "the group file is empty or cut off"; return m; }
            int mogp = -1, mogpSize = 0;
            for (int p = 0; p + 8 <= d.Length;)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 0) break;
                if (WmoRoot.Is(d, p, "MOGP")) { mogp = p + 8; mogpSize = (int)Math.Min((long)size, d.Length - (p + 8)); break; }
                p += 8 + size;
            }
            if (mogp < 0 || mogpSize < 68) { m.Problem = "this group has no MOGP chunk"; return m; }
            m.Flags = BitConverter.ToUInt32(d, mogp + 8);
            int movt = -1, movi = -1, monr = -1, mopy = -1, moba = -1, nVt = 0, nIdx = 0, nNor = 0, nPy = 0, nBa = 0;
            int end = mogp + mogpSize;
            for (int p = mogp + 68; p + 8 <= end;)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 0 || (long)p + 8 + size > end) break;
                if (WmoRoot.Is(d, p, "MOVT")) { movt = p + 8; nVt = size / 12; }
                else if (WmoRoot.Is(d, p, "MOVI")) { movi = p + 8; nIdx = size / 2; }
                else if (WmoRoot.Is(d, p, "MONR")) { monr = p + 8; nNor = size / 12; }
                else if (WmoRoot.Is(d, p, "MOPY")) { mopy = p + 8; nPy = size / 2; }
                else if (WmoRoot.Is(d, p, "MOBA")) { moba = p + 8; nBa = size / 24; }
                p += 8 + size;
            }
            if (movt < 0 || movi < 0 || nVt == 0 || nIdx < 3) { m.Problem = "this group has no vertices or triangles"; return m; }
            m.Pos = new float[nVt * 3];
            for (int i = 0; i < nVt; i++)
            {
                float x = WmoRoot.F(d, movt + i * 12), y = WmoRoot.F(d, movt + i * 12 + 4), z = WmoRoot.F(d, movt + i * 12 + 8);
                m.Pos[i * 3] = y; m.Pos[i * 3 + 1] = z; m.Pos[i * 3 + 2] = x;
            }
            if (monr >= 0 && nNor >= nVt)
            {
                m.Nor = new float[nVt * 3];
                for (int i = 0; i < nVt; i++)
                {
                    float x = WmoRoot.F(d, monr + i * 12), y = WmoRoot.F(d, monr + i * 12 + 4), z = WmoRoot.F(d, monr + i * 12 + 8);
                    m.Nor[i * 3] = y; m.Nor[i * 3 + 1] = z; m.Nor[i * 3 + 2] = x;
                }
            }
            // the rendered triangles are the batches' ranges; without batches, every triangle with a material
            var idx = new List<ushort>(nIdx); var mat = new List<byte>(nIdx / 3);
            void AddTri(int t, byte fallbackMat)
            {
                if (t < 0 || t * 3 + 3 > nIdx) return;
                ushort a = BitConverter.ToUInt16(d, movi + t * 6), b = BitConverter.ToUInt16(d, movi + t * 6 + 2), c = BitConverter.ToUInt16(d, movi + t * 6 + 4);
                if (a >= nVt || b >= nVt || c >= nVt || a == b || b == c || a == c) return;
                byte mt = mopy >= 0 && t < nPy ? d[mopy + t * 2 + 1] : fallbackMat;
                if (mt == 0xFF) return;
                idx.Add(a); idx.Add(b); idx.Add(c); mat.Add(mt);
            }
            if (moba >= 0 && nBa > 0)
            {
                for (int b = 0; b < nBa; b++)
                {
                    int q = moba + b * 24;
                    uint start = BitConverter.ToUInt32(d, q + 12); int count = BitConverter.ToUInt16(d, q + 16);
                    int t0 = (int)(start / 3), tn = count / 3;
                    for (int t = t0; t < t0 + tn; t++) AddTri(t, d[q + 23]);
                }
            }
            else for (int t = 0; t < nIdx / 3; t++) AddTri(t, 0);
            m.Idx = idx.ToArray(); m.Mat = mat.ToArray();
            if (m.Idx.Length == 0) m.Problem = "this group has no drawn triangles";
            return m;
        }
    }
}
