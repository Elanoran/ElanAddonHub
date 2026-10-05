using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace ElansAddonHub.Lodge
{
    // The preset personal avatars (original vector art in Theme/Avatars.xaml) and the 8 accent colours.
    // The ids are the server's whitelist (server/Lodge.Server/Profiles.cs ProfileRules) - keep both lists in step.
    public static class AvatarCatalog
    {
        public static readonly string[] Ids =
        {
            "av.wolf", "av.bear", "av.raptor", "av.owl", "av.boar", "av.lion", "av.serpent", "av.spider",
            "av.sword", "av.shield", "av.bow", "av.staff", "av.hammer", "av.axe", "av.skull", "av.gem",
            "av.potion", "av.campfire", "av.banner", "av.moon", "av.sun", "av.flame", "av.fish", "av.chicken",
        };

        public static readonly string[] AccentIds = { "gold", "crimson", "emerald", "teal", "azure", "violet", "rose", "slate" };
        static readonly string[] AccentHex = { "#E6B85C", "#D8484A", "#3FB27A", "#2FB5B0", "#4C8FE0", "#9A63DC", "#E0679A", "#8A94A3" };

        public static bool IsAvatar(string id) => id != null && Array.IndexOf(Ids, id) >= 0;
        public static bool IsAccent(string id) => id != null && Array.IndexOf(AccentIds, id) >= 0;

        public static string Name(string id)
        {
            if (!IsAvatar(id)) return "";
            var s = id.Substring(3);
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        public static string AccentName(string id) => IsAccent(id) ? char.ToUpperInvariant(id[0]) + id.Substring(1) : "";

        public static Color AccentColor(string id)
        {
            int i = Array.IndexOf(AccentIds, id);
            return (Color)ColorConverter.ConvertFromString(i >= 0 ? AccentHex[i] : "#8A94A3");
        }

        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>();

        // the finished round avatar: tinted background + subject + vignette + bronze frame. null for an unknown id.
        public static ImageSource Image(string id, string accent)
        {
            if (!IsAvatar(id)) return null;
            var tint = IsAccent(accent) ? accent : "none";
            var key = id + "|" + tint;
            lock (cache)
            {
                if (cache.TryGetValue(key, out var hit)) return hit;
                var res = Application.Current?.Resources;
                if (res == null) return null;
                ImageSource img = null;
                try
                {
                    var g = new DrawingGroup();
                    var inner = new DrawingGroup { ClipGeometry = (Geometry)res["AvClip"] };
                    inner.Children.Add((Drawing)res["AvBg." + tint]);
                    inner.Children.Add((Drawing)res["AvS." + id.Substring(3)]);
                    g.Children.Add(inner);
                    g.Children.Add((Drawing)res["AvFinish"]);
                    g.Children.Add((Drawing)res["AvFrame"]);
                    var di = new DrawingImage(g);
                    img = di;
                }
                catch (Exception e) { Services.Util.Log("avatar: " + e.Message); }
                cache[key] = img;
                return img;
            }
        }
    }
}
