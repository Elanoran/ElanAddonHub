using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Professions
{
    // ============================================================================================================================
    // WoW Forever "camping", found in the client's own tables (nothing is hard-coded by id or name):
    //   * a camp recipe is a profession recipe whose spell rank text is "Tier n" (Basic Campfire [Tier 1], Sharpening Wheel [Tier 1], Master Forge [Tier 3] ...)
    //   * it makes an item (the kit / the camp object) whose "Use:" spell describes what it does; a kit's text says "the placement of up to N additional
    //     camp features", everything else is a camp object
    //   * a recipe of Tier 2 and up is taught by a "Blueprint: ..." item (a normal recipe item whose effect teaches the recipe spell)
    // There is no "Camping" skill line in this client: camp objects are ordinary recipes of their profession (the profession's skill is what
    // gates them), with a skill band that starts at the skill from which they can be made (20 / 140 / 300 ...).
    // ============================================================================================================================
    public sealed class CampItem
    {
        public ProfRecipe Recipe;
        public string Name => Recipe.Name;
        public int Tier => Recipe.Tier;
        public int SkillId => Recipe.SkillId;
        public string Profession;
        public int ItemId => Recipe.ItemId;
        public int UseSpellId;
        public string UseText = "";            // the use spell's text, the common trailer ("Requires a Campfire nearby ...") removed
        public string Trailer = "";            // that trailer
        public bool IsKit;
        public int Capacity;                   // kits: how many camp objects the fire takes
        public long FireMs;                    // kits: how long the fire burns
        public int BenefitMinutes;             // kits: sitting / crafting time before the benefits apply
        public string Side = "";               // "Alliance" / "Horde" for a faction banner, else ""
        public string Upgrades = "";           // "Sharpening Wheel": what this object replaces (and includes the benefits of)
        public string ExclusiveWith = "";      // "Strength of Earth Totem": the class buff it does not stack with
        public string Buff = "";               // the short stat line: "34 increased Strength"
        public int Needs => Recipe.Low;        // the skill from which it can be made (the start of the recipe's band)
        public int BlueprintItemId => Recipe.TeachingItems.Count > 0 ? Recipe.TeachingItems[0] : 0;
        public bool NeedsBlueprint => Recipe.TeachingItems.Count > 0;
    }

    public sealed class CampBook
    {
        public readonly List<CampItem> Kits = new List<CampItem>();
        public readonly List<CampItem> Objects = new List<CampItem>();
        public string CooldownText = "";       // "1 hour": read from the objects' text
        public bool HasData => Kits.Count + Objects.Count > 0;
        public IEnumerable<CampItem> All => Kits.Concat(Objects);

        static readonly Regex Capacity = new Regex(@"up to (\d+) additional camp features", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex Minutes = new Regex(@"for (\d+) min", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex Cooldown = new Regex(@"share a cooldown of ([^.]+)\.", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex Over = new Regex(@"placed over (?:a|an|the) ([^.]+?) to replace it", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex Exclusive = new Regex(@"mutually exclusive with ([^./,]+(?: [A-Z][\w']*)*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex Gain = new Regex(@"(?:gain|grants?(?: you and others sitting nearby)?|regenerate) ([^,.]*?)(?:, mutually|\.|,|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static CampBook Build(ProfessionBook book, Func<int, SpellView> describe)
        {
            var cb = new CampBook();
            var sn = book.Snapshot;
            if (sn == null) return cb;
            foreach (var p in book.Professions)
                foreach (var r in p.Recipes)
                {
                    if (!r.IsCamp || r.ItemId <= 0) continue;
                    var ci = new CampItem { Recipe = r, Profession = p.Name };
                    // the use spell of the made item: the first item effect that is a "Use:" (trigger 0) of a camp spell
                    if (book.EffectsOf(r.ItemId) is int[] fx)
                        for (int i = 0; i + 1 < fx.Length; i += 2)
                        {
                            if (fx[i] != 0) continue;
                            var us = sn.Get(fx[i + 1]);
                            if (us == null) continue;
                            ci.UseSpellId = us.Id;
                            if (us.Rank != null && us.Rank.StartsWith("Tier", StringComparison.OrdinalIgnoreCase)) break;
                        }
                    var view = ci.UseSpellId > 0 ? describe?.Invoke(ci.UseSpellId) : null;
                    var text = (view?.Description ?? "").Replace("\r", "");
                    // split the text into what it does and the shared trailer
                    int req = text.IndexOf("Requires a ", StringComparison.OrdinalIgnoreCase);
                    int cd = text.IndexOf("All camping features share", StringComparison.OrdinalIgnoreCase);
                    int cut = req >= 0 ? req : cd >= 0 ? cd : text.Length;
                    ci.UseText = Collapse(text.Substring(0, cut));
                    ci.Trailer = Collapse(text.Substring(cut));
                    var mCd = Cooldown.Match(text);
                    if (mCd.Success && cb.CooldownText.Length == 0) cb.CooldownText = mCd.Groups[1].Value.Trim();
                    var mCap = Capacity.Match(text);
                    if (mCap.Success)
                    {
                        ci.IsKit = true;
                        ci.Capacity = int.Parse(mCap.Groups[1].Value, CultureInfo.InvariantCulture);
                        var mm = Minutes.Match(text);
                        if (mm.Success) ci.BenefitMinutes = int.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (view != null) ci.FireMs = view.DurationMs;
                    }
                    if (text.IndexOf("members of the Alliance", StringComparison.OrdinalIgnoreCase) >= 0) ci.Side = "Alliance";
                    else if (text.IndexOf("members of the Horde", StringComparison.OrdinalIgnoreCase) >= 0) ci.Side = "Horde";
                    var mo = Over.Match(text);
                    if (mo.Success) ci.Upgrades = mo.Groups[1].Value.Trim();
                    var me = Exclusive.Match(ci.UseText);
                    if (me.Success) ci.ExclusiveWith = me.Groups[1].Value.Trim().TrimEnd(',', '.');
                    if (!ci.IsKit)
                    {
                        var mg = Gain.Match(ci.UseText);
                        if (mg.Success) ci.Buff = Collapse(mg.Groups[1].Value);
                    }
                    (ci.IsKit ? cb.Kits : cb.Objects).Add(ci);
                }
            cb.Kits.Sort((a, b) => a.Capacity != b.Capacity ? a.Capacity.CompareTo(b.Capacity) : a.Tier.CompareTo(b.Tier));
            var objs = cb.Objects.OrderBy(o => o.Profession, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Tier).ThenBy(o => o.Needs).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
            cb.Objects.Clear(); cb.Objects.AddRange(objs);
            return cb;
        }

        static string Collapse(string s) => Regex.Replace((s ?? "").Replace('\n', ' '), @"\s{2,}", " ").Trim();

        // camp objects grouped by profession, each profession's objects lowest tier first
        public IEnumerable<IGrouping<string, CampItem>> ByProfession() => Objects.GroupBy(o => o.Profession);
    }
}
