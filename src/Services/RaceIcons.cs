using System;
using System.Collections.Generic;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services
{
    // The game's race icons (2.71.1): the character-creation portraits "raceicon128-<race>-<sex>" of the client's UI texture atlases. They are not icon
    // files of their own but squares inside two big atlas textures, so each one gets a made-up icon id (Base + n, far above any FileDataID) that IconStore
    // knows how to cut out of its atlas: the same pipeline, cache (cache\icons\<id>.png) and fallbacks as the class icons.
    //
    // Source of every number (build 1.60.1.70425, read from the player's own files with the app's CASC + WDC5 readers, looked at as pictures):
    //   UiTextureAtlasMember (FileDataID 897532): CommittedName, UiTextureAtlasID, width, height, left, right, top, bottom
    //   UiTextureAtlas (897470): atlas 897 = FileDataID 1662186 (2048 x 1024, the race icons of the classic races and more),
    //                            atlas 4135 = FileDataID 8200220 (1024 x 512, "raceicon128-skyborne-<sex>-c60", 256 x 256 squares)
    // The self-test's real-data section reads the member table again and checks every rectangle here against it (sections atlasclasses, zonelore, inventory).
    public static class RaceIcons
    {
        public const int Base = 2100000000;
        public const int ClassicAtlas = 1662186, SkyborneAtlas = 8200220;

        public sealed class Crop
        {
            public string Name;          // the atlas member's name ("raceicon128-human-male")
            public int AtlasFile, Left, Top, Size;
        }

        // race (ChrRaces.ClientFileString, lower case) -> (male, female) rectangles: left, top (the squares are Size wide and high)
        static readonly List<Crop> crops = new List<Crop>();
        static readonly Dictionary<string, int> first = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        static void Add(string race, string atlasName, int atlas, int size, int ml, int mt, int fl, int ft)
        {
            first[race] = crops.Count;
            crops.Add(new Crop { Name = "raceicon128-" + atlasName + "-male" + (atlas == SkyborneAtlas ? "-c60" : ""), AtlasFile = atlas, Left = ml, Top = mt, Size = size });
            crops.Add(new Crop { Name = "raceicon128-" + atlasName + "-female" + (atlas == SkyborneAtlas ? "-c60" : ""), AtlasFile = atlas, Left = fl, Top = ft, Size = size });
        }

        static RaceIcons()
        {
            // the order is part of the ids: only ever add at the end
            Add("Human", "human", ClassicAtlas, 128, 521, 781, 521, 651);
            Add("Orc", "orc", ClassicAtlas, 128, 781, 781, 781, 651);
            Add("Dwarf", "dwarf", ClassicAtlas, 128, 391, 391, 391, 261);
            Add("NightElf", "nightelf", ClassicAtlas, 128, 781, 521, 781, 391);
            Add("Scourge", "undead", ClassicAtlas, 128, 1041, 1, 911, 781);
            Add("Tauren", "tauren", ClassicAtlas, 128, 911, 391, 911, 261);
            Add("Gnome", "gnome", ClassicAtlas, 128, 521, 1, 391, 781);
            Add("Troll", "troll", ClassicAtlas, 128, 911, 651, 911, 521);
            Add("BloodElf", "bloodelf", ClassicAtlas, 128, 261, 1, 131, 781);
            Add("Draenei", "draenei", ClassicAtlas, 128, 391, 131, 391, 1);
            Add("Skyborne", "skyborne", SkyborneAtlas, 256, 259, 1, 1, 1);
            first["Undead"] = first["Scourge"];
        }

        public static IReadOnlyList<Crop> All => crops;
        public static bool IsCrop(int id) => id >= Base && id < Base + crops.Count;
        public static Crop CropOf(int id) => IsCrop(id) ? crops[id - Base] : null;

        // the icon id of a race file ("NightElf", "Scourge", "Skyborne") and sex (UnitSex: 2 male, 3 female); 0 = the game has no icon for it
        public static int For(string clientFile, int sex)
        {
            if (string.IsNullOrEmpty(clientFile) || !first.TryGetValue(clientFile.Replace(" ", ""), out var at)) return 0;
            return Base + at + (sex == 3 ? 1 : 0);
        }

        // the icon of a race page / row: the male portrait, except High Order Skyborne (the Alliance one of the two Skyborne races, which share a portrait pair):
        // the female one, so the two rows tell apart
        public static int For(RaceInfo r)
        {
            if (r == null) return 0;
            bool skyAlliance = string.Equals(r.ClientFile, "Skyborne", StringComparison.OrdinalIgnoreCase) && r.Side == RaceInfo.Alliance;
            return For(r.ClientFile, skyAlliance ? 3 : 2);
        }

        public static int ForRaceId(int raceId)
        {
            try { return For(ClassData.Data?.Race(raceId)); } catch (Exception) { return 0; }
        }
    }
}
