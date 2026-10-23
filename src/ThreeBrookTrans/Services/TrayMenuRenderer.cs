using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ThreeBrookTrans.Services;

/// <summary>
/// 托盘菜单（WinForms）按三溪外观规范绘制：底 card、描边 border、悬停 accentSoft + accent 字，
/// 颜色取自当前 WPF 主题字典，随浅色 / 深色切换。
/// </summary>
public sealed class TrayMenuRenderer : ToolStripRenderer
{
    public Color Card { get; set; }
    public Color Border { get; set; }
    public Color Text { get; set; }
    public Color TextMuted { get; set; }
    public Color Accent { get; set; }
    public Color AccentSoft { get; set; }

    /// <summary>从 WPF 资源（C.Card 等）读取颜色。</summary>
    public void LoadColors()
    {
        Card = Get("C.Card");
        Border = Get("C.Border");
        Text = Get("C.Text");
        TextMuted = Get("C.TextMuted");
        Accent = Get("C.Accent");
        AccentSoft = Get("C.AccentSoft");
    }

    private static Color Get(string key)
    {
        var c = (System.Windows.Media.Color)System.Windows.Application.Current.FindResource(key);
        return Color.FromArgb(c.A, c.R, c.G, c.B);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Card);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Border);
        var r = e.ToolStrip.ClientRectangle;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
            return;
        var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(rect, 6);
        using var brush = new SolidBrush(AccentSoft);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? TextMuted : e.Item.Selected ? Accent : Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Border);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Selected == true ? Accent : TextMuted;
        base.OnRenderArrow(e);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
