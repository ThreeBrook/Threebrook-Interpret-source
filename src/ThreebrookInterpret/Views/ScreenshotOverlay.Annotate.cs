using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ThreebrookInterpret.Views;

/// <summary>
/// 截图标注：矩形、椭圆、箭头、画笔、荧光笔、文字、马赛克、序号；Ctrl+Z / Ctrl+Y 撤销重做。
/// 标注画在与画布同尺寸、裁剪到选区的 Annotations 层上，导出时按物理像素与截图合成。
/// </summary>
public partial class ScreenshotOverlay
{
    private enum Tool { None, Rect, Ellipse, Arrow, Pen, Highlighter, Text, Mosaic, Number }

    /// <summary>标注颜色：醒目红为默认，其余取自三溪品牌与产品色（月黄、溪绿、晴蓝、暮紫）及黑白。</summary>
    private static readonly (string Hex, string Name)[] Palette =
    {
        ("#E5484D", "红"), ("#F4C25B", "月黄"), ("#1F9E6B", "溪绿"), ("#2F7FC1", "晴蓝"),
        ("#7460C4", "暮紫"), ("#FFFFFF", "白"), ("#1D3331", "墨"),
    };

    private static readonly double[] StrokeWidths = { 2, 4, 7 };
    private static readonly double[] FontSizes = { 16, 22, 32 };
    private static readonly double[] NumberSizes = { 22, 28, 38 };
    private static readonly int[] MosaicBlocks = { 6, 10, 16 }; // 物理像素

    private Tool _tool;
    private Color _color = (Color)ColorConverter.ConvertFromString(Palette[0].Hex);
    private int _size = 1;
    private readonly Stack<UIElement> _redo = new();
    private FrameworkElement? _current;
    private Point _drawStart;
    private TextBox? _editingText;
    private int _nextNumber = 1;

    private bool HasAnnotations => Annotations.Children.Count > 0;
    private bool Annotating => _tool != Tool.None;
    private double Stroke => StrokeWidths[_size];
    private SolidColorBrush Brush => new(_color);

    private void InitAnnotationTools()
    {
        foreach (var (hex, name) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                GroupName = "Color",
                ToolTip = name,
                IsChecked = color == _color,
                Content = new Ellipse
                {
                    Width = 16, Height = 16, Fill = new SolidColorBrush(color),
                    Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), StrokeThickness = 1,
                },
            };
            swatch.Checked += (_, _) =>
            {
                _color = color;
                if (_editingText != null)
                    _editingText.Foreground = Brush;
            };
            ColorButtons.Children.Add(swatch);
        }
    }

    // —— 工具切换 ——

    private void Tool_Click(object sender, RoutedEventArgs e) =>
        SetTool(Enum.Parse<Tool>((string)((FrameworkElement)sender).Tag));

    /// <summary>再点一次当前工具则取消，回到"拖动移动选区"。</summary>
    private void SetTool(Tool tool)
    {
        CommitText();
        _tool = _tool == tool ? Tool.None : tool;
        foreach (ToggleButton button in ToolButtons.Children)
            button.IsChecked = (string)button.Tag == _tool.ToString();
        PropertyBar.Visibility = Annotating ? Visibility.Visible : Visibility.Collapsed;
        UpdateHandles();
        ShowToolbar();
    }

    private void Size_Checked(object sender, RoutedEventArgs e)
    {
        _size = int.Parse((string)((FrameworkElement)sender).Tag);
        if (_editingText != null)
            _editingText.FontSize = FontSizes[_size];
    }

    private Cursor ToolCursor => _tool switch
    {
        Tool.Pen or Tool.Highlighter => Cursors.Pen,
        Tool.Text => Cursors.IBeam,
        Tool.Number => Cursors.Hand,
        _ => Cursors.Cross,
    };

    // —— 绘制 ——

    private void BeginDraw(Point p)
    {
        CommitText();
        _redo.Clear();
        _drawStart = p;

        switch (_tool)
        {
            case Tool.Text:
                BeginText(p);
                return;
            case Tool.Number:
                AddNumber(p);
                return;
            case Tool.Rect:
                _current = new Rectangle { Stroke = Brush, StrokeThickness = Stroke, RadiusX = 2, RadiusY = 2 };
                break;
            case Tool.Ellipse:
                _current = new Ellipse { Stroke = Brush, StrokeThickness = Stroke };
                break;
            case Tool.Arrow:
                _current = new Path { Fill = Brush, Stroke = Brush, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round };
                break;
            case Tool.Pen:
            case Tool.Highlighter:
                var highlighter = _tool == Tool.Highlighter;
                _current = new Polyline
                {
                    Stroke = Brush,
                    StrokeThickness = highlighter ? Stroke * 3 + 8 : Stroke,
                    Opacity = highlighter ? 0.4 : 1,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = highlighter ? PenLineCap.Flat : PenLineCap.Round,
                    StrokeEndLineCap = highlighter ? PenLineCap.Flat : PenLineCap.Round,
                    Points = { p },
                };
                break;
            case Tool.Mosaic:
                // 拖动时只显示虚线框，松开后再生成马赛克
                _current = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 } };
                break;
        }

        Annotations.Children.Add(_current!);
        UpdateHandles();
        UpdateDraw(p, shift: false);
        _state = State.Drawing;
        if (!PreviewMode)
            CaptureMouse();
    }

    private void UpdateDraw(Point p, bool shift)
    {
        if (_current == null)
            return;
        p = ClampToSelection(p);

        switch (_current)
        {
            case Polyline line:
                line.Points.Add(p);
                break;
            case Path arrow:
                arrow.Data = ArrowGeometry(_drawStart, p, Stroke);
                break;
            default:
                var rect = new Rect(_drawStart, p);
                if (shift)
                {
                    // Shift：正方形 / 圆
                    var side = Math.Max(rect.Width, rect.Height);
                    rect = new Rect(p.X < _drawStart.X ? _drawStart.X - side : _drawStart.X,
                                    p.Y < _drawStart.Y ? _drawStart.Y - side : _drawStart.Y, side, side);
                }
                Canvas.SetLeft(_current, rect.X);
                Canvas.SetTop(_current, rect.Y);
                _current.Width = rect.Width;
                _current.Height = rect.Height;
                break;
        }
    }

    private void EndDraw()
    {
        ReleaseMouseCapture();
        _state = State.Selected;
        var shape = _current;
        _current = null;
        if (shape == null)
            return;

        var tooSmall = shape switch
        {
            Polyline line => line.Points.Count < 2,
            Path arrow => arrow.Data == null || arrow.Data.Bounds.IsEmpty || (arrow.Data.Bounds.Width < 4 && arrow.Data.Bounds.Height < 4),
            _ => shape.Width < 3 || shape.Height < 3,
        };
        if (tooSmall)
        {
            Annotations.Children.Remove(shape);
            return;
        }

        if (_tool == Tool.Mosaic)
        {
            var index = Annotations.Children.IndexOf(shape);
            Annotations.Children.RemoveAt(index);
            Annotations.Children.Insert(index, Mosaic(new Rect(Canvas.GetLeft(shape), Canvas.GetTop(shape), shape.Width, shape.Height)));
        }
    }

    /// <summary>箭头：杆从尾部细到头部粗，带实心三角箭头。</summary>
    private static Geometry ArrowGeometry(Point from, Point to, double width)
    {
        var v = to - from;
        var length = v.Length;
        if (length < 2)
            return Geometry.Empty;
        v.Normalize();
        var n = new Vector(-v.Y, v.X);
        var head = Math.Min(length, Math.Max(12, width * 4));
        var half = head * 0.5;
        var neck = to - v * head;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(from + n * (width * 0.2), true, true);
            ctx.LineTo(neck + n * (width * 0.6), true, true);
            ctx.LineTo(neck + n * half, true, true);
            ctx.LineTo(to, true, true);
            ctx.LineTo(neck - n * half, true, true);
            ctx.LineTo(neck - n * (width * 0.6), true, true);
            ctx.LineTo(from - n * (width * 0.2), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>马赛克：把原始截图对应区域缩小再用最近邻放大。</summary>
    private Image Mosaic(Rect rect)
    {
        var pixels = ToPixels(rect);
        var block = MosaicBlocks[_size];
        var crop = new CroppedBitmap(_screen, pixels);
        var small = new TransformedBitmap(crop, new ScaleTransform(
            Math.Max(1.0 / block, 1.0 / pixels.Width), Math.Max(1.0 / block, 1.0 / pixels.Height)));
        small.Freeze();
        var image = new Image { Source = small, Width = rect.Width, Height = rect.Height, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(image, rect.X);
        Canvas.SetTop(image, rect.Y);
        return image;
    }

    private void AddNumber(Point p)
    {
        var size = NumberSizes[_size];
        var light = _color.R * 0.299 + _color.G * 0.587 + _color.B * 0.114 > 160;
        var badge = new Grid { Width = size, Height = size, Tag = "number" };
        badge.Children.Add(new Ellipse { Fill = Brush, Stroke = Brushes.White, StrokeThickness = 1.5 });
        badge.Children.Add(new TextBlock
        {
            Text = _nextNumber.ToString(),
            Foreground = light ? new SolidColorBrush(Color.FromRgb(0x1D, 0x33, 0x31)) : Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = size * 0.52,
            FontFamily = (FontFamily)FindResource("F.Body"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _nextNumber++;
        p = ClampToSelection(p);
        Canvas.SetLeft(badge, p.X - size / 2);
        Canvas.SetTop(badge, p.Y - size / 2);
        Annotations.Children.Add(badge);
    }

    // —— 文字 ——

    private void BeginText(Point p)
    {
        var box = new TextBox
        {
            Style = (Style)FindResource("AnnotationText"),
            Foreground = Brush,
            CaretBrush = Brush,
            FontSize = FontSizes[_size],
            FontFamily = (FontFamily)FindResource("F.Body"),
        };
        p = ClampToSelection(p);
        Canvas.SetLeft(box, p.X);
        Canvas.SetTop(box, p.Y - FontSizes[_size] * 0.7);
        Annotations.Children.Add(box);
        _editingText = box;
        Dispatcher.BeginInvoke(() => Keyboard.Focus(box), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>结束文字输入：空则删除，否则换成不可编辑的文字（导出时没有虚线框和光标）。</summary>
    private void CommitText()
    {
        var box = _editingText;
        if (box == null)
            return;
        _editingText = null;

        var index = Annotations.Children.IndexOf(box);
        Annotations.Children.Remove(box);
        Focus();
        if (string.IsNullOrWhiteSpace(box.Text))
            return;

        var text = new TextBlock
        {
            Text = box.Text.TrimEnd(),
            Foreground = box.Foreground,
            FontSize = box.FontSize,
            FontFamily = box.FontFamily,
            FontWeight = FontWeights.SemiBold,
        };
        // 与输入框里的文字位置对齐（边框 1 + 内边距 3 + TextBox 内部约 2）
        Canvas.SetLeft(text, Canvas.GetLeft(box) + 6);
        Canvas.SetTop(text, Canvas.GetTop(box) + 2);
        Annotations.Children.Insert(Math.Max(0, index), text);
    }

    // —— 撤销 / 重做 ——

    private void Undo()
    {
        CommitText();
        if (!HasAnnotations)
            return;
        var last = Annotations.Children[^1];
        Annotations.Children.Remove(last);
        _redo.Push(last);
        if (last is FrameworkElement { Tag: "number" })
            _nextNumber--;
        UpdateHandles();
    }

    private void Redo()
    {
        if (_redo.Count == 0)
            return;
        var element = _redo.Pop();
        Annotations.Children.Add(element);
        if (element is FrameworkElement { Tag: "number" })
            _nextNumber++;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private Point ClampToSelection(Point p) =>
        new(Math.Clamp(p.X, _selection.Left, _selection.Right), Math.Clamp(p.Y, _selection.Top, _selection.Bottom));

    // —— 导出 ——

    /// <summary>截图 + 标注，按选区的物理像素尺寸合成；没有标注时直接返回原图。</summary>
    private BitmapSource Compose(out Int32Rect pixels)
    {
        CommitText();
        var cropped = Crop(out pixels);
        if (!HasAnnotations)
            return cropped;

        // 刚加入的标注（如按 Enter 时才结束输入的文字）还没布局，渲染出来是空的（下方渲染前会 UpdateLayout）

        // 只渲染选区大小：把标注层平移到选区左上角再渲染，按屏幕物理像素的 DPI（高 DPI 下线条清晰）。
        // 不再整层渲染后裁剪——多台 4K 显示器时那样每次要分配上百 MB。
        var dpiX = 96.0 * _screen.PixelWidth / Layer.ActualWidth;
        var dpiY = 96.0 * _screen.PixelHeight / Layer.ActualHeight;
        var originalTransform = Annotations.RenderTransform;
        Annotations.RenderTransform = new TranslateTransform(-_selection.X, -_selection.Y);
        Annotations.UpdateLayout();
        var marks = new RenderTargetBitmap(pixels.Width, pixels.Height, dpiX, dpiY, PixelFormats.Pbgra32);
        marks.Render(Annotations);
        Annotations.RenderTransform = originalTransform;

        var rect = new Rect(0, 0, pixels.Width * 96.0 / dpiX, pixels.Height * 96.0 / dpiY);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(cropped, rect);
            dc.DrawImage(marks, rect);
        }
        var bitmap = new RenderTargetBitmap(pixels.Width, pixels.Height, dpiX, dpiY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void ClearAnnotations()
    {
        _editingText = null;
        Annotations.Children.Clear();
        _redo.Clear();
        _nextNumber = 1;
        if (Annotating)
            SetTool(_tool);
    }

    /// <summary>界面预览用：不激活窗口、不捕获鼠标。</summary>
    internal bool PreviewMode { get; set; }

    /// <summary>界面预览用：在选区里把每种标注各画一个，返回导出的合成图。</summary>
    internal BitmapSource RenderAnnotationPreview()
    {
        _selection = new Rect(60, 50, 600, 300);
        _state = State.Selected;
        Hint.Visibility = Visibility.Collapsed;
        UpdateSelection();

        void Use(Tool tool, int color, int size = 1)
        {
            _tool = tool;
            _color = (Color)ColorConverter.ConvertFromString(Palette[color].Hex);
            _size = size;
        }
        void Drag(params Point[] points)
        {
            BeginDraw(points[0]);
            foreach (var p in points.Skip(1))
                UpdateDraw(p, shift: false);
            EndDraw();
        }

        Use(Tool.Mosaic, 0, 1); Drag(new Point(150, 262), new Point(330, 300));
        Use(Tool.Highlighter, 1); Drag(new Point(90, 98), new Point(330, 98));
        Use(Tool.Rect, 0); Drag(new Point(80, 150), new Point(300, 220));
        Use(Tool.Ellipse, 1, 2); Drag(new Point(440, 140), new Point(640, 260));
        Use(Tool.Arrow, 0, 2); Drag(new Point(380, 300), new Point(310, 215));
        Use(Tool.Pen, 2); Drag(Enumerable.Range(0, 40).Select(i => new Point(90 + i * 6, 335 + Math.Sin(i / 3.0) * 6)).ToArray());
        Use(Tool.Number, 0); BeginDraw(new Point(80, 150)); BeginDraw(new Point(450, 160));
        Use(Tool.Text, 0); BeginDraw(new Point(400, 270));
        _editingText!.Text = "这里改成周五";
        CommitText();

        SetTool(Tool.Arrow); // 显示属性栏
        ShowToolbar();
        return Compose(out _);
    }

    /// <summary>界面预览用：只框选，不标注，用来检查不同选区宽度下的工具栏排布。</summary>
    internal void RenderSelectionPreview(Rect selection)
    {
        _selection = selection;
        _state = State.Selected;
        Hint.Visibility = Visibility.Collapsed;
        UpdateSelection();
        ShowToolbar();
    }

    /// <summary>数字键 1–8 选择工具。</summary>
    private static Tool? ToolForKey(Key key) => key switch
    {
        Key.D1 or Key.NumPad1 => Tool.Rect,
        Key.D2 or Key.NumPad2 => Tool.Ellipse,
        Key.D3 or Key.NumPad3 => Tool.Arrow,
        Key.D4 or Key.NumPad4 => Tool.Pen,
        Key.D5 or Key.NumPad5 => Tool.Highlighter,
        Key.D6 or Key.NumPad6 => Tool.Text,
        Key.D7 or Key.NumPad7 => Tool.Mosaic,
        Key.D8 or Key.NumPad8 => Tool.Number,
        _ => null,
    };
}
