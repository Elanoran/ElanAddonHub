using System;
using System.Collections.Generic;

namespace ElansAddonHub.Services.WorldMap
{
    // One WMO placement (a MODF record) of an obj0 ADT. Positions are in the scene's axes already (x east, y up, z south, from the grid corner): the ADT file stores
    // the position as (17066.67 - west, height, 17066.67 - north), which is exactly how the 3D page lays the world out.
    public sealed class WmoPlacement
    {
        public uint NameId;                  // the WMO's FileDataID (this client era) - or an index into MWID/MWMO when the file still has those chunks (HasNameIndex)
        public uint UniqueId;
        public float X, Y, Z;                // position
        public float RotX, RotY, RotZ;       // degrees
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;     // the placed model's bounding box in scene axes
        public ushort Flags, DoodadSet, NameSet;
        public float Scale = 1f;             // uint16 / 1024 (0 = 1)
        public bool HasNameIndex;
    }

    // ============================================================================================================================
    // The object ADT (obj0) of a map tile, read for its WMO placements only. Written from wowdev.wiki/ADT/v18 and checked on the real obj0 files of build
    // 1.60.1.70338: a sequence of chunks (4-byte tag stored reversed on disk: "FDOM" for MODF, 4-byte size, data). MODF is 64 bytes per record:
    //   uint32 nameId, uint32 uniqueId, float position[3], float rotation[3] (degrees), float extents[2][3] (min, max), uint16 flags, uint16 doodadSet,
    //   uint16 nameSet, uint16 scale (1024 = 1.0; 0 on older files = 1.0)
    // nameId is the WMO's FileDataID in this client era; a file that still has MWID + MWMO (the old name table) is flagged (HasNameIndex) and its records are
    // left out of the building list, because a name needs the listfile, which the Outpost does not carry.
    // ============================================================================================================================
    public static class AdtObjects
    {
        public const int ModfRecord = 64;

        static bool Is(byte[] d, int p, string want)
        {
            bool fwd = true, rev = true;
            for (int i = 0; i < 4; i++) { if (d[p + i] != want[i]) fwd = false; if (d[p + 3 - i] != want[i]) rev = false; }
            return fwd || rev;
        }

        public static List<WmoPlacement> ParsePlacements(byte[] d, out string problem)
        {
            problem = null;
            var res = new List<WmoPlacement>();
            if (d == null || d.Length < 16) { problem = "the object file is empty or cut off"; return res; }
            bool nameTable = false;
            int modf = -1, modfSize = 0;
            for (int p = 0; p + 8 <= d.Length;)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 0 || (long)p + 8 + size > d.Length) break;
                if (Is(d, p, "MODF")) { modf = p + 8; modfSize = size; }
                else if (Is(d, p, "MWID") || Is(d, p, "MWMO")) { if (size > 0) nameTable = true; }
                p += 8 + size;
            }
            if (modf < 0) return res;
            for (int i = 0; i + 1 <= modfSize / ModfRecord; i++)
            {
                int q = modf + i * ModfRecord;
                var w = new WmoPlacement
                {
                    NameId = BitConverter.ToUInt32(d, q), UniqueId = BitConverter.ToUInt32(d, q + 4),
                    X = F(d, q + 8), Y = F(d, q + 12), Z = F(d, q + 16), RotX = F(d, q + 20), RotY = F(d, q + 24), RotZ = F(d, q + 28),
                    MinX = F(d, q + 32), MinY = F(d, q + 36), MinZ = F(d, q + 40), MaxX = F(d, q + 44), MaxY = F(d, q + 48), MaxZ = F(d, q + 52),
                    Flags = BitConverter.ToUInt16(d, q + 56), DoodadSet = BitConverter.ToUInt16(d, q + 58), NameSet = BitConverter.ToUInt16(d, q + 60),
                    HasNameIndex = nameTable,
                };
                int sc = BitConverter.ToUInt16(d, q + 62);
                w.Scale = sc == 0 ? 1f : sc / 1024f;
                if (!Sane(w)) continue;
                res.Add(w);
            }
            return res;
        }

        static float F(byte[] d, int o) => BitConverter.ToSingle(d, o);
        static bool Fin(float f) => !float.IsNaN(f) && !float.IsInfinity(f) && Math.Abs(f) < 200000f;

        public static bool Sane(WmoPlacement w)
        {
            return Fin(w.X) && Fin(w.Y) && Fin(w.Z) && Fin(w.RotX) && Fin(w.RotY) && Fin(w.RotZ) && Fin(w.MinX) && Fin(w.MinY) && Fin(w.MinZ) && Fin(w.MaxX) && Fin(w.MaxY) && Fin(w.MaxZ)
                && w.NameId != 0 && w.NameId <= int.MaxValue && w.Scale > 0.01f && w.Scale < 100f && w.X > -1 && w.X < 34200 && w.Z > -1 && w.Z < 34200;
        }
    }
}
