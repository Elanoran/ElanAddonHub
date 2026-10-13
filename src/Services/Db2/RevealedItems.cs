using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.Db2
{
    // The Lodge hook of the item layer: the items the game client "revealed" (rows the server pushed into DBCache.bin, items the shipped tables do not
    // have) as the validated shape the Atlas sync uploads (AtlasObs.Revealed). Only fields the Outpost PARSED from the client's own rows are shared:
    // id, name, quality, item level, required level, class / subclass, slot (inventory type), stat amounts. The armor and weapon damage are worked out
    // from item-level tables the same way for everybody (see ItemDetails), so they are kept alongside for the Atlas page and not sent: the Lodge wire
    // format (server/PROTOCOL.md, atlas.share items) has no field for them yet. No character names, no paths, nothing about the player.
    public sealed class RevealedItemsSource : AtlasObs.IRevealedItemsSource
    {
        public IEnumerable<AtlasObs.Revealed> Items()
        {
            var snap = ItemData.Snapshot;
            if (snap == null) return new AtlasObs.Revealed[0];
            return From(snap.Items.Values);
        }

        // the revealed ones among `items`, in id order
        public static List<AtlasObs.Revealed> From(IEnumerable<ClientItem> items)
        {
            var res = new List<AtlasObs.Revealed>();
            foreach (var it in items.Where(i => i != null && i.Revealed && i.Id >= 1 && i.Id <= AtlasObs.MaxItemId).OrderBy(i => i.Id))
            {
                var name = AtlasObs.CleanName(it.Name);
                if (name.Length == 0 || it.Quality < 0 || it.Quality > 7) continue;
                var r = new AtlasObs.Revealed
                {
                    Id = it.Id, Name = name, Quality = it.Quality, ItemLevel = Clamp(it.ItemLevel, 0, 1000), RequiredLevel = Clamp(it.RequiredLevel, 0, 100),
                    Class = Clamp(it.ClassId, 0, 30), SubClass = Clamp(it.SubclassId, 0, 30), Slot = Clamp(it.InventoryType, 0, 30),
                };
                // stat amounts (only when they could be worked out); the wire format takes types 0..127, values within 16 bits, at most 10
                var st = new List<int[]>();
                if (it.Stats != null)
                    for (int i = 0; i + 1 < it.Stats.Length && st.Count < AtlasObs.MaxStats; i += 2)
                    {
                        int type = it.Stats[i], val = it.Stats[i + 1];
                        if (type < 0 || type > 127 || val < -32768 || val > 32767 || val == 0) continue;
                        st.Add(new[] { type, val });
                    }
                r.Stats = st.ToArray();
                // what the Outpost works out for this item (not part of the wire format yet)
                r.Armor = it.ClassId == 4 ? it.Armor : 0;
                if (it.ClassId == 2 && it.TryDamage(out int mn, out int mx, out double dps)) { r.DmgMin = mn; r.DmgMax = mx; }
                res.Add(r);
            }
            return res;
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
