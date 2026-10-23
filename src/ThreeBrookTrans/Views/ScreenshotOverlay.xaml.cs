using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThreeBrookTrans.Services;

namespace ThreeBrookTrans.Views;

/// <summary>
/// 全屏截图遮罩：显示冻结的屏幕画面，拖动框选，选区可拖动移动，可标注（见 ScreenshotOverlay.Annotate.cs），然后钉图 / 复制 / 保存。
/// 窗口用物理像素铺满整个虚拟屏幕，并忽略 DPI 变化消息，保证画面与屏幕像素一一对应。
/// </summary>
public partial class ScreenshotOverlay : Window
{
    private enum State { Idle, Selecting, Moving, Resizing, Selected, Drawing }

    private const double MinSelection = 3;

    private readonly BitmapSource _screen;
    private readonly System.Drawing.Rectangle _bounds;
    private State _state = State.Idle;
    private Point _anchor;
    private Rect _selection;
    private Rect _selectionBeforeMove;

    private readonly bool _translateMode;

    /// <param name="translateMode">截图翻译模式：松开鼠标后直接识别并翻译。</param>
    public ScreenshotOverlay(BitmapSource screen, System.Drawing.Rectangle bounds, bool translateMode = false)
    {
        InitializeComponent();
        _screen = screen;
        _bounds = bounds;
        _translateMode = translateMode;
        if (translateMode)
            HintText.Text = "框选要翻译的区域 · 右键 / Esc 取消";
        ScreenImage.Source = screen;
        InitAnnotationTools();
        InitSelectionHelpers();
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => UpdateMask();
        Loaded += (_, _) =>
        {
            if (PreviewMode)
                return;
            Activate();
            Focus();
            PlaceHint();
            UpdateMask();
            UpdateHover(Mouse.GetPosition(Layer));
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)!.AddHook(IgnoreDpiChange);
        SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, 0);
    }

    private static IntPtr IgnoreDpiChange(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DPICHANGED = 0x02E0;
        if (msg == WM_DPICHANGED)
            handled = true;
        return IntPtr.Zero;
    }

    // —— 鼠标交互 ——

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Toolbar.IsMouseOver)
            return;
        var p = e.GetPosition(Layer);

        if (_state == State.Selected && Annotating)
        {
            if (_selection.Contains(p))
                BeginDraw(p);
            else
                CommitText();
            return;
        }
        if (_state == State.Selected && CanAdjustSelection && HitEdges(p) is var edges && edges != Edge.None)
        {
            BeginResize(p, edges);
            return;
        }
        if (_state == State.Selected && _selection.Contains(p))
        {
            if (e.ClickCount == 2)
            {
                Copy();
                return;
            }
            if (HasAnnotations)
                return; // 已有标注时不能移动选区
            _state = State.Moving;
            _anchor = p;
            _selectionBeforeMove = _selection;
        }
        else if (_state == State.Selected && HasAnnotations)
        {
            return; // 已有标注时不能重新框选，右键可清除
        }
        else
        {
            _hoverAtPress = _state == State.Idle ? _selection : Rect.Empty;
            _state = State.Selecting;
            _dragStarted = false;
            _anchor = p;
            if (_hoverAtPress.IsEmpty)
                _selection = new Rect(p, p);
        }

        Toolbar.Visibility = Visibility.Collapsed;
        Hint.Visibility = Visibility.Collapsed;
        CaptureMouse();
        UpdateSelection();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(Layer);

        switch (_state)
        {
            case State.Idle:
                UpdateHover(p);
                break;
            case State.Selecting:
                if (!_dragStarted && (p - _anchor).Length < DragThreshold)
                    break; // 还没拖动：可能是单击选中窗口
                _dragStarted = true;
                _selection = new Rect(Clamp(_anchor), Clamp(p));
                UpdateSelection();
                break;
            case State.Resizing:
                ResizeTo(p);
                break;
            case State.Moving:
                var x = Math.Clamp(_selectionBeforeMove.X + p.X - _anchor.X, 0, Layer.ActualWidth - _selection.Width);
                var y = Math.Clamp(_selectionBeforeMove.Y + p.Y - _anchor.Y, 0, Layer.ActualHeight - _selection.Height);
                _selection = new Rect(x, y, _selection.Width, _selection.Height);
                UpdateSelection();
                break;
            case State.Drawing:
                UpdateDraw(p, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                break;
            case State.Selected when CanAdjustSelection && !Toolbar.IsMouseOver && HitEdges(p) is var hover && hover != Edge.None:
                Cursor = EdgeCursor(hover);
                break;
            case State.Selected:
                var inside = _selection.Contains(p) && !Toolbar.IsMouseOver;
                Cursor = Annotating ? (inside ? ToolCursor : Cursors.Arrow)
                    : inside ? (HasAnnotations ? Cursors.Arrow : Cursors.SizeAll)
                    : HasAnnotations ? Cursors.Arrow : Cursors.Cross;
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_state == State.Drawing)
        {
            EndDraw();
            return;
        }
        if (_state is not (State.Selecting or State.Moving or State.Resizing))
            return;

        ReleaseMouseCapture();
        if (_state == State.Selecting && !_dragStarted)
            _selection = _hoverAtPress.IsEmpty ? Rect.Empty : _hoverAtPress; // 单击：选中悬停的窗口
        if (_selection.IsEmpty || _selection.Width < MinSelection || _selection.Height < MinSelection)
        {
            Reset();
            return;
        }
        _state = State.Selected;
        UpdateHandles();
        if (_translateMode)
            Recognize(translate: true);
        else
            ShowToolbar();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_state is State.Selecting or State.Moving or State.Resizing or State.Drawing)
            return;
        if (Annotating)
        {
            SetTool(_tool);
            return;
        }
        if (_state == State.Idle)
            Close();
        else
            Reset();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        // 正在输入标注文字时，按键都给输入框；Esc / Ctrl+Enter 结束输入
        if (_editingText != null)
        {
            if (e.Key == Key.Escape || (ctrl && e.Key == Key.Enter))
            {
                CommitText();
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Escape)
            Close();
        else if (_state != State.Selected)
            return;
        else if (ctrl && e.Key == Key.Z)
            Undo();
        else if (ctrl && e.Key == Key.Y)
            Redo();
        else if (!ctrl && ToolForKey(e.Key) is { } tool)
            SetTool(tool);
        else if (e.Key == Key.Enter || (ctrl && e.Key == Key.C))
            Copy();
        else if (ctrl && e.Key == Key.S)
            Save();
        else if (e.Key == Key.F3)
            Pin();
        else if (e.Key == Key.O)
            Recognize(translate: false);
        else if (e.Key == Key.T)
            Recognize(translate: true);
    }

    private void Reset()
    {
        _state = State.Idle;
        _selection = Rect.Empty;
        ClearAnnotations();
        Cursor = Cursors.Cross;
        Toolbar.Visibility = Visibility.Collapsed;
        SelectionBorder.Visibility = Visibility.Collapsed;
        SizeLabel.Visibility = Visibility.Collapsed;
        Hint.Visibility = Visibility.Visible;
        UpdateHandles();
        UpdateMask();
        UpdateHover(Mouse.GetPosition(Layer));
    }

    // —— 绘制 ——

    private void UpdateSelection()
    {
        UpdateMask();

        SelectionBorder.Visibility = Visibility.Visible;
        Annotations.Clip = new RectangleGeometry(_selection);
        Canvas.SetLeft(SelectionBorder, _selection.X - 1);
        Canvas.SetTop(SelectionBorder, _selection.Y - 1);
        SelectionBorder.Width = _selection.Width + 2;
        SelectionBorder.Height = _selection.Height + 2;

        var pixels = ToPixels(_selection);
        SizeText.Text = $"{pixels.Width} × {pixels.Height}";
        SizeLabel.Visibility = Visibility.Visible;
        SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelY = _selection.Y - SizeLabel.DesiredSize.Height - 4;
        Canvas.SetLeft(SizeLabel, _selection.X);
        Canvas.SetTop(SizeLabel, labelY >= 0 ? labelY : _selection.Y + 4);
        UpdateHandles();
    }

    private void UpdateMask()
    {
        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry(new Rect(0, 0, Layer.ActualWidth, Layer.ActualHeight)));
        if (!_selection.IsEmpty)
            mask.Children.Add(new RectangleGeometry(_selection));
        Mask.Data = mask;
    }

    private void ShowToolbar()
    {
        Toolbar.Visibility = Visibility.Visible;
        LayoutToolbarRows();
        var size = MeasureToolbar();

        var x = Math.Max(0, _selection.Right - size.Width);
        var y = _selection.Bottom + 6;
        if (y + size.Height > Layer.ActualHeight)
            y = _selection.Top - size.Height - 6; // 下方放不下放上方
        if (y < 0)
            y = _selection.Bottom - size.Height - 6; // 上下都放不下放选区内
        Canvas.SetLeft(Toolbar, x);
        Canvas.SetTop(Toolbar, y);
    }

    /// <summary>一排比选区宽时分成两排：上排标注工具，下排识别 / 钉图 / 复制等操作（靠右，与选区右边对齐）。</summary>
    private void LayoutToolbarRows()
    {
        ToolbarRows.Orientation = Orientation.Horizontal;
        GroupSeparator.Visibility = Visibility.Visible;
        ActionGroup.Margin = new Thickness(0);
        if (MeasureToolbar().Width <= _selection.Width)
            return;

        ToolbarRows.Orientation = Orientation.Vertical;
        GroupSeparator.Visibility = Visibility.Collapsed;
        ActionGroup.Margin = new Thickness(0, 2, 0, 0);
    }

    /// <summary>
    /// 切换排布后必须重新测量：直接调用 Measure 时，中间各层容器没有标记失效就会返回上次的尺寸，
    /// 工具栏会按一排的宽度定位到错误位置。所以从 ToolbarRows 到 Toolbar 逐层标记失效。
    /// </summary>
    private Size MeasureToolbar()
    {
        for (DependencyObject? element = ToolbarRows; element != null && element != Layer; element = VisualTreeHelper.GetParent(element))
            (element as UIElement)?.InvalidateMeasure();
        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Toolbar.DesiredSize;
    }

    private void PlaceHint()
    {
        // 提示放在鼠标所在位置附近，多显示器时不会跑到别的屏幕
        var p = Mouse.GetPosition(Layer);
        Hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(Hint, Math.Clamp(p.X + 16, 0, Math.Max(0, Layer.ActualWidth - Hint.DesiredSize.Width)));
        Canvas.SetTop(Hint, Math.Clamp(p.Y + 16, 0, Math.Max(0, Layer.ActualHeight - Hint.DesiredSize.Height)));
    }

    private Point Clamp(Point p) =>
        new(Math.Clamp(p.X, 0, Layer.ActualWidth), Math.Clamp(p.Y, 0, Layer.ActualHeight));

    /// <summary>选区（DIP）换算成截图上的物理像素区域。</summary>
    private Int32Rect ToPixels(Rect rect)
    {
        var sx = _screen.PixelWidth / Layer.ActualWidth;
        var sy = _screen.PixelHeight / Layer.ActualHeight;
        var x = (int)Math.Round(rect.X * sx);
        var y = (int)Math.Round(rect.Y * sy);
        var w = Math.Clamp((int)Math.Round(rect.Width * sx), 1, _screen.PixelWidth - x);
        var h = Math.Clamp((int)Math.Round(rect.Height * sy), 1, _screen.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    private BitmapSource Crop(out Int32Rect pixels)
    {
        pixels = ToPixels(_selection);
        // 复制成独立位图：CroppedBitmap 会一直引用整张虚拟屏幕截图，钉图、历史拿着它就会占用整屏内存
        var copy = new WriteableBitmap(new CroppedBitmap(_screen, pixels));
        copy.Freeze();
        return copy;
    }

    // —— 操作 ——

    private void Copy()
    {
        ImageActions.Copy(Compose(out _));
        Close();
    }

    private void Save()
    {
        var image = Compose(out _);
        Topmost = false; // 让"另存为"对话框显示在最前
        var path = ImageActions.Save(image, this);
        if (path != null)
            Close();
        else
            Topmost = true;
    }

    private void Pin()
    {
        var image = Compose(out var pixels);
        new PinWindow(image, _bounds.Left + pixels.X, _bounds.Top + pixels.Y).Show();
        Close();
    }

    private void Recognize(bool translate)
    {
        var image = Crop(out _); // 用原图识别，标注会干扰文字识别
        Close();
        App.Instance.RecognizeImage(image, translate);
    }

    private void Ocr_Click(object sender, RoutedEventArgs e) => Recognize(translate: false);
    private void Translate_Click(object sender, RoutedEventArgs e) => Recognize(translate: true);
    private void Pin_Click(object sender, RoutedEventArgs e) => Pin();
    private void Copy_Click(object sender, RoutedEventArgs e) => Copy();
    private void Save_Click(object sender, RoutedEventArgs e) => Save();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
