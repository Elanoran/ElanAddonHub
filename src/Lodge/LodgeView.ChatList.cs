using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ElansAddonHub.Lodge
{
    // The rows of the message list are a window over the channel's messages: a channel opens with its newest OpenRows built, more are built
    // (Chunk at a time) as you scroll towards either edge of the window, and rows far from the view are let go once there are more than MaxRows.
    // Everything in the window is a real row (no virtualising panel), so what the list does - and how it looks - is what it always did; a
    // channel with thousands of messages simply never builds thousands of rows. The session's list of messages is not touched.
    // (Also here: the list keeps what you are reading in place when rows above it change size - a picture or a video card that arrives late,
    // an edit, a reaction - and stays at the end when you are at the end.)
    sealed class MessageWindow
    {
        public const int OpenRows = LodgeSession.WelcomeHistory + 20;   // a welcome page is always built whole (so paging works as it did)
        public const int Chunk = 20, MaxRows = 320, Before = 40, After = 80;

        // the rows on screen; Reset when the window moves as a whole (a channel opens, a jump)
        public sealed class Rows : ObservableCollection<MessageVM>
        {
            public void ReplaceAll(IEnumerable<MessageVM> items)
            {
                CheckReentrancy();
                Items.Clear();
                foreach (var m in items) Items.Add(m);
                OnPropertyChanged(new PropertyChangedEventArgs("Count"));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }

        public readonly Rows Shown = new Rows();
        ObservableCollection<MessageVM> source = new ObservableCollection<MessageVM>();
        public int Start { get; private set; }                   // source index of Shown[0]
        public int End => Start + Shown.Count;
        public int Total => source.Count;
        public bool Live => End >= source.Count;                  // the newest message is in the window (new ones are added to it)
        public int Version { get; private set; }                 // changes when the window moves as a whole

        // a channel opens: its newest messages, or (with a "New" line far up) from a little above the line
        public void Attach(ObservableCollection<MessageVM> list, MessageVM focus)
        {
            if (!ReferenceEquals(list, source))
            {
                source.CollectionChanged -= SourceChanged;
                source = list ?? new ObservableCollection<MessageVM>();
                source.CollectionChanged += SourceChanged;
            }
            int at = focus == null ? -1 : source.IndexOf(focus);
            if (at < 0 || at >= source.Count - OpenRows) ShowEnd();
            else Around(at);
        }

        public void ShowEnd() => Show(Math.Max(0, source.Count - OpenRows), source.Count);
        public int IndexOf(MessageVM m) => source.IndexOf(m);

        // makes sure `m` has a row (a jump): the window moves to it when it is outside
        public void Include(MessageVM m)
        {
            int at = source.IndexOf(m);
            if (at < 0 || (at >= Start && at < End)) return;
            Around(at);
        }

        void Around(int at)
        {
            int start = Math.Max(0, at - Before);
            int end = Math.Min(source.Count, Math.Max(start + OpenRows, at + After));
            if (source.Count - end < Chunk) end = source.Count;   // close to the end: take the rest too (the window is live again)
            Show(start, end);
        }

        void Show(int start, int end)
        {
            Version++;
            Start = start;
            Shown.ReplaceAll(source.Skip(start).Take(Math.Max(0, end - start)).ToList());
        }

        // n more rows above the window / below it
        public void GrowTop(int n)
        {
            n = Math.Min(n, Start);
            for (int i = 1; i <= n; i++) Shown.Insert(0, source[Start - i]);
            Start -= Math.Max(0, n);
        }
        public void GrowBottom(int n)
        {
            n = Math.Min(n, source.Count - End);
            int from = End;
            for (int i = 0; i < n; i++) Shown.Add(source[from + i]);
        }
        public void DropTop(int n) { n = Math.Min(n, Shown.Count); for (int i = 0; i < n; i++) Shown.RemoveAt(0); Start += n; }
        public void DropBottom(int n) { n = Math.Min(n, Shown.Count); for (int i = 0; i < n; i++) Shown.RemoveAt(Shown.Count - 1); }

        // the channel's own list changed: the window follows (new messages join a live window; an older page joins a window at the start)
        void SourceChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                {
                    int i = e.NewStartingIndex, k = e.NewItems.Count;
                    int end = End, oldCount = source.Count - k;
                    if (i < 0) { Resync(); return; }
                    if (i < Start || (i == Start && Start > 0)) { Start += k; return; }
                    if (i < end || (i == end && end >= oldCount)) for (int j = 0; j < k; j++) Shown.Insert(i - Start + j, (MessageVM)e.NewItems[j]);
                    return;
                }
                case NotifyCollectionChangedAction.Remove:
                {
                    int i = e.OldStartingIndex, k = e.OldItems.Count;
                    if (i < 0) { Resync(); return; }
                    if (i < Start) { Start = Math.Max(0, Start - k); return; }
                    for (int j = 0; j < k && i - Start < Shown.Count; j++) Shown.RemoveAt(i - Start);
                    return;
                }
                case NotifyCollectionChangedAction.Replace:
                {
                    int i = e.NewStartingIndex;
                    if (i < 0) { Resync(); return; }
                    for (int j = 0; j < e.NewItems.Count; j++) if (i + j >= Start && i + j < End) Shown[i + j - Start] = (MessageVM)e.NewItems[j];
                    return;
                }
                default: Resync(); return;   // Move / Reset: rebuilt around the same first row (or at the end)
            }
        }

        void Resync()
        {
            int first = Shown.Count > 0 ? source.IndexOf(Shown[0]) : -1;
            bool live = Shown.Count == 0 || first < 0 || first + Shown.Count >= source.Count;
            if (live) { Show(Math.Max(0, Math.Min(first < 0 ? source.Count : first, source.Count - OpenRows)), source.Count); return; }
            Show(first, Math.Min(source.Count, first + Math.Max(Shown.Count, OpenRows)));
        }
    }

    public partial class LodgeView
    {
        readonly MessageWindow window = new MessageWindow();
        bool adjusting;                 // the list itself is moving rows / the offset: its own ScrollChanged is not a reader's scroll
        MessageVM holdVm;               // what the reader is looking at (the first row on screen) and where its top is
        double holdY;
        bool holdBottom;                // ... and whether the list was at the end
        int holdVersion = -1;

        double RevealMargin => Math.Max(600, MessageScroll.ViewportHeight * 2);

        // first thing on every ScrollChanged: rows changed size (a picture or a video card arrived, an edit, a reaction, a row above was
        // removed) while nobody scrolled. At the end the list stays at the end; otherwise the row being read stays where it was.
        void KeepReaderInPlace(ScrollChangedEventArgs e)
        {
            if (adjusting || e.ExtentHeightChange == 0 || e.VerticalChange != 0 || holdVersion != window.Version) return;
            if (holdBottom && window.Live)
            {
                if (MessageScroll.VerticalOffset < MessageScroll.ScrollableHeight - 0.5) MessageScroll.ScrollToVerticalOffset(MessageScroll.ScrollableHeight);
            }
            else if (holdVm != null)
            {
                var c = ContainerOf(holdVm);
                var y = c == null ? double.NaN : OffsetInView(c);
                if (!double.IsNaN(y) && Math.Abs(y - holdY) > 0.5) MessageScroll.ScrollToVerticalOffset(Math.Max(0, MessageScroll.VerticalOffset + y - holdY));
            }
        }

        // last thing on every ScrollChanged (and after the list moved rows itself): remember what is being read
        void NoteReaderPlace()
        {
            if (adjusting) return;
            holdVm = FirstVisible(out holdY);
            holdBottom = AtBottom;
            holdVersion = window.Version;
        }

        // the window grows towards the edge the reader scrolls to (and lets go of rows far behind)
        void MaybeGrowWindow(ScrollChangedEventArgs e)
        {
            if (adjusting || Session?.Selected == null) return;
            if (e.VerticalChange < 0 && window.Start > 0 && MessageScroll.VerticalOffset <= RevealMargin) RevealOlder();
            else if (e.VerticalChange > 0 && !window.Live && MessageScroll.ScrollableHeight - MessageScroll.VerticalOffset <= RevealMargin) RevealNewer();
        }

        // rows above the window: built and laid out at once, the view shifted by exactly their height (nothing on screen moves)
        void RevealOlder()
        {
            if (window.Start == 0) return;
            HoldWhile(() => window.GrowTop(MessageWindow.Chunk));
            TrimFar();
        }

        // rows below a window that does not reach the newest message (after a jump, or after scrolling far up)
        void RevealNewer()
        {
            if (window.Live) return;
            HoldWhile(() => window.GrowBottom(MessageWindow.Chunk));
            TrimFar();
        }

        // more than MaxRows: let go of the rows far above / below the view (at the end, the list stays at the end)
        bool trimQueued;
        void QueueTrim()
        {
            if (trimQueued || window.Shown.Count <= MessageWindow.MaxRows + MessageWindow.Chunk) return;
            trimQueued = true;
            Dispatcher.BeginInvoke(new Action(() => { trimQueued = false; TrimFar(); }), System.Windows.Threading.DispatcherPriority.Background);
        }

        void TrimFar()
        {
            int extra = window.Shown.Count - MessageWindow.MaxRows;
            if (extra <= 0 || adjusting) { NoteReaderPlace(); return; }
            bool atEnd = AtBottom;
            double margin = RevealMargin;
            int n = 0;
            for (int i = 0; i < window.Shown.Count && n < extra; i++)
            {
                var c = Messages.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                if (c == null || OffsetInView(c) + c.ActualHeight > -margin) break;
                n++;
            }
            if (n > 0)
            {
                int drop = n;
                if (atEnd)
                {
                    adjusting = true;
                    try { window.DropTop(drop); MessageScroll.UpdateLayout(); MessageScroll.ScrollToVerticalOffset(MessageScroll.ScrollableHeight); MessageScroll.UpdateLayout(); }
                    finally { adjusting = false; }
                }
                else HoldWhile(() => window.DropTop(drop));
                extra -= n;
            }
            if (extra > 0 && !atEnd)
            {
                int m = 0;
                double below = MessageScroll.ViewportHeight + margin;
                for (int i = window.Shown.Count - 1; i >= 0 && m < extra; i--)
                {
                    var c = Messages.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                    if (c == null || OffsetInView(c) < below) break;
                    m++;
                }
                if (m > 0) HoldWhile(() => window.DropBottom(m));
            }
            NoteReaderPlace();
        }

        // runs `change` (rows added / removed), lays the list out at once (before anything is drawn) and moves the offset so the first row
        // on screen stays exactly where it was
        void HoldWhile(Action change)
        {
            var a = FirstVisible(out var ay);
            adjusting = true;
            try
            {
                change();
                MessageScroll.UpdateLayout();
                var c = a == null ? null : ContainerOf(a);
                var ny = c == null ? double.NaN : OffsetInView(c);
                if (!double.IsNaN(ny) && Math.Abs(ny - ay) > 0.01)
                {
                    MessageScroll.ScrollToVerticalOffset(Math.Max(0, MessageScroll.VerticalOffset + (ny - ay)));
                    MessageScroll.UpdateLayout();
                }
            }
            finally { adjusting = false; }
        }

        // a wheel turn at the very top / bottom of the window scrolls nothing (no ScrollChanged): grow the window here
        bool GrowAtEdge(MouseWheelEventArgs e)
        {
            if (e.Delta > 0 && window.Start > 0 && MessageScroll.VerticalOffset <= 0.5) { RevealOlder(); return true; }
            if (e.Delta < 0 && !window.Live && MessageScroll.VerticalOffset >= MessageScroll.ScrollableHeight - 0.5) { RevealNewer(); return true; }
            return false;
        }

        // ---- test hooks
        public ItemsControl MessagesForTest => Messages;
        public int RowCountForTest => Messages.Items.Count;          // message rows that are built (have a container)
        public bool AtBottomForTest => AtBottom;
        public double ViewportForTest => MessageScroll.ViewportHeight;
        public void ClickJumpForTest() => Jump_Click(null, null);
        public bool MenuOpenForTest => MsgMenu.IsOpen;
        public bool MenuCopyVisibleForTest => MenuCopy.Visibility == Visibility.Visible;
        public string ModeTextForTest => ModeBar.Visibility == Visibility.Visible ? ModeText.Text : "";
        public string TypingTextForTest => TypingText.Text;
        MessageVM MessageForTest(string id) => Session?.MessagesOf(Session.Selected?.Id).FirstOrDefault(x => x.Id == id);
        // a message's row (null when it has none)
        public FrameworkElement RowForTest(string id) { var m = MessageForTest(id); return m == null ? null : ContainerOf(m); }
        // a key that reaches the list itself (focus on the list, not in a message's text)
        public void ListKeyForTest(Key key)
        {
            var src = PresentationSource.FromVisual(MessageScroll);
            if (src == null) return;
            MessageScroll.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            MessageScroll.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        }
        public void ComposerKeyForTest(Key key)
        {
            var src = PresentationSource.FromVisual(Composer);
            if (src == null) return;
            Composer_PreviewKeyDown(Composer, new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
        // a right click on a message's row (the row's own handler, as the mouse would)
        public void RightClickRowForTest(string id)
        {
            var row = RowForTest(id);
            var border = row == null ? null : FindNamed(row, "row");
            if (border != null) Message_RightClick(border, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
        }
        static FrameworkElement FindNamed(DependencyObject d, string name)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                if (c is FrameworkElement f && f.Name == name) return f;
                var r = FindNamed(c, name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
