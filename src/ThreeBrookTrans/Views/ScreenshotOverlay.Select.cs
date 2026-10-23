using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using ThreeBrookTrans.Services;

namespace ThreeBrookTrans.Views;

/// <summary>
/// 选区辅助：鼠标悬停高亮所在窗口、单击直接选中；框选后拖动边和角调整大小（8 个控制点）；
/// 方向键微调位置、Shift+方向键调整大小。开始标注后选区锁定，不再显示控制点。
/// </summary>
public partial class ScreenshotOverlay
{
    [Flags]
    private enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    /// <summary>按下后移动超过这个距离才算框选，否则算单击选中窗口。</summary>
    private const double DragThreshold = 4;

    private const double EdgeTolerance = 6;
    private const double HandleSize = 7;

    private readonly List<System.Drawing.Rectangle> _windows = WindowFinder.Snapshot();
    private readonly Rectangle[] _handles = new Rectangle[8];
    private Rect _hoverAtPress = Rect.Empty;
    private bool _dragStarted;
    private Edge _resizeEdges;

    private void InitSelectionHelpers()
    {
        var index = Layer.Children.IndexOf(SelectionBorder) + 1;
        for (var i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Rectangle
            {
                Width = HandleSize, Height = HandleSize, Fill = System.Windows.Media.Brushes.White,
                StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            };
            _handles[i].SetResourceReference(Shape.StrokeProperty, "B.Brook");
            Layer.Children.Insert(index + i, _handles[i]);
        }
    }

    // —— 悬停识别窗口 ——

    /// <summary>空闲时把鼠标所在窗口（或显示器）作为候选选区显示出来。</summary>
    private void UpdateHover(Point p)
    {
        var hover = WindowAt(p);
        if (hover == _selection)
            return;
        _selection = hover;
        if (_selection.IsEmpty)
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            UpdateMask();
        }
        else
        {
            UpdateSelection();
        }
    }

    private Rect WindowAt(Point p)
    {
        if (Layer.ActualWidth <= 0)
            return Rect.Empty;
        var scale = Layer.ActualWidth / _screen.PixelWidth; // 物理像素 → DIP
        var physicalX = _bounds.Left + p.X / scale;
        var physicalY = _bounds.Top + p.Y / scale;
        foreach (var window in _windows)
        {
            if (!window.Contains((int)physicalX, (int)physicalY))
                continue;
            var rect = new Rect((window.Left - _bounds.Left) * scale, (window.Top - _bounds.Top) * scale, window.Width * scale, window.Height * scale);
            rect.Intersect(new Rect(0, 0, Layer.ActualWidth, Layer.ActualHeight));
            return rect;
        }
        return Rect.Empty;
    }

    // —— 调整大小 ——

    private bool CanAdjustSelection => _state is State.Selected or State.Moving or State.Resizing && !HasAnnotations && !Annotating;

    private Edge HitEdges(Point p)
    {
        var s = _selection;
        if (s.IsEmpty || p.X < s.Left - EdgeTolerance || p.X > s.Right + EdgeTolerance ||
            p.Y < s.Top - EdgeTolerance || p.Y > s.Bottom + EdgeTolerance)
            return Edge.None;

        var edges = Edge.None;
        if (Math.Abs(p.X - s.Left) <= EdgeTolerance)
            edges |= Edge.Left;
        else if (Math.Abs(p.X - s.Right) <= EdgeTolerance)
            edges |= Edge.Right;
        if (Math.Abs(p.Y - s.Top) <= EdgeTolerance)
            edges |= Edge.Top;
        else if (Math.Abs(p.Y - s.Bottom) <= EdgeTolerance)
            edges |= Edge.Bottom;
        return edges;
    }

    private static Cursor EdgeCursor(Edge edges) => edges switch
    {
        Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
        Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
        Edge.Left or Edge.Right => Cursors.SizeWE,
        _ => Cursors.SizeNS,
    };

    private void BeginResize(Point p, Edge edges)
    {
        _state = State.Resizing;
        _resizeEdges = edges;
        _anchor = p;
        _selectionBeforeMove = _selection;
        Toolbar.Visibility = Visibility.Collapsed;
        if (!PreviewMode)
            CaptureMouse();
    }

    /// <summary>拖过对边时自动翻转（Rect 由两点构造会自动规范化）。</summary>
    private void ResizeTo(Point p)
    {
        var q = Clamp(p);
        var b = _selectionBeforeMove;
        double left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom;
        if (_resizeEdges.HasFlag(Edge.Left)) left = q.X;
        if (_resizeEdges.HasFlag(Edge.Right)) right = q.X;
        if (_resizeEdges.HasFlag(Edge.Top)) top = q.Y;
        if (_resizeEdges.HasFlag(Edge.Bottom)) bottom = q.Y;
        _selection = new Rect(new Point(left, top), new Point(right, bottom));
        UpdateSelection();
    }

    /// <summary>方向键移动 1 像素，Shift+方向键改变宽高。</summary>
    private bool NudgeSelection(Key key, bool resize)
    {
        if (_state != State.Selected || !CanAdjustSelection)
            return false;
        var step = Layer.ActualWidth / _screen.PixelWidth; // 1 个物理像素
        var (dx, dy) = key switch
        {
            Key.Left => (-step, 0.0),
            Key.Right => (step, 0.0),
            Key.Up => (0.0, -step),
            Key.Down => (0.0, step),
            _ => (0.0, 0.0),
        };
        if (dx == 0 && dy == 0)
            return false;

        var s = _selection;
        _selection = resize
            ? new Rect(s.X, s.Y, Math.Max(MinSelection, s.Width + dx), Math.Max(MinSelection, s.Height + dy))
            : new Rect(Math.Clamp(s.X + dx, 0, Layer.ActualWidth - s.Width), Math.Clamp(s.Y + dy, 0, Layer.ActualHeight - s.Height), s.Width, s.Height);
        _selection.Intersect(new Rect(0, 0, Layer.ActualWidth, Layer.ActualHeight));
        UpdateSelection();
        ShowToolbar();
        return true;
    }

    /// <summary>四角和四边中点的控制点；可调整时显示。</summary>
    private void UpdateHandles()
    {
        var show = CanAdjustSelection && !_selection.IsEmpty;
        var s = _selection;
        var points = show
            ? new[]
            {
                new Point(s.Left, s.Top), new Point(s.Left + s.Width / 2, s.Top), new Point(s.Right, s.Top),
                new Point(s.Right, s.Top + s.Height / 2), new Point(s.Right, s.Bottom), new Point(s.Left + s.Width / 2, s.Bottom),
                new Point(s.Left, s.Bottom), new Point(s.Left, s.Top + s.Height / 2),
            }
            : Array.Empty<Point>();
        for (var i = 0; i < _handles.Length; i++)
        {
            _handles[i].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
                continue;
            Canvas.SetLeft(_handles[i], points[i].X - HandleSize / 2);
            Canvas.SetTop(_handles[i], points[i].Y - HandleSize / 2);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_editingText == null && e.Key is Key.Left or Key.Right or Key.Up or Key.Down &&
            NudgeSelection(e.Key, resize: (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
            e.Handled = true;
    }

    /// <summary>界面预览用：模拟鼠标停在某个窗口上。</summary>
    internal void PreviewHover(Rect window)
    {
        _selection = window;
        Hint.Visibility = Visibility.Collapsed;
        UpdateSelection();
    }
}
