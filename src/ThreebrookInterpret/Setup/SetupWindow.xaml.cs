using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ThreebrookInterpret.Services;

namespace ThreebrookInterpret.Setup;

/// <summary>
/// 安装 / 卸载向导。安装：欢迎（含许可协议）→ 位置和选项 → 安装中 → 装好了，出错页和已取消页；
/// 卸载：确认 → 卸载中 → 卸载好了。真正改文件的是 SetupEngine，在后台线程运行，这里只显示进度。
/// </summary>
public partial class SetupWindow : Window
{
    private static string[] InstallSteps => [Loc.T("Setup.StepWelcome"), Loc.T("Setup.StepOptions"), Loc.T("Setup.StepInstall"), Loc.T("Setup.StepDone")];
    private static string[] UninstallSteps => [Loc.T("Setup.StepConfirm"), Loc.T("Setup.StepUninstall"), Loc.T("Setup.StepDone")];

    private readonly SetupContext _context;
    private readonly SetupPayload? _payload;
    private readonly ExistingInstall? _existing;
    private readonly string _uninstallDirectory = "";
    private readonly bool _uninstall;
    private readonly HotkeySettings _hotkeys = LoadHotkeys();
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Grid? _backPage;
    private string _installedExe = "";
    private double _target, _shown;
    private CancellationTokenSource? _cancel;
    private bool _running, _closeWhenDone;

    /// <summary>安装。</summary>
    internal SetupWindow(SetupPayload payload, SetupContext context) : this(context, uninstall: false)
    {
        _payload = payload;
        if (payload.Files.Any(f => f.Path.StartsWith(OcrComponent.FolderName + "\\", StringComparison.OrdinalIgnoreCase)))
            ShowFullEdition();
        _existing = SetupEngine.FindExisting(context);
        DirBox.Text = _existing?.Directory ?? context.DefaultDirectory;
        if (_existing != null)
        {
            DesktopBox.IsChecked = _existing.Desktop;
            AutoStartBox.IsChecked = _existing.AutoStart || context.Test;
        }
        ShowWelcome();
    }

    /// <summary>卸载。</summary>
    internal SetupWindow(string directory, SetupContext context) : this(context, uninstall: true)
    {
        _uninstallDirectory = directory;
        UninstallDirText.Text = directory;
        DataDirText.Text = context.Test ? Loc.T("Setup.TestNoData") : Loc.F("Setup.DataLocation", SettingsStore.InstalledDataDirectory);
        ShowConfirm();
    }

    private SetupWindow(SetupContext context, bool uninstall)
    {
        InitializeComponent();
        _context = context;
        _uninstall = uninstall;
        var suffix = context.Test ? Loc.T("Setup.TestSuffix") : "";
        Title = Loc.F(uninstall ? "Setup.UninstallWindowTitle" : "Setup.InstallWindowTitle", AppInfo.Name) + suffix;
        Bar.Title = Title;
        ProductNameText.Text = AppInfo.Name;
        HeadingText.FontSize = Loc.IsEnglish ? 25 : 30;
        VersionText.Text = Loc.IsEnglish ? Loc.F("Settings.Version", AppInfo.Version) : $"{AppInfo.EnglishName} {AppInfo.Version}";
        FooterText.Text = uninstall ? Loc.T("Setup.FooterUninstall") : Loc.T("Setup.FooterInstall");
        HotkeyFeatureText.Text = string.Join(" · ", new[]
        {
            Describe(_hotkeys.SelectionTranslate, Loc.T("Setup.FeatureSelection")), Describe(_hotkeys.InputTranslate, Loc.T("Setup.FeatureInput")), Describe(_hotkeys.ScreenshotTranslate, Loc.T("Setup.FeatureScreenshot")),
        }.Where(s => s != null));
        _animation.Tick += (_, _) => Animate();
        ProgressTrack.SizeChanged += (_, _) => UpdateProgressBar();
        Closing += OnClosing;
    }

    // —— 页面 ——

    private IEnumerable<Grid> Pages => [WelcomePage, OptionsPage, LicensePage, ProgressPage, DonePage, ErrorPage, CancelledPage, ConfirmPage, UninstalledPage];

    private void ShowPage(Grid page, string heading, string description, string tip, int step)
    {
        foreach (var p in Pages)
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        HeadingText.Text = heading;
        DescText.Text = description;
        TipText.Text = tip;
        BuildSteps(step);
    }

    private void BuildSteps(int current)
    {
        StepsPanel.Children.Clear();
        var steps = _uninstall ? UninstallSteps : InstallSteps;
        for (var i = 0; i < steps.Length; i++)
        {
            var active = i == current;
            var done = i < current;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var marker = new TextBlock
            {
                Style = (Style)FindResource("Text.Icon"),
                FontSize = 15,
                Width = 24,
                Text = done ? (string)FindResource("I.Check") : "",
            };
            if (active)
            {
                marker.FontFamily = (System.Windows.Media.FontFamily)FindResource("F.Body");
                marker.Text = "●";
                marker.FontSize = 10;
                marker.Margin = new Thickness(3, 0, 0, 0);
                marker.Width = 21;
            }
            else if (!done)
            {
                marker.FontFamily = (System.Windows.Media.FontFamily)FindResource("F.Body");
                marker.Text = "○";
                marker.FontSize = 10;
                marker.Margin = new Thickness(3, 0, 0, 0);
                marker.Width = 21;
            }
            marker.SetResourceReference(TextBlock.ForegroundProperty, active || done ? "B.Accent" : "B.TextMuted");
            var label = new TextBlock { Text = steps[i], FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, active ? "B.Text" : done ? "B.TextSub" : "B.TextMuted");
            if (active)
                label.FontWeight = FontWeights.Bold;
            row.Children.Add(marker);
            row.Children.Add(label);
            StepsPanel.Children.Add(row);
        }
    }

    private static string WelcomeTip => Loc.T("Setup.WelcomeTip");

    private void ShowWelcome()
    {
        var description = _existing != null
            ? _existing.Version == AppInfo.Version
                ? Loc.F("Setup.WelcomeSame", AppInfo.Version)
                : Loc.F("Setup.WelcomeUpgrade", _existing.Version, AppInfo.Version)
            : Loc.T("Setup.WelcomeDesc");
        ShowPage(WelcomePage, Loc.T("Setup.WelcomeTitle"), description, WelcomeTip, 0);
        WelcomePathText.Text = SelectedDirectory() ?? DirBox.Text;
        WelcomeSizeText.Text = Loc.F("Setup.NeedsPrefix", SetupEngine.FormatSize(_payload?.TotalSize ?? 0));
    }

    private void ShowOptions()
    {
        ShowPage(OptionsPage, Loc.T("Setup.OptionsTitle"), Loc.T("Setup.OptionsDesc"), Loc.T("Setup.OptionsTip"), 1);
        UpdateSpace();
    }

    private void ShowConfirm() =>
        ShowPage(ConfirmPage, Loc.T("Setup.ConfirmTitle"), Loc.T("Setup.ConfirmDesc"), Loc.T("Setup.ConfirmTip"), 0);

    private void Options_Click(object sender, RoutedEventArgs e) => ShowOptions();

    private void License_Click(object sender, RoutedEventArgs e)
    {
        _backPage = WelcomePage;
        if (string.IsNullOrEmpty(LicenseText.Text))
        {
            using var stream = typeof(SetupWindow).Assembly.GetManifestResourceStream(Loc.IsEnglish ? "ThreebrookInterpret.Setup.License.en.txt" : "ThreebrookInterpret.Setup.License.txt");
            LicenseText.Text = stream == null ? "" : new StreamReader(stream).ReadToEnd();
        }
        ShowPage(LicensePage, Loc.T("Setup.LicenseTitle"), Loc.T("Setup.LicenseDesc"), WelcomeTip, 0);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowWelcome();

    private void Restart_Click(object sender, RoutedEventArgs e) => ShowWelcome();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // —— 位置和选项 ——

    /// <summary>选项页里填的位置；不是完整路径时返回 null。</summary>
    private string? SelectedDirectory()
    {
        var text = DirBox.Text.Trim().Trim('"');
        try
        {
            return Path.IsPathFullyQualified(text) ? Path.GetFullPath(text).TrimEnd('\\') : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Setup.ChooseFolder") };
        var current = SelectedDirectory();
        if (current != null)
            dialog.InitialDirectory = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
        if (dialog.ShowDialog(this) == true)
            DirBox.Text = SetupEngine.NormalizeDirectory(dialog.FolderName, _context);
    }

    private void DirBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
            UpdateSpace();
    }

    private void UpdateSpace()
    {
        var size = SetupEngine.FormatSize(_payload?.TotalSize ?? 0);
        var directory = SelectedDirectory();
        if (directory == null)
        {
            SpaceText.Text = Loc.F("Setup.FullPathNeeded", _context.DefaultDirectory);
            SpaceText.SetResourceReference(TextBlock.ForegroundProperty, "B.Danger");
            return;
        }
        SpaceText.SetResourceReference(TextBlock.ForegroundProperty, "B.TextMuted");
        var free = SetupEngine.FreeSpace(directory);
        SpaceText.Text = free >= 0 ? Loc.F("Setup.NeedsFree", size, SetupEngine.FormatSize(free)) : Loc.F("Setup.Needs", size);
    }

    // —— 安装 ——

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_payload == null)
            return;
        var directory = SelectedDirectory();
        if (directory == null)
        {
            ShowOptions();
            return;
        }
        var options = new InstallOptions(directory, DesktopBox.IsChecked == true, AutoStartBox.IsChecked == true);
        _installedExe = Path.Combine(directory, SetupEngine.ExeName);

        ShowPage(ProgressPage, Loc.T("Setup.InstallingTitle"), Loc.T("Setup.Moment"), Loc.T("Setup.InstallingTip"), 2);
        AbortButton.Visibility = Visibility.Visible;
        AbortButton.IsEnabled = true;
        ProgressTip.Visibility = Visibility.Visible;
        ProgressTipText.Text = Loc.F("Setup.ProgressHint", _hotkeys.SelectionTranslate);
        _cancel = new CancellationTokenSource();
        var cancel = _cancel.Token;
        var error = await Run((progress, _) => SetupEngine.Install(_payload, options, _context, progress, cancel));
        _cancel.Dispose();
        _cancel = null;

        if (error is OperationCanceledException)
        {
            if (_closeWhenDone)
            {
                Close();
                return;
            }
            ShowPage(CancelledPage, Loc.T("Setup.CancelledTitle"),
                _existing != null ? Loc.T("Setup.CancelledKept") : Loc.T("Setup.CancelledClean"),
                Loc.T("Setup.CancelledTip"), 2);
        }
        else if (error != null)
        {
            ShowError(Loc.T("Setup.FailedTitle"),
                _existing != null ? Loc.T("Setup.FailedKept") : Loc.T("Setup.FailedClean"), error);
        }
        else
        {
            HotkeyList.ItemsSource = new[]
            {
                (Loc.T("Common.InputTranslate"), _hotkeys.InputTranslate), (Loc.T("Common.SelectionTranslate"), _hotkeys.SelectionTranslate), (Loc.T("Common.Screenshot"), _hotkeys.Screenshot),
                (Loc.T("Common.ScreenshotTranslate"), _hotkeys.ScreenshotTranslate), (Loc.T("Common.ClipboardHistory"), _hotkeys.History),
            }.Where(h => !string.IsNullOrWhiteSpace(h.Item2)).Select(h => new KeyValuePair<string, string>(h.Item1, h.Item2)).ToList();
            ShowPage(DonePage, Loc.T("Setup.DoneTitle"), Loc.T("Setup.DoneDesc"),
                Loc.T("Setup.DoneTip"), 3);
            if (_closeWhenDone)
                Close();
        }
    }

    private void Abort_Click(object sender, RoutedEventArgs e)
    {
        _cancel?.Cancel();
        AbortButton.IsEnabled = false;
        FileText.Text = Loc.T("Setup.RollingBack");
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_uninstall)
            Uninstall_Click(sender, e);
        else
            Install_Click(sender, e);
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchBox.IsChecked == true && File.Exists(_installedExe))
        {
            try
            {
                // --welcome：第一次打开时弹出设置窗口，不然只进托盘，容易以为没装好
                var start = new ProcessStartInfo(_installedExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_installedExe) };
                start.ArgumentList.Add("--welcome");
                Process.Start(start);
            }
            catch (Win32Exception ex)
            {
                MessageBox.Show(this, Loc.F("Setup.LaunchFailed", ex.Message), AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        Close();
    }

    // —— 卸载 ——

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var deleteData = DeleteDataBox.IsChecked == true;
        ShowPage(ProgressPage, Loc.T("Setup.UninstallingTitle"), Loc.T("Setup.Moment"), Loc.T("Setup.UninstallingTip"), 1);
        AbortButton.Visibility = Visibility.Collapsed;
        ProgressTip.Visibility = Visibility.Collapsed;
        var error = await Run((progress, _) => SetupEngine.Uninstall(_uninstallDirectory, deleteData, _context, progress));
        if (error != null)
            ShowError(Loc.T("Setup.UninstallFailedTitle"), Loc.T("Setup.UninstallFailedDesc"), error);
        else
            ShowPage(UninstalledPage, Loc.T("Setup.UninstalledTitle"),
                deleteData && !_context.Test ? Loc.T("Setup.UninstalledAll") : Loc.T("Setup.UninstalledKept"),
                Loc.T("Setup.Thanks"), 2);
    }

    // —— 进度 ——

    /// <summary>在后台线程运行引擎，进度条平滑追上实际进度；文件很小时也让进度页停留片刻，不会一闪而过。</summary>
    private async Task<Exception?> Run(Action<IProgress<SetupProgress>, CancellationToken> work)
    {
        _running = true;
        _target = _shown = 0;
        UpdateProgressBar();
        FileText.Text = "";
        _animation.Start();
        var progress = new Progress<SetupProgress>(p =>
        {
            if (p.Fraction < 0)
            {
                FileText.Text = Loc.T("Setup.RollingBack");
                return;
            }
            _target = Math.Max(_target, p.Fraction);
            if (!string.IsNullOrEmpty(p.File))
                FileText.Text = p.File;
        });
        Exception? error = null;
        try
        {
            await Task.Run(() => work(progress, CancellationToken.None));
            _target = 1;
            while (_shown < 1)
                await Task.Delay(30);
            await Task.Delay(250);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        _animation.Stop();
        _running = false;
        return error;
    }

    private void Animate()
    {
        if (_shown >= _target)
            return;
        _shown = Math.Min(_target, _shown + 0.02);
        UpdateProgressBar();
    }

    private void UpdateProgressBar()
    {
        PercentText.Text = $"{(int)Math.Round(_shown * 100)} %";
        ProgressFill.Width = ProgressTrack.ActualWidth * _shown;
    }

    private void ShowError(string heading, string description, Exception error)
    {
        ErrorText.Text = error is SetupException or InvalidDataException ? error.Message : $"{error.GetType().Name}: {error.Message}";
        ShowPage(ErrorPage, heading, description, Loc.T("Setup.ErrorTip"), _uninstall ? 1 : 2);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_running)
            return;
        // 安装中点关闭 = 中止：撤回完再关；卸载不能中途停
        e.Cancel = true;
        if (!_uninstall && _cancel != null)
        {
            _closeWhenDone = true;
            Abort_Click(this, new RoutedEventArgs());
        }
    }

    // —— 工具 ——

    private static string? Describe(string gesture, string name) => string.IsNullOrWhiteSpace(gesture) ? null : $"{gesture} {name}";

    /// <summary>覆盖安装时按用户改过的快捷键显示；只读配置，不写。</summary>
    private static HotkeySettings LoadHotkeys()
    {
        try
        {
            if (File.Exists(SettingsStore.SettingsPath))
                return SettingsStore.Load().Hotkeys;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }
        return new HotkeySettings();
    }

    // —— 界面预览 ——

    /// <summary>完整版安装包（带高精度识别组件）：欢迎页说明识别用的是哪个。</summary>
    private void ShowFullEdition()
    {
        OcrFeatureText.SetResourceReference(TextBlock.TextProperty, "S.Setup.Feature2SubFull");
        VersionText.Text += "\n" + Loc.T("Setup.FullEdition"); // 左栏窄，单独一行
    }

    internal static SetupWindow CreatePreview(bool uninstall, bool full = false)
    {
        var window = uninstall
            ? new SetupWindow(@"C:\Users\你\AppData\Local\Programs\ThreebrookInterpret", new SetupContext(false))
            : new SetupWindow(new SetupContext(false), uninstall: false);
        if (full)
            window.ShowFullEdition();
        return window;
    }

    /// <summary>预览用：直接显示某一页（示例数据）。</summary>
    internal void ShowPreviewPage(string page)
    {
        DirBox.Text = @"C:\Users\你\AppData\Local\Programs\ThreebrookInterpret";
        WelcomePathText.Text = DirBox.Text;
        WelcomeSizeText.Text = Loc.F("Setup.NeedsPrefix", "1 MB");
        switch (page)
        {
            case "welcome":
                ShowPage(WelcomePage, Loc.T("Setup.WelcomeTitle"), Loc.T("Setup.WelcomeDesc"), WelcomeTip, 0);
                break;
            case "options":
                ShowPage(OptionsPage, Loc.T("Setup.OptionsTitle"), Loc.T("Setup.OptionsDesc"), Loc.T("Setup.OptionsTip"), 1);
                SpaceText.Text = Loc.F("Setup.NeedsFree", "1 MB", "182 GB");
                break;
            case "license":
                License_Click(this, new RoutedEventArgs());
                break;
            case "progress":
                ShowPage(ProgressPage, Loc.T("Setup.InstallingTitle"), Loc.T("Setup.Moment"), Loc.T("Setup.InstallingTip"), 2);
                ProgressTipText.Text = Loc.F("Setup.ProgressHint", "Alt+D");
                FileText.Text = "ThreebrookInterpret.exe";
                UpdateLayout();
                _shown = 0.64;
                UpdateProgressBar();
                break;
            case "done":
                HotkeyList.ItemsSource = new[]
                {
                    new KeyValuePair<string, string>(Loc.T("Common.InputTranslate"), "Alt+Q"), new(Loc.T("Common.SelectionTranslate"), "Alt+D"), new(Loc.T("Common.Screenshot"), "Alt+S"),
                    new(Loc.T("Common.ScreenshotTranslate"), "Alt+W"), new(Loc.T("Common.ClipboardHistory"), "Alt+H"),
                };
                ShowPage(DonePage, Loc.T("Setup.DoneTitle"), Loc.T("Setup.DoneDesc"),
                    Loc.T("Setup.DoneTip"), 3);
                break;
            case "error":
                ShowError(Loc.T("Setup.FailedTitle"), Loc.T("Setup.FailedKept"),
                    new SetupException(Loc.F("Setup.DiskFull", "2 MB", "D:", "1 MB")));
                break;
            case "cancelled":
                ShowPage(CancelledPage, Loc.T("Setup.CancelledTitle"), Loc.T("Setup.CancelledClean"), Loc.T("Setup.CancelledTip"), 2);
                break;
            case "confirm":
                ShowConfirm();
                break;
            case "uprogress":
                ShowPage(ProgressPage, Loc.T("Setup.UninstallingTitle"), Loc.T("Setup.Moment"), Loc.T("Setup.UninstallingTip"), 1);
                AbortButton.Visibility = Visibility.Collapsed;
                ProgressTip.Visibility = Visibility.Collapsed;
                FileText.Text = @"licenses\OFL-ZCOOLXiaoWei.txt";
                UpdateLayout();
                _shown = 0.5;
                UpdateProgressBar();
                break;
            case "udone":
                ShowPage(UninstalledPage, Loc.T("Setup.UninstalledTitle"), Loc.T("Setup.UninstalledKept"), Loc.T("Setup.Thanks"), 2);
                break;
        }
    }
}
