using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ElansAddonHub.Lodge
{
    // The Lodge tab, part 3: older messages (loaded when you scroll to the top, or when a jump needs them) and the search panel.
    public partial class LodgeView
    {
        // where the list was when a page was asked for: the page lands above it and the list is shifted back so nothing seems to move
        MessageVM anchorVm;
        double anchorY;

        // ---- scrolling to the top loads older messages
        public bool AutoLoadOlder = true;   // (the self-test measures the anchoring with it off)

        void MessageScroll_Wheel(object sender, MouseWheelEventArgs e)
        {
            if (GrowAtEdge(e)) return;   // rows the channel already has come first (LodgeView.ChatList.cs)
            if (!AutoLoadOlder) return;
            // at the very top the wheel no longer scrolls, so no ScrollChanged: ask for more here
            if (e.Delta > 0 && MessageScroll.VerticalOffset <= 0.5) _ = LoadOlderFromTop();
        }

        void MaybeLoadOlder(ScrollChangedEventArgs e)
        {
            if (AutoLoadOlder && window.Start == 0 && e.VerticalChange < 0 && MessageScroll.VerticalOffset <= 60) _ = LoadOlderFromTop();
        }

        async Task LoadOlderFromTop()
        {
            var ch = Session?.Selected;
            if (ch == null || !Session.CanLoadOlder(ch.Id)) return;
            UpdateOlderNote(true);
            await Session.LoadOlder(ch.Id);
            UpdateOlderNote();
        }

        // the first message that is (partly) on screen and where its top is: found by binary search over the realized rows
        MessageVM FirstVisible(out double y)
        {
            y = 0;
            var list = window.Shown;   // the rows that are built
            if (list.Count == 0) return null;
            int lo = 0, hi = list.Count - 1, found = -1;
            double foundY = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var c = Messages.ItemContainerGenerator.ContainerFromIndex(mid) as FrameworkElement;
                double top = c == null ? double.NaN : OffsetInView(c);
                if (double.IsNaN(top)) return null;
                if (top + c.ActualHeight > 0) { found = mid; foundY = top; hi = mid - 1; } else lo = mid + 1;
            }
            if (found < 0) return null;
            y = foundY;
            return list[found];
        }

        void OnOlderRequested(string channel)
        {
            if (channel != Session?.Selected?.Id) { anchorVm = null; return; }
            anchorVm = FirstVisible(out anchorY);
            UpdateOlderNote(true);
        }

        void OnOlderLoaded(string channel, List<MessageVM> added, bool more)
        {
            try
            {
                if (channel == Session?.Selected?.Id && added.Count > 0 && anchorVm != null)
                {
                    // the new rows are above everything: lay out now (before anything is drawn) and move the view by exactly what was added above the anchor
                    MessageScroll.UpdateLayout();
                    var c = ContainerOf(anchorVm);
                    var ny = c == null ? double.NaN : OffsetInView(c);
                    if (!double.IsNaN(ny))
                    {
                        MessageScroll.ScrollToVerticalOffset(Math.Max(0, MessageScroll.VerticalOffset + (ny - anchorY)));
                        MessageScroll.UpdateLayout();
                    }
                }
            }
            finally { anchorVm = null; UpdateOlderNote(); NoteReaderPlace(); TrimFar(); }
        }

        // the quiet line at the top of the list: loading / scroll up for more / the start of the channel
        void UpdateOlderNote(bool loading = false)
        {
            var s = Session;
            var ch = s?.Selected;
            if (s == null || ch == null || !s.SupportsPaging || s.MessagesOf(ch.Id).Count == 0) { OlderNote.Visibility = Visibility.Collapsed; return; }
            OlderNote.Visibility = Visibility.Visible;
            OlderNote.Text = loading || s.IsLoadingOlder(ch.Id) ? "Loading earlier messages..." : s.HasMoreOlder(ch.Id) ? "Scroll up for earlier messages" : "This is the start of #" + ch.Name;
        }

        // scrolls to a message of `channel` and flashes it. A message that is not loaded is looked for in older pages (the page that lands is anchored,
        // so the view stays still while it loads); true when it was found. `atMs` (a search hit's or a pin's time) lets the search stop early.
        public async Task<bool> JumpToAsync(string channel, string id, long atMs = 0)
        {
            if (Session == null || channel == null || id == null) return false;
            if (Session.Selected?.Id != channel)
            {
                var ch = Session.TextChannels.FirstOrDefault(c => c.Id == channel);
                if (ch == null) return false;
                Session.Select(ch);
            }
            var list = Session.MessagesOf(channel);
            if (list.All(m => m.Id != id) && Session.HasMoreOlder(channel))
            {
                SetNote("Loading older messages..."); noteUntil = DateTime.UtcNow.AddSeconds(30);
                await Session.LoadUntil(channel, id, atMs);
                if (ChatNote.Text == "Loading older messages...") ChatNote.Text = "";
            }
            var target = list.FirstOrDefault(m => m.Id == id);
            if (target != null && Session.Selected?.Id == channel) { ScrollToMessage(target, true); return true; }
            if (Session.Selected?.Id == channel)
            {
                SetNote(Session.SupportsPaging ? "That message is no longer in the history" : "That message is older than what is loaded here");
                noteUntil = DateTime.UtcNow.AddSeconds(4);
            }
            return false;
        }

        // ---- search
        void View_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && ChatPanel.Visibility == Visibility.Visible && Session != null && Session.SupportsSearch)
            {
                OpenSearch();
                e.Handled = true;
            }
        }

        void SearchButton_Click(object sender, RoutedEventArgs e) => OpenSearch();

        public void OpenSearch()
        {
            if (Session == null || !Session.SupportsSearch) return;
            if (SearchHint.Text.Length == 0) SearchHint.Text = "Press Enter to search";
            SearchPopup.PlacementTarget = SearchButton;
            SearchPopup.HorizontalOffset = SearchButton.ActualWidth - SearchBoxCard.Width - 8 - PopupShadow;
            SearchPopup.IsOpen = true;
        }

        void SearchPopup_Opened(object sender, EventArgs e)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { _ = RunSearch(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SearchPopup.IsOpen = false; e.Handled = true; }
        }

        // one search per Enter (the server allows a few a minute, so nothing is searched while typing)
        public async Task RunSearch()
        {
            var q = SearchBox.Text.Trim();
            if (q.Length < 2) { SearchHint.Text = "Type at least 2 characters"; return; }
            SearchHint.Text = "Searching...";
            var res = await Session.SearchAsync(q);
            if (SearchBox.Text.Trim() != q) return;   // typed on meanwhile
            if (res.Rate) { SearchHint.Text = "You are searching a lot - try again in a few seconds"; return; }
            if (res.Failed) { SearchHint.Text = "The lodge didn't answer - try again in a moment"; return; }
            SearchList.ItemsSource = res.Groups;
            SearchHint.Text = res.Hits.Count == 0 ? "Nothing found for \"" + q + "\""
                : res.Hits.Count + (res.Hits.Count == 1 ? " result" : " results") + (res.More ? " - the newest are shown, narrow the search to see others" : "");
        }

        async void SearchHit_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is SearchHitVM hit)) return;
            e.Handled = true;
            SearchPopup.IsOpen = false;
            await JumpToAsync(hit.ChannelId, hit.Id, hit.AtMs);
        }

        // ---- test hooks
        public bool SearchButtonVisibleForTest => SearchButton.Visibility == Visibility.Visible;
        public bool SearchOpenForTest => SearchPopup.IsOpen;
        public string SearchBoxTextForTest { get => SearchBox.Text; set => SearchBox.Text = value; }
        public int SearchBoxMaxLengthForTest => SearchBox.MaxLength;
        public string SearchHintForTest => SearchHint.Text;
        public List<SearchGroupVM> SearchGroupsForTest => SearchList.ItemsSource as List<SearchGroupVM> ?? new List<SearchGroupVM>();
        public FrameworkElement SearchCardForTest => SearchBoxCard;
        public Task RunSearchForTest() => RunSearch();
        public Task<bool> ClickHitForTest(SearchHitVM hit) => JumpToAsync(hit.ChannelId, hit.Id, hit.AtMs);
        public void CloseSearchForTest() => SearchPopup.IsOpen = false;
        public string OlderNoteForTest => OlderNote.Visibility == Visibility.Visible ? OlderNote.Text : "";
        public Task LoadOlderForTest() => LoadOlderFromTop();
        public void ScrollToVerticalForTest(double y) { MessageScroll.UpdateLayout(); MessageScroll.ScrollToVerticalOffset(y); MessageScroll.UpdateLayout(); }
        public double ScrollOffsetForTest => MessageScroll.VerticalOffset;
        public string FirstVisibleForTest(out double y) { var m = FirstVisible(out y); return m?.Id; }
        public string ChatNoteTextForTest => ChatNote.Text;
        // the top-left corner the search card has (relative to `window`) while it is open under the magnifier
        public Point SearchCardOriginForTest(Window window)
        {
            var p = SearchButton.TransformToAncestor(window).Transform(new Point(0, 0));
            return new Point(p.X + SearchButton.ActualWidth - SearchBoxCard.Width - 8, p.Y + SearchButton.ActualHeight + 6);
        }
        // where a message's row starts in the view (NaN when it has no row)
        public double OffsetOfForTest(string id) { var m = Session.MessagesOf(Session.Selected?.Id).FirstOrDefault(x => x.Id == id); var c = m == null ? null : ContainerOf(m); return c == null ? double.NaN : OffsetInView(c); }
        public void FireWheelUpForTest() => MessageScroll_Wheel(MessageScroll, new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120));
    }
}
