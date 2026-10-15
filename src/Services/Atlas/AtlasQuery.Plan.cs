using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    public static partial class AtlasQuery
    {
        // Collects markers from the Classic baseline (world coordinates of a map: turned into a zone map position here) and from the Lodge (zone map
        // fractions already) into one plan per zone. Capped: a page never carries more than MaxMarkers.
        sealed class PlanBuilder
        {
            readonly Dictionary<int, AtlasMapZone> zones = new Dictionary<int, AtlasMapZone>();
            readonly Dictionary<int, int> inInstance = new Dictionary<int, int>();
            public int Unplaced, Total;
            public bool MapsMissing;

            public bool Full => Total >= MaxMarkers;

            void Put(int uiMap, double fx, double fy, MarkerKind kind, string label, string tip, string link)
            {
                if (Total >= MaxMarkers) return;
                if (!zones.TryGetValue(uiMap, out var z)) zones[uiMap] = z = new AtlasMapZone { UiMapId = uiMap, Name = ZoneName(uiMap) };
                z.Markers.Add(new AtlasMarker(fx, fy, kind, label, tip, link));
                Total++;
            }

            public void Spot(int uiMap, double fx, double fy, MarkerKind kind, string label, string tip, string link)
            {
                if (uiMap <= 0 || fx < 0 || fx > 1 || fy < 0 || fy > 1) { Unplaced++; return; }
                Put(uiMap, fx, fy, kind, label, tip, link);
            }

            // up to MaxSpawnsPerSource of a thing's spawn points, spread over the list (not just the first ones)
            public void Classic(IReadOnlyList<ClassicSpawn> spawns, MarkerKind kind, string label, string tip, string link, int max = MaxSpawnsPerSource)
            {
                if (spawns == null || spawns.Count == 0) return;
                var idx = MapData.Index;
                if (idx == null) { MapsMissing = true; return; }
                int take = Math.Min(spawns.Count, Math.Max(1, max));
                double step = spawns.Count / (double)take;
                for (int i = 0; i < take; i++)
                {
                    var s = spawns[(int)Math.Min(spawns.Count - 1, Math.Floor(i * step))];
                    var p = idx.WorldToMap(s.Map, s.X, s.Y);
                    if (p == null)
                    {
                        if (idx.Instance(s.Map) != null && !idx.Instance(s.Map).HasMap) { inInstance.TryGetValue(s.Map, out var c); inInstance[s.Map] = c + 1; }
                        else Unplaced++;
                        continue;
                    }
                    Put(p.Value.UiMapId, p.Value.Fx, p.Value.Fy, kind, label, tip, link);
                }
            }

            public AtlasMapPlan Build()
            {
                var plan = new AtlasMapPlan { Unplaced = Unplaced };
                plan.Zones = zones.Values.OrderByDescending(z => z.Forever).ThenByDescending(z => z.Markers.Count).ThenBy(z => z.Name, StringComparer.OrdinalIgnoreCase).ToList();
                plan.InInstances = inInstance.OrderByDescending(k => k.Value).ThenBy(k => k.Key).ToList();
                if (MapsMissing) plan.Note = "Maps are not read from your game, so spawn points cannot be placed. Check Settings > General > Game data.";
                else if (plan.Zones.Count == 0 && plan.InInstances.Count > 0) plan.Note = "These places are inside instances, which have no map in the game files.";
                if (Total >= MaxMarkers) plan.Note = (plan.Note.Length > 0 ? plan.Note + " " : "") + "Showing the first " + MaxMarkers + " places.";
                return plan;
            }
        }

        // the zone most of an npc's spawn points are in, for a table row ("Elwynn Forest"); "" when unknown or inside an instance
        internal static string ZoneOfSpawns(IReadOnlyList<ClassicSpawn> spawns)
        {
            var idx = MapData.Index;
            if (idx == null || spawns == null || spawns.Count == 0) return "";
            var count = new Dictionary<int, int>();
            string instance = "";
            int step = Math.Max(1, spawns.Count / 12);
            for (int i = 0; i < spawns.Count; i += step)
            {
                var s = spawns[i];
                var p = idx.WorldToMap(s.Map, s.X, s.Y);
                if (p == null) { var inst = idx.Instance(s.Map); if (inst != null && !inst.Placeholder) instance = inst.Name; continue; }
                count.TryGetValue(p.Value.UiMapId, out var c); count[p.Value.UiMapId] = c + 1;
            }
            if (count.Count == 0) return instance;
            return ZoneName(count.OrderByDescending(k => k.Value).ThenBy(k => k.Key).First().Key);
        }
    }
}
