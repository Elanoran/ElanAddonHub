using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ElansAddonHub.Lodge
{
    // The Lodge tab: the message list's test hooks (the "chatlist" self-test pins how the list behaves).
    public partial class LodgeView
    {
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
