using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ThreebrookInterpret.Services;
using ThreebrookInterpret.Translation;

namespace ThreebrookInterpret.Views;

public partial class TranslateWindow : Window
{
    private readonly ObservableCollection<ResultItem> _results = new();
    private readonly TranslationHistory? _history;
    private readonly ICollectionView? _historyView;
    private CancellationTokenSource? _cts;
    private bool _loadingTargets;
    private bool _confirming;

    /// <param name="history">翻译历史；界面预览不传，翻译时就不记录。</param>
    public TranslateWindow(TranslationHistory? history = null)
    {
        InitializeComponent();
        ResultsList.ItemsSource = _results;
        _history = history;
        if (history != null)
        {
            _historyView = CollectionViewSource.GetDefaultView(history.Entries);
            _historyView.Filter = item => MatchesSearch((TranslationRecord)item);
            HistoryList.ItemsSource = _historyView;
            history.Entries.CollectionChanged += (_, _) => UpdateHistoryEmpty();
        }
        else
        {
            HistoryButton.Visibility = Visibility.Collapsed;
        }
        LoadTargets();
    }

    /// <summary>重新加载配置后刷新目标语言下拉框。</summary>
    public void LoadTargets()
    {
        _loadingTargets = true;
        TargetBox.ItemsSource = new[] { Languages.AutoOption }.Concat(Languages.All).ToList();
        TargetBox.SelectedValue = App.Settings.TargetLanguage;
        if (TargetBox.SelectedIndex < 0)
            TargetBox.SelectedIndex = 0;
        _loadingTargets = false;
    }

    /// <summary>快捷键呼出：已在前台则隐藏，否则显示在鼠标所在屏幕的上方居中。</summary>
    public void Toggle()
    {
        if (IsVisible && IsActive)
        {
            Hide();
            return;
        }
        ShowAndFocus();
    }

    public void ShowAndFocus()
    {
        HistoryButton.IsChecked = false;
        var wasVisible = IsVisible;
        Show();
        if (!wasVisible)
            WindowPlacement.CenterOnCursorScreen(this);
        Activate();
        InputBox.Focus();
        InputBox.SelectAll();
    }

    /// <summary>
    /// 在鼠标旁显示文字（划词、截图识别、历史记录）。
    /// translate 为 false 时只显示原文，按 Enter 再翻译；text 为空表示没取到文字。
    /// </summary>
    public void ShowAtCursor(string? text, bool translate = true, string? emptyHint = null)
    {
        HistoryButton.IsChecked = false;
        Show();
        WindowPlacement.MoveNearCursor(this);
        Activate();

        _cts?.Cancel();
        _results.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            InputBox.Clear();
            SetDirection(emptyHint ?? Loc.T("Translate.NoSelection"));
        }
        else
        {
            InputBox.Text = text;
            if (translate)
                StartTranslate();
            else
                SetDirection(Loc.T("Translate.Recognized"));
        }
        InputBox.Focus();
        InputBox.SelectAll();
    }

    /// <summary>高精度识别要零点几秒到几秒：先在鼠标旁显示"正在识别"，识别完再由 ShowAtCursor 填入文字。</summary>
    public void ShowRecognizing()
    {
        HistoryButton.IsChecked = false;
        Show();
        WindowPlacement.MoveNearCursor(this);
        _cts?.Cancel();
        _results.Clear();
        InputBox.Clear();
        SetDirection(Loc.T("Translate.Recognizing"));
    }

    private void StartTranslate()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0)
            return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var to = Languages.ResolveTarget(text, App.Settings.TargetLanguage);
        SetDirection("→ " + Languages.Get(to).DisplayName);
        _results.Clear();

        var runs = new List<(ResultItem Item, Task Task)>();
        foreach (var engine in App.Settings.Engines.Where(e => e.Enabled))
        {
            var translator = TranslatorFactory.Create(engine) is { } created ? Glossary.Wrap(created, App.Settings) : null;
            var item = new ResultItem(engine.Name);
            _results.Add(item);
            if (translator == null)
                item.Fail(Loc.F("Translate.UnknownEngine", engine.Type));
            else
                runs.Add((item, RunAsync(item, translator, text, to, ct)));
        }
        if (_history != null && runs.Count > 0)
            _ = RecordAsync(text, to, runs, ct);

        if (_results.Count == 0)
        {
            var item = new ResultItem(Loc.T("Translate.Notice"));
            item.Fail(Loc.T("Translate.NoEngines"));
            _results.Add(item);
        }
    }

    /// <summary>界面预览用：填入示例原文和结果，不发起翻译。</summary>
    internal void LoadPreview(string input, string direction, params ResultItem[] results)
    {
        InputBox.Text = input;
        SetDirection(direction);
        _results.Clear();
        foreach (var item in results)
            _results.Add(item);
    }

    private void SetDirection(string text)
    {
        DirectionText.Text = text;
        DirectionChip.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static async Task RunAsync(ResultItem item, ITranslator translator, string text, string to, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await translator.TranslateAsync(text, Languages.Auto, to, ct);
            if (!ct.IsCancellationRequested)
                item.Complete(result, sw.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                item.Fail(ex is TranslationException ? ex.Message : $"{ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>所有引擎都出结果（或失败）后记进翻译历史；中途又发起了新翻译就不记这次。</summary>
    private async Task RecordAsync(string text, string to, List<(ResultItem Item, Task Task)> runs, CancellationToken ct)
    {
        await Task.WhenAll(runs.Select(r => r.Task));
        if (!ct.IsCancellationRequested)
            _history!.Add(text, to, runs.Select(r => (r.Item.Name, r.Item.HasResult ? r.Item.Text : "")));
    }

    // —— 翻译历史 ——

    /// <summary>托盘菜单"翻译历史"：显示窗口并切到历史列表。</summary>
    public void ShowHistory()
    {
        if (_history == null)
            return;
        ShowAndFocus();
        HistoryButton.IsChecked = true;
    }

    /// <summary>界面预览：切到历史列表；open 为 true 时打开第一条。</summary>
    internal void ShowHistoryPreview(bool open)
    {
        HistoryButton.IsChecked = true;
        if (open)
            OpenRecord(HistoryList.Items.Count > 0 ? (TranslationRecord)HistoryList.Items[0] : null);
    }

    private void HistoryButton_Changed(object sender, RoutedEventArgs e)
    {
        var on = HistoryButton.IsChecked == true;
        HistoryPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TranslatePanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on)
        {
            _historyView?.Refresh(); // 刷新相对时间
            HistorySearch.Focus();
            HistorySearch.SelectAll();
            SelectFirstRecord();
        }
        else
        {
            InputBox.Focus();
        }
    }

    private bool MatchesSearch(TranslationRecord record)
    {
        var keyword = HistorySearch.Text.Trim();
        return keyword.Length == 0
               || record.Source.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || record.Results.Any(r => r.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateHistoryEmpty()
    {
        HistoryEmpty.Visibility = HistoryList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 关闭了记录时，空状态里提示去设置里打开
        HistoryEmptyHint.Text = Loc.T(App.Settings.TranslationHistory.Enabled ? "TransHistory.EmptyHint" : "TransHistory.Disabled");
    }

    private void SelectFirstRecord()
    {
        if (HistoryList.Items.Count > 0)
        {
            HistoryList.SelectedIndex = 0;
            HistoryList.ScrollIntoView(HistoryList.SelectedItem);
        }
        UpdateHistoryEmpty();
    }

    private TranslationRecord? SelectedRecord => HistoryList.SelectedItem as TranslationRecord;

    private static TranslationRecord? RecordOf(object sender) => (sender as FrameworkElement)?.DataContext as TranslationRecord;

    /// <summary>打开一条记录：原文和当时的译文放回翻译区，不重新翻译（按 Enter 才重新翻）。</summary>
    private void OpenRecord(TranslationRecord? record)
    {
        if (record == null)
            return;
        HistoryButton.IsChecked = false;
        _cts?.Cancel();
        InputBox.Text = record.Source;
        SetDirection("→ " + record.TargetName);
        _results.Clear();
        foreach (var result in record.Results)
            _results.Add(ResultItem.Restored(result.Engine, result.Text, record.Time));
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    private static void CopyRecord(TranslationRecord? record)
    {
        if (record == null || record.Preview.Length == 0)
            return;
        try
        {
            Clipboard.SetText(record.Preview);
        }
        catch (COMException)
        {
            // 剪贴板被其他程序占用，忽略
        }
    }

    private void DeleteRecord(TranslationRecord? record)
    {
        if (record == null || _history == null)
            return;
        var index = HistoryList.SelectedIndex;
        _history.Delete(record);
        if (HistoryList.Items.Count > 0)
            HistoryList.SelectedIndex = Math.Clamp(index, 0, HistoryList.Items.Count - 1);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_history == null || _history.Entries.Count == 0)
            return;
        // 确认框会让窗口失去焦点，这期间不要自动隐藏
        _confirming = true;
        Topmost = false;
        var confirm = MessageBox.Show(this, Loc.T("TransHistory.ConfirmClear"), AppInfo.Name, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        Topmost = true;
        _confirming = false;
        if (confirm == MessageBoxResult.OK)
            _history.Clear();
        Activate();
        HistorySearch.Focus();
    }

    private void OpenRecord_Click(object sender, RoutedEventArgs e) => OpenRecord(RecordOf(sender));
    private void CopyRecord_Click(object sender, RoutedEventArgs e) => CopyRecord(RecordOf(sender));
    private void DeleteRecord_Click(object sender, RoutedEventArgs e) => DeleteRecord(RecordOf(sender));

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(HistoryList, source) is ListBoxItem item)
            OpenRecord(item.DataContext as TranslationRecord);
    }

    private void HistorySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        HistoryPlaceholder.Visibility = HistorySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _historyView?.Refresh();
        SelectFirstRecord();
    }

    /// <summary>历史列表里的按键：上下选择、Enter 打开、Ctrl+C 复制译文、Delete 删除、Esc 回到翻译。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (HistoryButton.IsChecked != true)
            return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var inSearch = HistorySearch.IsKeyboardFocused;
        switch (e.Key)
        {
            case Key.Escape:
                HistoryButton.IsChecked = false;
                break;
            case Key.Enter:
                OpenRecord(SelectedRecord);
                break;
            case Key.Down or Key.Up when inSearch && HistoryList.Items.Count > 0:
                HistoryList.SelectedIndex = Math.Clamp(HistoryList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, HistoryList.Items.Count - 1);
                HistoryList.ScrollIntoView(HistoryList.SelectedItem);
                break;
            case Key.Delete when !inSearch || HistorySearch.Text.Length == 0:
                DeleteRecord(SelectedRecord);
                break;
            case Key.C when ctrl && (!inSearch || HistorySearch.SelectionLength == 0):
                CopyRecord(SelectedRecord);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            StartTranslate();
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) =>
        Placeholder.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void TargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTargets || TargetBox.SelectedValue is not string code)
            return;

        App.Settings.TargetLanguage = code;
        try
        {
            SettingsStore.Save(App.Settings);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
        }
        StartTranslate();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ResultItem item)
            return;
        try
        {
            Clipboard.SetText(item.Text);
        }
        catch (COMException)
        {
            // 剪贴板被其他程序占用，忽略
        }
    }



    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Hide();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (PinButton.IsChecked != true && !_confirming)
            Hide();
    }

    /// <summary>翻译结果出来后窗口变高，超出屏幕底部时往上移。</summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (IsVisible)
            WindowPlacement.ClampToWorkArea(this);
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
}
