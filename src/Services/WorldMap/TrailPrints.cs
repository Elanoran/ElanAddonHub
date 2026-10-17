using System;
using System.Collections.Generic;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // 2.88: the paw-print trail. The live trail (LivePosition) is drawn by the world map pages as footprints in your class colour when you zoom in
    // (trailprints.js draws them; zoomed out it stays the plain line). What the app adds for it is small and lives here:
    //   - how you moved at each trail point (LiveMove bits: on a flight, indoors, swimming, mounted, pet out, dead / ghost), from the strip;
    //   - an odometer per point: yards along the trail so far, so the pages can put a print every few yards at fixed distances of it (the prints
    //     stay where they are while old points drop off the end of the 10 minutes);
    //   - the kind of foot: hooves for tauren, bony prints for the undead, tiny prints for gnomes, paws for everyone else; and whether the Hub
    //     says if your pet is out (Elan's Hub 1.17.0+; with an older Hub a hunter's pet prints are always drawn).
    // Like the trail itself it is memory only and never sent anywhere.
    // ============================================================================================================================
    public static class TrailPrints
    {
        // the page's layer row (WorldMapLayers.Catalog), plain words
        public const string LayerId = "trailprints";
        public const string LayerTitle = "Paw prints";
        public const string LayerTip = "Your live trail as little footprints in your class colour when you zoom in: paws on foot, hoofprints when mounted, ripples when swimming, dots and a feather on a flight, faint wisps as a ghost; hunters get their pet's paws beside them. Zoomed out it stays a line";

        // the move bits of a trail point. From an Elan's Hub before 1.16.1 nothing about moving is known (0 = on foot); before 1.17.0 the mounted and
        // pet bits are not sent (masked off here too, whatever the strip said). Dead / ghost comes from the strip's presence flags (every Hub since 1.5).
        public static int MoveOf(GamePresence.Character c)
        {
            if (c == null) return 0;
            int m = c.PosMoveKnown ? c.PosMove & (c.PosMountKnown ? LiveMove.Taxi | LiveMove.Indoors | LiveMove.Swimming | LiveMove.Mounted | LiveMove.PetOut : LiveMove.Taxi | LiveMove.Indoors | LiveMove.Swimming) : 0;
            if (c.Dead) m |= LiveMove.Dead;
            return m;
        }

        // the odometer of a new point: the last one's plus the step, when it is a step (same map, not a jump of more than TrailBreakYards: a
        // teleport, a hearthstone, a gap); the first point starts at 0
        public static double NextOdo(LiveTrail.Point? last, LiveTrail.Point p)
        {
            if (!last.HasValue) return 0;
            var l = last.Value;
            if (l.MapId != p.MapId) return l.Odo;
            double d = LiveTrail.Dist(l.X, l.Y, p.X, p.Y);
            return double.IsNaN(d) || double.IsInfinity(d) || d > WorldMapProtocol.TrailBreakYards ? l.Odo : l.Odo + d;
        }

        // the kind of foot by the race's file name (PixelStrip's RaceFiles): hoof | bone | tiny | paw
        public static string FootOf(string raceFile)
        {
            switch ((raceFile ?? "").ToUpperInvariant())
            {
                case "TAUREN": return "hoof";
                case "SCOURGE": case "UNDEAD": return "bone";
                case "GNOME": return "tiny";
                default: return "paw";
            }
        }

        // who the trail belongs to, as far as the prints care: race, class, whether the Hub tells if the pet is out
        public sealed class Owner
        {
            public string RaceFile { get; private set; }
            public string ClassFile { get; private set; }
            public bool PetKnown { get; private set; }
            public void Take(GamePresence.Character c)
            {
                if (c == null) return;
                RaceFile = c.RaceFile; ClassFile = c.ClassFile; PetKnown = c.PosMoveKnown && c.PosMountKnown;
            }
            public bool Hunter => string.Equals(ClassFile, "HUNTER", StringComparison.OrdinalIgnoreCase);
        }

        // the "prints" part of the me message: {foot, hunter, petKnown}
        public static Dictionary<string, object> Style(Owner o) => new Dictionary<string, object>
        {
            ["foot"] = FootOf(o?.RaceFile), ["hunter"] = o != null && o.Hunter, ["petKnown"] = o != null && o.PetKnown,
        };
    }
}
