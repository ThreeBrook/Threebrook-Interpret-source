using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Views;

/// <summary>
/// 钉在桌面的截图：置顶、可拖动；滚轮缩放，Ctrl+滚轮调透明度，双击或 Esc 关闭。
/// 尺寸按物理像素设置，100% 时与原屏幕像素一一对应。
/// 钉住后在后台识别文字，识别完鼠标移到文字上变成 I 形光标，拖动选取文字后可复制、翻译；
/// 在文字以外的地方拖动（或按住 Ctrl 在任意位置拖动）移动钉图。
/// </summary>
public partial class PinWindow : Window
{
    private const double MinZoom = 0.1, MaxZoom = 5.0, ZoomStep = 1.1;
    private const double MinOpacity = 0.2;

    private readonly BitmapSource _image;
    private readonly DispatcherTimer _labelTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _ocrCancel = new();
    private int _x, _y;
    private double _zoom = 1.0;

    // —— 文字选取 ——
    // 识别结果按阅读顺序展开成一串单词，选区是其中一段 [_selStart, _selEnd]；没识别完或没有文字时 _words 为空
    private List<OcrLine> _lines = [];
    private readonly List<(int Line, OcrWord Word)> _words = [];
    private int _anchor = -1, _selStart = -1, _selEnd = -1;
    private bool _selecting;

    /// <summary>界面预览用：不在后台识别，由预览代码直接给出识别结果。</summary>
    internal bool PreviewMode { get; init; }

    /// <param name="x">左上角屏幕物理像素坐标。</param>
    /// <param name="zoom">初始缩放（长截图比屏幕高时缩小显示）。</param>
    public PinWindow(BitmapSource image, int x, int y, double zoom = 1.0)
    {
        InitializeComponent();
        _image = image;
        _x = x;
        _y = y;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        PinImage.Source = image;
        TextLayer.Width = image.PixelWidth;
        TextLayer.Height = image.PixelHeight;
        _labelTimer.Tick += (_, _) =>
        {
            _labelTimer.Stop();
            ZoomLabel.Visibility = Visibility.Collapsed;
        };
        SourceInitialized += (_, _) =>
        {
            // 尺寸按物理像素管理，忽略 DPI 变化，否则在缩放比例不同的显示器上会被系统缩放、不再 1:1
            HwndSource.FromHwnd(Handle)!.AddHook(IgnoreDpiChange);
            ApplySize();
        };
        Loaded += (_, _) =>
        {
            if (PreviewMode)
                return;
            Activate();
            RecognizeText();
        };
        // 操作条是独立的弹出层，切到别的窗口时先收起，回来再显示
        Deactivated += (_, _) => TextActions.IsOpen = false;
        Activated += (_, _) => ShowTextActions();
        Closed += (_, _) =>
        {
            _ocrCancel.Cancel();
            TextActions.IsOpen = false;
        };
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    private void ApplySize()
    {
        var width = Math.Max(1, (int)Math.Round(_image.PixelWidth * _zoom));
        var height = Math.Max(1, (int)Math.Round(_image.PixelHeight * _zoom));
        SetWindowPos(Handle, IntPtr.Zero, _x, _y, width, height, 0x0004 | 0x0010 /* NOZORDER | NOACTIVATE */);
        RenderOptions.SetBitmapScalingMode(PinImage,
            Math.Abs(_zoom - 1.0) < 0.001 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private void SetZoom(double zoom)
    {
        // 拖动后位置可能已变，先读回当前左上角
        if (GetWindowRect(Handle, out var rect))
        {
            _x = rect.Left;
            _y = rect.Top;
        }
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplySize();
        ShowLabel($"{_zoom:P0}");
        // 等窗口按新尺寸排版后，操作条再跟着选区挪位置
        Dispatcher.BeginInvoke(ShowTextActions, DispatcherPriority.Loaded);
    }

    private void SetPinOpacity(double opacity)
    {
        Opacity = Math.Clamp(opacity, MinOpacity, 1.0);
        ShowLabel(Loc.F("Pin.OpacityValue", Opacity.ToString("P0")));
    }

    private void ShowLabel(string text)
    {
        ZoomText.Text = text;
        ZoomLabel.Visibility = Visibility.Visible;
        _labelTimer.Stop();
        _labelTimer.Start();
    }

    // —— 文字识别 ——

    private async void RecognizeText()
    {
        try
        {
            var lines = await OcrService.RecognizeLinesAsync(_image, _ocrCancel.Token);
            SetLines(lines);
            if (_words.Count > 0)
            {
                ShowLabel(Loc.T("Pin.TextReady"));
                UpdateCursor(Mouse.GetPosition(TextLayer));
            }
        }
        catch (Exception)
        {
            // 后台识别只是附加功能：没装 OCR 语言、钉图已关闭等情况都不打扰，钉图照常可用；
            // 右键"识别文字"会重新识别并给出错误提示
        }
    }

    private void SetLines(List<OcrLine> lines)
    {
        _lines = lines;
        _words.Clear();
        for (var i = 0; i < lines.Count; i++)
            _words.AddRange(lines[i].Words.Select(word => (i, word)));
        ClearSelection();
    }

    /// <summary>行高的这个比例作为命中文字的放宽距离：行与行之间、字与字之间的空隙也算在文字上。</summary>
    private const double HitPadding = 0.3;

    /// <summary>鼠标下面的单词（放宽到行间距），不在文字上返回 -1。</summary>
    private int WordAt(Point p)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var bounds = _lines[i].Bounds;
            var pad = bounds.Height * HitPadding;
            bounds.Inflate(pad, pad);
            if (bounds.Contains(p))
                return NearestInLine(i, p.X);
        }
        return -1;
    }

    /// <summary>拖动选取时鼠标可能离开文字：取上下最近的一行，再取这一行里左右最近的单词。</summary>
    private int NearestWord(Point p)
    {
        var line = -1;
        var best = double.MaxValue;
        for (var i = 0; i < _lines.Count; i++)
        {
            var bounds = _lines[i].Bounds;
            var distance = p.Y < bounds.Top ? bounds.Top - p.Y : p.Y > bounds.Bottom ? p.Y - bounds.Bottom : 0;
            if (distance == 0 && !(p.X >= bounds.Left && p.X <= bounds.Right))
                distance = 0.001; // 同一高度有多段（分栏）时，优先水平方向也落在范围里的那段
            if (distance < best)
            {
                best = distance;
                line = i;
            }
        }
        return line < 0 ? -1 : NearestInLine(line, p.X);
    }

    private int NearestInLine(int line, double x)
    {
        var first = _words.FindIndex(w => w.Line == line);
        var result = first;
        var best = double.MaxValue;
        for (var i = first; i < _words.Count && _words[i].Line == line; i++)
        {
            var box = _words[i].Word.Box;
            var distance = x < box.Left ? box.Left - x : x > box.Right ? x - box.Right : 0;
            if (distance < best)
            {
                best = distance;
                result = i;
            }
        }
        return result;
    }

    private bool HasSelection => _selStart >= 0;

    private void Select(int from, int to)
    {
        _selStart = Math.Min(from, to);
        _selEnd = Math.Max(from, to);
        DrawSelection();
    }

    private void ClearSelection()
    {
        _anchor = _selStart = _selEnd = -1;
        TextLayer.Children.Clear();
        TextActions.IsOpen = false;
    }

    /// <summary>每行选中的部分画一块高亮（比逐字画整齐，字间空隙也连起来）。</summary>
    private void DrawSelection()
    {
        TextLayer.Children.Clear();
        foreach (var bounds in SelectedLineBounds())
        {
            var pad = bounds.Height * 0.12;
            var highlight = new Rectangle
            {
                Width = bounds.Width + pad * 2,
                Height = bounds.Height + pad * 2,
                RadiusX = pad,
                RadiusY = pad,
                Opacity = 0.32,
            };
            highlight.SetResourceReference(Shape.FillProperty, "B.Accent");
            Canvas.SetLeft(highlight, bounds.Left - pad);
            Canvas.SetTop(highlight, bounds.Top - pad);
            TextLayer.Children.Add(highlight);
        }
    }

    private IEnumerable<Rect> SelectedLineBounds() =>
        SelectedLines().Select(words => words.Aggregate(Rect.Empty, (bounds, word) => Rect.Union(bounds, word.Box)));

    private IEnumerable<List<OcrWord>> SelectedLines()
    {
        if (!HasSelection)
            yield break;
        var current = new List<OcrWord>();
        for (var i = _selStart; i <= _selEnd; i++)
        {
            current.Add(_words[i].Word);
            if (i == _selEnd || _words[i + 1].Line != _words[i].Line)
            {
                yield return current;
                current = [];
            }
        }
    }

    /// <summary>选中的文字：同一行按 OCR 规则拼接单词，行间换行。</summary>
    internal string SelectedText => string.Join('\n', SelectedLines().Select(OcrService.JoinWords));

    /// <summary>操作条放在选区下方（下面放不下时系统会放到上方）。</summary>
    private void ShowTextActions()
    {
        TextActions.IsOpen = false;
        if (!HasSelection || _selecting || !IsActive && !PreviewMode)
            return;
        var bounds = SelectedLineBounds().Aggregate(Rect.Empty, Rect.Union);
        TextActions.PlacementRectangle = TextLayer.TransformToAncestor(Root).TransformBounds(bounds);
        TextActions.IsOpen = true;
    }

    private void CopySelection()
    {
        if (!HasSelection)
            return;
        ImageActions.CopyText(App.PrepareText(SelectedText) ?? "");
        ShowLabel(Loc.T("Pin.TextCopied"));
    }

    private void TranslateSelection()
    {
        if (HasSelection)
            App.Instance.ShowRecognizedText(SelectedText, translate: true);
    }

    private void SelectAll()
    {
        if (_words.Count == 0)
            return;
        _anchor = 0;
        Select(0, _words.Count - 1);
        ShowTextActions();
    }

    private void UpdateCursor(Point p) =>
        Cursor = _words.Count > 0 && (Keyboard.Modifiers & ModifierKeys.Control) == 0 && WordAt(p) >= 0 ? Cursors.IBeam : null;

    // —— 鼠标、键盘 ——

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(TextLayer);
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var word = ctrl ? -1 : WordAt(p);
        if (word >= 0)
        {
            if (e.ClickCount == 2)
            {
                // 双击文字选中整行
                var line = _words[word].Line;
                Select(_words.FindIndex(w => w.Line == line), _words.FindLastIndex(w => w.Line == line));
                ShowTextActions();
                return;
            }
            // 按住 Shift 点击：从原来的起点延伸选区
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || _anchor < 0)
                _anchor = word;
            _selecting = true;
            TextActions.IsOpen = false;
            Select(_anchor, word);
            CaptureMouse();
            return;
        }

        ClearSelection();
        if (e.ClickCount == 2)
            Close();
        else
            DragMove();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(TextLayer);
        if (!_selecting)
        {
            UpdateCursor(p);
            return;
        }
        var word = NearestWord(p);
        if (word >= 0 && (Math.Min(_anchor, word), Math.Max(_anchor, word)) != (_selStart, _selEnd))
            Select(_anchor, word);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_selecting)
            return;
        _selecting = false;
        ReleaseMouseCapture();
        ShowTextActions();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var up = e.Delta > 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            SetPinOpacity(Opacity + (up ? 0.1 : -0.1));
        else
            SetZoom(up ? _zoom * ZoomStep : _zoom / ZoomStep);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape)
        {
            // 先取消选中的文字，再按一次关闭钉图
            if (HasSelection)
                ClearSelection();
            else
                Close();
        }
        else if (ctrl && e.Key == Key.C)
        {
            if (HasSelection)
                CopySelection();
            else
                ImageActions.Copy(_image);
        }
        else if (ctrl && e.Key == Key.A)
            SelectAll();
        else if (ctrl && e.Key == Key.S)
            ImageActions.Save(_image, this);
        else if (ctrl && e.Key is Key.D0 or Key.NumPad0)
            SetZoom(1.0);
        else if (e.Key is Key.LeftCtrl or Key.RightCtrl)
            UpdateCursor(Mouse.GetPosition(TextLayer)); // 按住 Ctrl 时在文字上也显示普通光标，表示可以拖动
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
            UpdateCursor(Mouse.GetPosition(TextLayer));
    }

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var selected = HasSelection ? Visibility.Visible : Visibility.Collapsed;
        CopySelectionItem.Visibility = TranslateSelectionItem.Visibility = SelectionSeparator.Visibility = selected;
        SelectAllItem.Visibility = _words.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // 选中文字时 Ctrl+C 复制文字，复制图片就不再标快捷键
        CopyImageItem.InputGestureText = HasSelection ? "" : "Ctrl+C";
    }

    // 已在后台识别过就直接用结果，没识别出来（还在识别、识别失败）再重新识别
    private void Ocr_Click(object sender, RoutedEventArgs e) => ShowAllText(translate: false);
    private void Translate_Click(object sender, RoutedEventArgs e) => ShowAllText(translate: true);

    private void ShowAllText(bool translate)
    {
        if (_words.Count > 0)
            App.Instance.ShowRecognizedText(OcrService.ToText(_lines), translate);
        else
            App.Instance.RecognizeImage(_image, translate);
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e) => CopySelection();
    private void TranslateSelection_Click(object sender, RoutedEventArgs e) => TranslateSelection();
    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll();
    private void Copy_Click(object sender, RoutedEventArgs e) => ImageActions.Copy(_image);
    private void Save_Click(object sender, RoutedEventArgs e) => ImageActions.Save(_image, this);
    private void ResetZoom_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Opacity_Click(object sender, RoutedEventArgs e) =>
        SetPinOpacity(double.Parse((string)((MenuItem)sender).Tag, CultureInfo.InvariantCulture));

    // —— 界面预览 ——

    /// <summary>预览：用给定的识别结果选中一段文字（按单词序号），返回选中的文字。</summary>
    internal string ShowPreviewSelection(List<OcrLine> lines, int from, int to)
    {
        SetLines(lines);
        _anchor = from;
        Select(from, Math.Min(to, _words.Count - 1));
        return SelectedText;
    }

    internal int PreviewWordAt(Point imagePoint) => WordAt(imagePoint);
    internal int PreviewNearestWord(Point imagePoint) => NearestWord(imagePoint);

    /// <summary>预览：操作条从弹出层里取出来单独渲染。</summary>
    internal FrameworkElement DetachTextActions()
    {
        var bar = TextActionsBar;
        TextActions.Child = null;
        return bar;
    }

    private static IntPtr IgnoreDpiChange(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DPICHANGED = 0x02E0;
        if (msg == WM_DPICHANGED)
            handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
