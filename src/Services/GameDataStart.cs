using System;
using System.Threading.Tasks;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services
{
    // The start of the game-data readers, in one place (the window calls it once, a moment after it is shown; the cold-start self-test calls it too).
    // They used to start one by one between 2.5 and 4 s, each opening the game storage on its own. Now they start together (they share one opened
    // storage, CascShared): the maps first, because the Atlas pages wait for them most, then items (which starts spells and classes), what the client has
    // seen and the talent trees. Each reader still runs on its own background thread and reports to GameDataProgress.
    public static class GameDataStart
    {
        public static int DelayMs = 2500;

        public static void StartAll()
        {
            if (MapData.AutoStart) MapData.Start();
            if (ItemData.AutoStart) ItemData.Start();          // + SpellData, ClassData
            if (SeenData.AutoStart) SeenData.Start();
            if (TalentData.AutoStart) TalentData.Start();
        }

        // the switches are read now (the self-test turns them off before the window is made, and on again in its own sections)
        public static Task StartAllLater()
        {
            bool m = MapData.AutoStart, i = ItemData.AutoStart, s = SeenData.AutoStart, t = TalentData.AutoStart;
            if (!m && !i && !s && !t) return Task.CompletedTask;
            return Task.Delay(DelayMs).ContinueWith(_ => { if (m) MapData.Start(); if (i) ItemData.Start(); if (s) SeenData.Start(); if (t) TalentData.Start(); });
        }
    }
}
