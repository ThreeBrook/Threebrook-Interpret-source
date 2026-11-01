using System;
using System.ComponentModel;
using System.Linq;
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
    private readonly TranslationHistory? _translations;
    private readonly ICollectionView? _transView;
    private IntPtr _previousWindow;

    /// <param name="translations">翻译历史（"翻译"页）；界面预览不传时只有剪贴板页。</param>
    public HistoryWindow(ClipboardHistory history, TranslationHistory? translations = null)
    {
        InitializeComponent();
        _history = history;
        _view = CollectionViewSource.GetDefaultView(history.Entries);
        _view.Filter = Filter;
        List.ItemsSource = _view;
        history.Entries.CollectionChanged += (_, _) => UpdateCount();
        _translations = translations;
        if (translations != null)
        {
            _transView = CollectionViewSource.GetDefaultView(translations.Entries);
            _transView.Filter = item => MatchesSearch((TranslationRecord)item);
            TransList.ItemsSource = _transView;
            translations.Entries.CollectionChanged += (_, _) => UpdateCount();
        }
        else
        {
            TabTranslation.Visibility = Visibility.Collapsed;
        }
        UpdateCount();
    }

    /// <summary>Alt+H：已在前台则隐藏，否则在鼠标旁显示（停在上次看的那一页）。</summary>
    public void Toggle()
    {
        if (IsVisible && IsActive)
        {
            Hide();
            return;
        }
        ShowNearCursor();
    }

    /// <summary>翻译窗口的历史按钮、托盘菜单"翻译历史"：打开"翻译"页。</summary>
    public void ShowTranslations()
    {
        TabTranslation.IsChecked = true;
        ShowNearCursor();
    }

    private void ShowNearCursor()
    {
        _previousWindow = PasteTarget();
        _view.Refresh(); // 刷新相对时间
        _transView?.Refresh();
        Show();
        WindowPlacement.MoveNearCursor(this);
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        SelectFirst();
    }

    private bool OnTranslationTab => TabTranslation.IsChecked == true;

    private ListBox ActiveList => OnTranslationTab ? TransList : List;

    private bool Filter(object item)
    {
        var entry = (HistoryEntry)item;
        if (KindText?.IsChecked == true && entry.IsImage || KindImage?.IsChecked == true && !entry.IsImage)
            return false;
        var keyword = SearchBox.Text.Trim();
        return keyword.Length == 0 || !entry.IsImage && entry.Text!.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesSearch(TranslationRecord record)
    {
        var keyword = SearchBox.Text.Trim();
        return keyword.Length == 0
               || record.Source.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || record.Results.Any(r => r.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateCount()
    {
        if (CountText == null)
            return; // InitializeComponent 期间
        CountText.Text = (OnTranslationTab ? _translations?.Entries.Count ?? 0 : _history.Entries.Count).ToString();
        EmptyText.Visibility = ActiveList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 空状态随当前页；翻译历史关闭时提示去设置里打开
        EmptyIcon.SetResourceReference(TextBlock.TextProperty, OnTranslationTab ? "I.Translate" : "I.Paste");
        EmptyHintText.SetResourceReference(TextBlock.TextProperty, !OnTranslationTab ? "S.History.EmptyHint"
            : App.Settings.TranslationHistory.Enabled ? "S.TransHistory.EmptyHint" : "S.TransHistory.Disabled");
    }

    private void SelectFirst()
    {
        var list = ActiveList;
        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
            list.ScrollIntoView(list.SelectedItem);
        }
        UpdateCount();
    }

    /// <summary>切换 剪贴板 / 翻译 页：换列表、筛选按钮、搜索提示和底部提示。</summary>
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (TransList == null)
            return; // InitializeComponent 期间
        var translation = OnTranslationTab;
        List.Visibility = translation ? Visibility.Collapsed : Visibility.Visible;
        TransList.Visibility = translation ? Visibility.Visible : Visibility.Collapsed;
        KindChips.Visibility = translation ? Visibility.Collapsed : Visibility.Visible;
        ClipboardHint.Visibility = translation ? Visibility.Collapsed : Visibility.Visible;
        TranslationHint.Visibility = translation ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.SetResourceReference(TextBlock.TextProperty, translation ? "S.TransHistory.Search" : "S.History.Search");
        (translation ? _transView : _view)?.Refresh();
        SelectFirst();
        SearchBox.Focus();
    }

    // —— 翻译历史 ——

    private TranslationRecord? SelectedRecord => TransList.SelectedItem as TranslationRecord;

    private static TranslationRecord? RecordOf(object sender) => (sender as FrameworkElement)?.DataContext as TranslationRecord;

    /// <summary>打开一条翻译记录：原文和当时的译文放回翻译窗口（不重新翻译）。</summary>
    private void OpenRecord(TranslationRecord? record)
    {
        if (record == null)
            return;
        Hide();
        if (OpenRecordOverride != null)
            OpenRecordOverride(record);
        else
            App.Instance.OpenTranslationRecord(record);
    }

    /// <summary>界面预览 / 自测：打开的记录交给它，不经过 App 的翻译窗口。</summary>
    internal Action<TranslationRecord>? OpenRecordOverride { get; init; }

    private void CopyRecord(TranslationRecord? record)
    {
        if (record == null || record.Preview.Length == 0)
            return;
        ImageActions.CopyText(record.Preview);
        Hide();
    }

    private void DeleteRecord(TranslationRecord? record)
    {
        if (record == null || _translations == null)
            return;
        var index = TransList.SelectedIndex;
        _translations.Delete(record);
        if (TransList.Items.Count > 0)
            TransList.SelectedIndex = Math.Min(index, TransList.Items.Count - 1);
    }

    private void OpenRecord_Click(object sender, RoutedEventArgs e) => OpenRecord(RecordOf(sender));
    private void CopyRecord_Click(object sender, RoutedEventArgs e) => CopyRecord(RecordOf(sender));
    private void DeleteRecord_Click(object sender, RoutedEventArgs e) => DeleteRecord(RecordOf(sender));

    private void RecordItem_Click(object sender, MouseButtonEventArgs e) =>
        OpenRecord(((ListBoxItem)sender).DataContext as TranslationRecord);

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

    /// <summary>清空当前这一页。</summary>
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var translation = OnTranslationTab;
        Topmost = false;
        var confirm = MessageBox.Show(this, Loc.T(translation ? "TransHistory.ConfirmClear" : "History.ConfirmClear"), AppInfo.Name,
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        Topmost = true;
        if (confirm != MessageBoxResult.OK)
            return;
        if (translation)
            _translations?.Clear();
        else
            _history.Clear();
    }

    // —— 键盘与窗口 ——

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var inSearch = SearchBox.IsKeyboardFocused;
        var list = ActiveList;

        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                break;
            case Key.Enter when OnTranslationTab:
                OpenRecord(SelectedRecord);
                break;
            case Key.Enter:
                Paste(Selected);
                break;
            case Key.Down or Key.Up when inSearch && list.Items.Count > 0:
                list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, list.Items.Count - 1);
                list.ScrollIntoView(list.SelectedItem);
                break;
            case Key.Delete when !inSearch || SearchBox.Text.Length == 0:
                if (OnTranslationTab)
                    DeleteRecord(SelectedRecord);
                else
                    Delete(Selected);
                break;
            case Key.C when ctrl && (!inSearch || SearchBox.SelectionLength == 0):
                if (OnTranslationTab)
                    CopyRecord(SelectedRecord);
                else
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
        _transView?.Refresh();
        SelectFirst();
    }

    // —— 自测（不动真实鼠标键盘） ——

    /// <summary>照真实按键的路由发一次按键（先 PreviewKeyDown 再 KeyDown）。</summary>
    internal void SimulateKey(Key key)
    {
        var target = Keyboard.FocusedElement as UIElement ?? this;
        var source = PresentationSource.FromVisual(this)!;
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled)
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    /// <summary>在翻译页第 index 条上发一次左键抬起（单击打开走的就是这个事件）。</summary>
    internal void SimulateRecordClick(int index)
    {
        TransList.UpdateLayout();
        if (TransList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonUpEvent });
    }

    /// <summary>预览：打开翻译页（不移动、不激活窗口）。</summary>
    internal void ShowTranslationTabPreview() => TabTranslation.IsChecked = true;

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
