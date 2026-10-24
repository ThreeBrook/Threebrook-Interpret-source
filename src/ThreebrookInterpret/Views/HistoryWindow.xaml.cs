using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Views;

public partial class HistoryWindow : Window
{
    private readonly ClipboardHistory _history;
    private readonly ICollectionView _view;
    private IntPtr _previousWindow;

    public HistoryWindow(ClipboardHistory history)
    {
        InitializeComponent();
        _history = history;
        _view = CollectionViewSource.GetDefaultView(history.Entries);
        _view.Filter = Filter;
        List.ItemsSource = _view;
        history.Entries.CollectionChanged += (_, _) => UpdateCount();
        UpdateCount();
    }

    public void Toggle()
    {
        if (IsVisible && IsActive)
        {
            Hide();
            return;
        }

        _previousWindow = PasteTarget();
        _view.Refresh(); // 刷新相对时间
        Show();
        WindowPlacement.MoveNearCursor(this);
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        SelectFirst();
    }

    private bool Filter(object item)
    {
        var entry = (HistoryEntry)item;
        if (KindText?.IsChecked == true && entry.IsImage || KindImage?.IsChecked == true && !entry.IsImage)
            return false;
        var keyword = SearchBox.Text.Trim();
        return keyword.Length == 0 || !entry.IsImage && entry.Text!.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateCount()
    {
        CountText.Text = _history.Entries.Count.ToString();
        EmptyText.Visibility = List.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectFirst()
    {
        if (List.Items.Count > 0)
        {
            List.SelectedIndex = 0;
            List.ScrollIntoView(List.SelectedItem);
        }
        UpdateCount();
    }

    private HistoryEntry? Selected => List.SelectedItem as HistoryEntry;

    private static HistoryEntry? EntryOf(object sender) => (sender as FrameworkElement)?.DataContext as HistoryEntry;

    // —— 操作 ——

    private async void Paste(HistoryEntry? entry)
    {
        if (entry == null)
            return;
        _history.CopyToClipboard(entry);
        Hide();

        if (!App.Settings.History.PasteOnSelect || _previousWindow == IntPtr.Zero)
            return;
        SetForegroundWindow(_previousWindow);
        await Task.Delay(80);
        await KeyboardInput.ReleaseModifiersAsync();
        KeyboardInput.SendCtrl(KeyboardInput.VK_V);
    }

    private void CopyOnly(HistoryEntry? entry)
    {
        if (entry == null)
            return;
        _history.CopyToClipboard(entry);
        Hide();
    }

    private void Delete(HistoryEntry? entry)
    {
        if (entry == null)
            return;
        var index = List.SelectedIndex;
        _history.Delete(entry);
        if (List.Items.Count > 0)
            List.SelectedIndex = Math.Min(index, List.Items.Count - 1);
    }

    private void Paste_Click(object sender, RoutedEventArgs e) => Paste(EntryOf(sender));
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyOnly(EntryOf(sender));
    private void Delete_Click(object sender, RoutedEventArgs e) => Delete(EntryOf(sender));

    private void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender)?.Text is { } text)
        {
            Hide();
            App.Instance.TranslateText(text);
        }
    }

    private void Ocr_Click(object sender, RoutedEventArgs e) => Recognize(EntryOf(sender), translate: false);
    private void OcrTranslate_Click(object sender, RoutedEventArgs e) => Recognize(EntryOf(sender), translate: true);

    private void Recognize(HistoryEntry? entry, bool translate)
    {
        if (entry != null && ClipboardHistory.LoadImage(entry) is { } image)
        {
            Hide();
            App.Instance.RecognizeImage(image, translate);
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry && ClipboardHistory.LoadImage(entry) is { } image)
        {
            Hide();
            WindowPlacement.GetCursorPosition(out var x, out var y);
            new PinWindow(image, x, y).Show();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry && ClipboardHistory.LoadImage(entry) is { } image)
        {
            Topmost = false;
            ImageActions.Save(image, this);
            Topmost = true;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Topmost = false;
        var confirm = MessageBox.Show(this, Loc.T("History.ConfirmClear"), AppInfo.Name,
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        Topmost = true;
        if (confirm == MessageBoxResult.OK)
            _history.Clear();
    }

    // —— 键盘与窗口 ——

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var inSearch = SearchBox.IsKeyboardFocused;

        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                break;
            case Key.Enter:
                Paste(Selected);
                break;
            case Key.Down or Key.Up when inSearch && List.Items.Count > 0:
                List.SelectedIndex = Math.Clamp(List.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, List.Items.Count - 1);
                List.ScrollIntoView(List.SelectedItem);
                break;
            case Key.Delete when !inSearch || SearchBox.Text.Length == 0:
                Delete(Selected);
                break;
            case Key.C when ctrl && (!inSearch || SearchBox.SelectionLength == 0):
                CopyOnly(Selected);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(List, source) is ListBoxItem item)
            Paste(item.DataContext as HistoryEntry);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        SelectFirst();
    }

    private void Kind_Checked(object sender, RoutedEventArgs e)
    {
        if (_view == null)
            return; // InitializeComponent 期间
        _view.Refresh();
        SelectFirst();
    }



    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (Topmost) // 弹出确认框 / 保存对话框时会临时取消置顶，此时不隐藏
            Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>
    /// 之前的前台窗口作为粘贴目标；从托盘打开时前台是任务栏 / 桌面，或者是本程序自己的窗口，
    /// 这些都不能粘贴，返回空（只复制不粘贴）。
    /// </summary>
    private static IntPtr PasteTarget()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return IntPtr.Zero;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId)
            return IntPtr.Zero;
        var className = new System.Text.StringBuilder(64);
        GetClassName(hwnd, className, className.Capacity);
        return className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "Progman" or "WorkerW"
            ? IntPtr.Zero
            : hwnd;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
