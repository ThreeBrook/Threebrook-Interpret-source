using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Views;

public partial class TitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(TitleBar), new PropertyMetadata("", (d, _) => ((TitleBar)d).UpdateTitle()));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(TitleBar));

    public static readonly DependencyProperty TitleExtraProperty = DependencyProperty.Register(
        nameof(TitleExtra), typeof(object), typeof(TitleBar));

    public static readonly DependencyProperty ShowMinimizeProperty = DependencyProperty.Register(
        nameof(ShowMinimize), typeof(bool), typeof(TitleBar),
        new PropertyMetadata(false, (d, e) => ((TitleBar)d).MinimizeButton.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public static readonly DependencyProperty CloseToolTipProperty = DependencyProperty.Register(
        nameof(CloseToolTip), typeof(string), typeof(TitleBar), new PropertyMetadata("关闭"));

    public TitleBar() => InitializeComponent();

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>右侧操作区（关闭按钮左边）。</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    /// <summary>紧跟标题的附加内容（如计数）。</summary>
    public object? TitleExtra { get => GetValue(TitleExtraProperty); set => SetValue(TitleExtraProperty, value); }

    public bool ShowMinimize { get => (bool)GetValue(ShowMinimizeProperty); set => SetValue(ShowMinimizeProperty, value); }
    public string CloseToolTip { get => (string)GetValue(CloseToolTipProperty); set => SetValue(CloseToolTipProperty, value); }

    /// <summary>规范：中文标题用站酷小薇且字号 +2，英文用 Bricolage Grotesque 粗体。</summary>
    private void UpdateTitle()
    {
        var chinese = Title.Any(TextUtil.IsCjk);
        TitleText.Text = Title;
        TitleText.FontFamily = (System.Windows.Media.FontFamily)FindResource(chinese ? "F.DisplayZh" : "F.DisplayEn");
        TitleText.FontSize = chinese ? 16 : 14;
    }

    private void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 按钮、下拉框会先处理并标记事件，到这里的只有标题栏空白处
        if (e.ButtonState == MouseButtonState.Pressed)
            Window.GetWindow(this)?.DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
            window.WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
