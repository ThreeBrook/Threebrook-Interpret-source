using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ThreebrookInterpret.Services;
using ThreebrookInterpret.Translation;

namespace ThreebrookInterpret.Views;

public partial class TranslateWindow : Window
{
    private readonly ObservableCollection<ResultItem> _results = new();
    private CancellationTokenSource? _cts;
    private bool _loadingTargets;

    public TranslateWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _results;
        LoadTargets();
    }

    /// <summary>重新加载配置后刷新目标语言下拉框。</summary>
    public void LoadTargets()
    {
        _loadingTargets = true;
        TargetBox.ItemsSource = new[] { new Language(Languages.Auto, "自动（中⇄英）", "") }.Concat(Languages.All).ToList();
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
        Show();
        WindowPlacement.MoveNearCursor(this);
        Activate();

        _cts?.Cancel();
        _results.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            InputBox.Clear();
            SetDirection(emptyHint ?? "未获取到选中的文字，可直接输入");
        }
        else
        {
            InputBox.Text = text;
            if (translate)
                StartTranslate();
            else
                SetDirection("已识别文字，按 Enter 翻译");
        }
        InputBox.Focus();
        InputBox.SelectAll();
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
        SetDirection("→ " + Languages.Get(to).Name);
        _results.Clear();

        foreach (var engine in App.Settings.Engines.Where(e => e.Enabled))
        {
            var translator = TranslatorFactory.Create(engine) is { } created ? Glossary.Wrap(created, App.Settings) : null;
            var item = new ResultItem(engine.Name);
            _results.Add(item);
            if (translator == null)
                item.Fail($"未知的引擎类型：{engine.Type}");
            else
                _ = RunAsync(item, translator, text, to, ct);
        }

        if (_results.Count == 0)
        {
            var item = new ResultItem("提示");
            item.Fail("没有启用的翻译引擎，请在设置中启用。");
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
        if (PinButton.IsChecked != true)
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
