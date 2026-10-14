using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Lodge
{
    // An item chip under a chat message: the icon (from the player's own game files via IconStore, when the Bags data or the Atlas item data knows the item),
    // the name in its quality colour, a tooltip, and a click that opens the item on Wowhead Classic.
    // A pasted Atlas link (item:2589, npc:46, quest:783, zone:1411, browse:items/weapons ...) is a chip too: the kind's glyph, the name the Atlas
    // knows (item names and icons come from the Atlas item data first), and a click that opens the Atlas page (never Wowhead).
    public class ItemChipVM : Bindable
    {
        public ItemRef Ref { get; }
        public ItemInfo Info { get; }
        string name; int quality; string tipLine;
        public string Name { get => name; private set { if (Set(ref name, value)) Raise(nameof(TipTitle)); } }
        public int Quality { get => quality; private set { if (Set(ref quality, value)) Raise(nameof(NameBrush)); } }
        public bool Known => Ref.Id > 0 && Info != null;
        public bool IsAtlas => Ref.AtlasId != null;
        public string Url => ItemLinks.UrlFor(Ref);
        public Task Resolving { get; private set; } = Task.CompletedTask;   // self-test: the Atlas lookup of a link the item tables do not answer

        // the quiet tile's glyph: a bag for items, the kind's own glyph for Atlas links
        public string TileGlyph { get; } = "";

        ImageSource icon;
        public ImageSource Icon { get => icon; private set { if (Set(ref icon, value)) { Raise(nameof(IconVisibility)); Raise(nameof(TileVisibility)); } } }
        public Visibility IconVisibility => icon != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TileVisibility => icon != null ? Visibility.Collapsed : Visibility.Visible;

        public Brush NameBrush => Quality >= 0 && Quality < ItemLinks.QualityHex.Length ? Avatar.Frozen(ItemLinks.QualityHex[Quality]) : Avatar.Res("Brush.Text.Secondary");

        public string TipTitle => Name;
        public string TipLine { get => tipLine ?? DefaultTipLine; private set => Set(ref tipLine, value); }
        string DefaultTipLine => (Quality >= 0 && Quality < ItemLinks.QualityNames.Length ? ItemLinks.QualityNames[Quality] + " · " : "") + (Ref.Id > 0 ? "Item #" + Ref.Id : "Item name not known here");
        public string TipHint => IsAtlas ? "Click to open in the Atlas" : Ref.Id > 0 ? "Click to open on Wowhead Classic · Ctrl+click for Atlas" : "Click to search Wowhead Classic";

        // the hover tooltip: the game's own for an item or an Atlas thing, the chip's lines in the footer
        public GameTooltip BuildTip()
        {
            GameTooltip gt = null;
            try
            {
                var ci = Ref.Id > 0 ? ItemData.Get(Ref.Id) : null;
                if (ci != null && (!IsAtlas || Ref.AtlasKind == "item")) gt = new GameTooltip(Name, Quality).AddItem(ci);
                else if (IsAtlas && Ref.AtlasKind != "item" && Ref.AtlasKind != "browse" && Ref.AtlasKind != "talents" && Ref.AtlasKind != "professions")
                {
                    var m = AtlasQuery.Build(Ref.AtlasId);
                    if (m != null && !m.NotFound) gt = GameTooltip.FromAtlas(m);
                }
            }
            catch (Exception e) { Util.Log("chip tooltip: " + e.Message); }
            if (gt == null)
            {
                gt = new GameTooltip(Name, Quality);
                bool simple = IsAtlas && (Ref.AtlasKind == "browse" || Ref.AtlasKind == "talents" || Ref.AtlasKind == "professions");
                gt.AddText(simple ? TipLine : "Not in the game data on this PC", "grey");
                if (!simple && Ref.Id > 0) gt.AddText(TipLine, "grey");
            }
            gt.Footer.Children.Add(GameTooltip.Muted(TipHint));
            return gt;
        }

        public ItemChipVM(ItemRef r, ItemInfo info, ClientItem atlasItem = null)
        {
            Ref = r; Info = info;
            name = atlasItem?.Name ?? info?.Name ?? r.Name ?? (r.Id > 0 ? "Item #" + r.Id : "Item");
            quality = r.Quality >= 0 ? r.Quality : atlasItem != null ? atlasItem.Quality : info != null ? info.Quality : -1;
            var iconId = info != null && info.Icon > 0 ? info.Icon : atlasItem?.IconId ?? 0;
            if (iconId > 0) LoadIcon(iconId);
            if (!IsAtlas) return;
            if (r.AtlasKind == "item")
            {
                if (atlasItem == null && info == null) { name = "Item " + r.Id; Resolve(); }   // not in the game data here: the Lodge / Classic layers may still know it
                return;
            }
            TileGlyph = GlyphOf(r);
            if (r.AtlasKind == "browse" || r.AtlasKind == "talents" || r.AtlasKind == "professions") { name = PathTitle(r, out var line); tipLine = line; }
            else { name = !string.IsNullOrEmpty(r.Name) ? r.Name : AtlasKinds.Singular(r.AtlasKind) + " " + r.AtlasId.Substring(r.AtlasId.IndexOf(':') + 1); tipLine = AtlasKinds.Singular(r.AtlasKind) + " · not known on this PC"; Resolve(); }
        }

        static string GlyphOf(ItemRef r)
        {
            try
            {
                if (r.AtlasKind == "browse")
                {
                    if (BrowseQuery.TryParse(r.AtlasId, out var q)) { var c = AtlasCategories.Find(q.Cat); if (c != null && !string.IsNullOrEmpty(c.Glyph)) return c.Glyph; }
                    return AtlasKinds.Glyph("item");
                }
                if (r.AtlasKind == "talents" || r.AtlasKind == "professions")
                {
                    var c = AtlasCategories.Find(r.AtlasKind);
                    return c != null && !string.IsNullOrEmpty(c.Glyph) ? c.Glyph : AtlasKinds.Glyph(r.AtlasKind == "talents" ? "spell" : "skill");
                }
            }
            catch (Exception) { }
            return AtlasKinds.Glyph(r.AtlasKind);
        }

        static string Pretty(string slug) => string.Join(" ", (slug ?? "").Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));

        // the title of a browse / talents / professions link, from the id alone (nothing is built or loaded)
        static string PathTitle(ItemRef r, out string line)
        {
            line = "Atlas page";
            try
            {
                if (r.AtlasKind == "browse" && BrowseQuery.TryParse(r.AtlasId, out var q))
                {
                    var cat = AtlasCategories.Find(q.Cat);
                    line = "Atlas list" + (q.HasFilters ? " · with filters" : "");
                    return string.Join(" › ", new[] { cat?.Title ?? Pretty(q.Cat) }.Concat(q.Path.Select(Pretty)));
                }
                var sub = r.AtlasId.Substring(r.AtlasId.IndexOf(':') + 1);
                if (r.AtlasKind == "talents")
                {
                    line = "Talent calculator";
                    var t = AtlasHosts.TalentsSubTitle(sub);
                    return t.Length == 0 ? "Talents" : t == "Build" ? "Talent build" : "Talents · " + t;
                }
                if (r.AtlasKind == "professions")
                {
                    line = "Professions";
                    var t = AtlasHosts.ProfessionsSubTitle(sub);
                    return t.Length == 0 ? "Professions" : "Professions · " + t;
                }
            }
            catch (Exception) { }
            return r.AtlasId;
        }

        // the Atlas knows more than the item table (spells, NPCs, quests, zones, the Lodge's revealed items): asked off the UI thread, the chip updates when it answers
        void Resolve()
        {
            var id = Ref.AtlasId;
            var disp = Application.Current?.Dispatcher;
            Resolving = Task.Run(() =>
            {
                AtlasHit hit = null;
                try { hit = AtlasQuery.Describe(id); } catch (Exception e) { Util.Log("chat atlas link " + id + ": " + e.Message); }
                if (hit == null || string.IsNullOrEmpty(hit.Name)) return;
                void Apply()
                {
                    Name = hit.Name;
                    if (hit.Quality >= 0) Quality = hit.Quality;
                    TipLine = AtlasKinds.Singular(Ref.AtlasKind) + (string.IsNullOrEmpty(hit.Detail) ? "" : " · " + hit.Detail);
                    if (hit.IconId > 0) LoadIcon(hit.IconId);
                }
                if (disp != null) disp.BeginInvoke(new Action(Apply)); else Apply();
            });
        }

        // from disk when it is already extracted, otherwise requested once (read-only from the local game files) and filled in when it arrives
        void LoadIcon(int fileId)
        {
            var have = IconStore.TryGet(fileId);
            if (have != null) { icon = have; return; }
            var disp = Application.Current?.Dispatcher;
            IconStore.Request(fileId, img => disp?.BeginInvoke(new Action(() => Icon = img)));
        }

        public static ItemChipVM From(ItemRef r)
        {
            var info = r.Id > 0 ? ItemKnowledge.Get(r.Id) : null;
            ClientItem ci = null;
            if (r.Id > 0) { try { ci = ItemData.Get(r.Id); } catch (Exception) { } }
            return new ItemChipVM(r, info, ci);
        }
    }
}
