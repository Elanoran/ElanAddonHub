using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.Maps
{
    public enum MarkerKind { Spawn, Find, Vendor, Node, Quest, Entrance }

    // One marker on an AtlasMap. Fx / Fy are the fractions of the shown map's picture (0..1 from the left / top), the same numbers the game's own
    // map coordinates use (GetPlayerMapPosition x / y), so an observation and a converted Classic position meet on one map.
    public sealed class AtlasMarker
    {
        public double Fx, Fy;
        public MarkerKind Kind;
        public string Label;        // the marker's name ("Defias Pillager", "Copper Vein")
        public string Tooltip;      // the second line of the hover text ("Level 18 · 4 % drop", "Sells ...")
        public string LinkId;       // an Atlas link id ("npc:456", "obj:789", "quest:12") raised by AtlasMap.LinkClicked; null = not clickable

        public AtlasMarker() { }
        public AtlasMarker(double fx, double fy, MarkerKind kind, string label = null, string tooltip = null, string linkId = null)
        { Fx = fx; Fy = fy; Kind = kind; Label = label; Tooltip = tooltip; LinkId = linkId; }
    }

    // markers that sit close together on the screen, drawn as one
    public sealed class MarkerCluster
    {
        public double X, Y;                                         // centre on the screen
        public readonly List<AtlasMarker> Members = new List<AtlasMarker>();
        public int Count => Members.Count;
        public bool Single => Members.Count == 1;
        public bool SameKind => Members.All(m => m.Kind == Members[0].Kind);
        public MarkerKind Kind => Members[0].Kind;

        // merges markers whose screen positions are closer than `radius` (greedy, in input order, centres follow the members: deterministic).
        // O(n) with a grid; cell = radius, neighbours checked in the 3 x 3 cells around.
        public static List<MarkerCluster> Build(IReadOnlyList<AtlasMarker> markers, Func<AtlasMarker, KeyValuePair<double, double>> toScreen, double radius)
        {
            var result = new List<MarkerCluster>();
            var grid = new Dictionary<long, List<MarkerCluster>>();
            long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;
            double cell = Math.Max(1, radius);
            double r2 = radius * radius;
            foreach (var m in markers)
            {
                var p = toScreen(m);
                int cx = (int)Math.Floor(p.Key / cell), cy = (int)Math.Floor(p.Value / cell);
                MarkerCluster hit = null; double hitD = double.MaxValue;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (grid.TryGetValue(Key(cx + dx, cy + dy), out var l))
                            foreach (var c in l)
                            {
                                double d = (c.X - p.Key) * (c.X - p.Key) + (c.Y - p.Value) * (c.Y - p.Value);
                                if (d <= r2 && d < hitD) { hit = c; hitD = d; }
                            }
                if (hit == null)
                {
                    hit = new MarkerCluster { X = p.Key, Y = p.Value };
                    hit.Members.Add(m);
                    result.Add(hit);
                    var k = Key(cx, cy);
                    if (!grid.TryGetValue(k, out var gl)) grid[k] = gl = new List<MarkerCluster>();
                    gl.Add(hit);
                }
                else
                {
                    // the centre moves to the mean; keep the grid right when it crosses a cell border
                    int oldX = (int)Math.Floor(hit.X / cell), oldY = (int)Math.Floor(hit.Y / cell);
                    int n = hit.Members.Count;
                    hit.X = (hit.X * n + p.Key) / (n + 1); hit.Y = (hit.Y * n + p.Value) / (n + 1);
                    hit.Members.Add(m);
                    int nx = (int)Math.Floor(hit.X / cell), ny = (int)Math.Floor(hit.Y / cell);
                    if (nx != oldX || ny != oldY)
                    {
                        grid[Key(oldX, oldY)].Remove(hit);
                        var k = Key(nx, ny);
                        if (!grid.TryGetValue(k, out var gl)) grid[k] = gl = new List<MarkerCluster>();
                        gl.Add(hit);
                    }
                }
            }
            return result;
        }
    }
}
