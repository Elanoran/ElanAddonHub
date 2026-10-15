using System;
using System.Linq;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // the browse lists of the two character categories: every class, every playable race (grouped by side)
    public static partial class AtlasBrowse
    {
        static BIndex BuildClasses(Ctx x)
        {
            var ix = new BIndex { Noun = "class" };
            foreach (var c in ClassData.Data.ClassList())
            {
                if (string.IsNullOrEmpty(c.Name)) continue;
                var e = Entry("class", c.Id, c.Name);
                e.Client = true; e.Icon = c.IconId;
                e.Detail = string.Join(" · ", new[] { c.RoleText, c.PowerTypes.Length > 0 ? ClassSnapshot.PowerName(c.PowerTypes[0]) : "" }.Where(t => !string.IsNullOrEmpty(t)));
                ix.All.Add(e);
            }
            ix.Root.Title = "Classes";
            return ix;
        }

        static BIndex BuildRaces(Ctx x)
        {
            var ix = new BIndex { Noun = "race" };
            foreach (var r in ClassData.Data.PlayableRaces())
            {
                if (string.IsNullOrEmpty(r.Name)) continue;
                var e = Entry("race", r.Id, r.Name);
                e.Client = true; e.SubLabel = r.SideName;
                e.Detail = ClassData.Data.ClassesOf(r.Id).Count + " classes";
                ix.All.Add(e);
            }
            ix.Root.Title = "Races";
            KidsBy(ix.Root, ix.All, e => e.SubLabel, e => true);
            return ix;
        }
    }
}
