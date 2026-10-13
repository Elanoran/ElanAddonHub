using System;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // An item chip under a chat message: the icon (from the player's own game files via IconStore, when the Bags data knows the item),
    // the name in its quality colour, a tooltip, and a click that opens the item on Wowhead Classic.
    public class ItemChipVM : Bindable
    {
        public ItemRef Ref { get; }
        public ItemInfo Info { get; }
        public string Name { get; }
        public int Quality { get; }
        public bool Known => Ref.Id > 0 && Info != null;
        public string Url => ItemLinks.UrlFor(Ref);

        ImageSource icon;
        public ImageSource Icon { get => icon; private set { if (Set(ref icon, value)) { Raise(nameof(IconVisibility)); Raise(nameof(TileVisibility)); } } }
        public Visibility IconVisibility => icon != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TileVisibility => icon != null ? Visibility.Collapsed : Visibility.Visible;

        public Brush NameBrush => Quality >= 0 && Quality < ItemLinks.QualityHex.Length ? Avatar.Frozen(ItemLinks.QualityHex[Quality]) : Avatar.Res("Brush.Text.Secondary");

        public string TipTitle => Name;
        public string TipLine => (Quality >= 0 && Quality < ItemLinks.QualityNames.Length ? ItemLinks.QualityNames[Quality] + " · " : "") + (Ref.Id > 0 ? "Item #" + Ref.Id : "Item name not known here");
        public string TipHint => Ref.Id > 0 ? "Click to open on Wowhead Classic · Ctrl+click for Atlas" : "Click to search Wowhead Classic";

        public ItemChipVM(ItemRef r, ItemInfo info)
        {
            Ref = r; Info = info;
            Name = info?.Name ?? r.Name ?? (r.Id > 0 ? "Item #" + r.Id : "Item");
            Quality = r.Quality >= 0 ? r.Quality : info != null ? info.Quality : -1;
            if (info != null && info.Icon > 0) LoadIcon(info.Icon);
        }

        // from disk when it is already extracted, otherwise requested once (read-only from the local game files) and filled in when it arrives
        void LoadIcon(int fileId)
        {
            var have = IconStore.TryGet(fileId);
            if (have != null) { icon = have; return; }
            var disp = Application.Current?.Dispatcher;
            IconStore.Request(fileId, img => disp?.BeginInvoke(new Action(() => Icon = img)));
        }

        public static ItemChipVM From(ItemRef r) => new ItemChipVM(r, r.Id > 0 ? ItemKnowledge.Get(r.Id) : null);
    }
}
