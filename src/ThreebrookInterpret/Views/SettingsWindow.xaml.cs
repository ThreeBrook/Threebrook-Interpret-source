using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ThreebrookInterpret.Services;
using ThreebrookInterpret.Translation;

namespace ThreebrookInterpret.Views;

/// <summary>设置窗口：在配置副本上编辑，点"保存"后写入 settings.json 并立即生效。</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<EngineConfig> _engines;
    private readonly ObservableCollection<GlossaryEntry> _glossary;

    public SettingsWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Clone(App.Settings);
        _originalTargetLanguage = _settings.TargetLanguage;
        _engines = new ObservableCollection<EngineConfig>(_settings.Engines);
        DataContext = _settings;

        TargetBox.ItemsSource = new[] { Languages.AutoOption }.Concat(Languages.All).ToList();
        AutoStartBox.IsChecked = AutoStart.IsEnabled;
        DataDirBox.Text = SettingsStore.DataDirectory;
        foreach (var (link, url) in new[] { (ProductLink, AppInfo.ProductUrl), (CompanyLink, AppInfo.CompanyUrl) })
        {
            link.Content = url.TrimEnd('/');
            link.Tag = url;
        }
        OcrDownloadLink.Tag = AppInfo.ProductUrl;
        ApplyCodeTexts();

        var addMenu = new ContextMenu();
        foreach (var type in TranslatorFactory.Types)
        {
            var item = new MenuItem { Header = type.DisplayName, Tag = type };
            item.Click += (_, _) => AddEngine((EngineType)item.Tag);
            addMenu.Items.Add(item);
        }
        AddEngineButton.ContextMenu = addMenu;

        _glossary = new ObservableCollection<GlossaryEntry>(_settings.Glossary);
        _glossary.CollectionChanged += (_, _) => UpdateGlossaryEmpty();
        GlossaryList.ItemsSource = _glossary;
        UpdateGlossaryEmpty();

        EnginesList.ItemsSource = _engines;
        EnginesList.SelectedIndex = _engines.Count > 0 ? 0 : -1;
        UpdateEngineDetail();

        (_settings.Theme switch { "light" => ThemeLight, "dark" => ThemeDark, _ => ThemeAuto }).IsChecked = true;
        (_settings.Language switch { "zh" => LangZh, "en" => LangEn, _ => LangAuto }).IsChecked = true;
    }

    // —— 导航与外观 ——

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageAbout == null)
            return; // InitializeComponent 期间，页面还没创建
        var pages = new FrameworkElement[] { PageGeneral, PageHotkeys, PageEngines, PageGlossary, PageScreenshot, PageHistory, PageAbout };
        for (var i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == NavList.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.ScrollToTop();
    }

    /// <summary>切换外观立即预览；点"取消"或直接关闭窗口时恢复为已保存的外观。</summary>
    /// <summary>和外观一样立即预览；取消时恢复已保存的语言。</summary>
    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        _settings.Language = (string)((FrameworkElement)sender).Tag;
        Loc.Apply(_settings.Language);
        SettingsStore.LocalizeDefaultEngineNames(_settings, Loc.IsEnglish);
        EnginesList.Items.Refresh();
        ApplyCodeTexts();
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        _settings.Theme = (string)((FrameworkElement)sender).Tag;
        ThemeManager.Apply(_settings.Theme);
    }

    private bool _saved;

    private readonly string _originalTargetLanguage;
    private bool _hotkeysSuspended;

    // —— 快捷键录入期间暂停全局快捷键，否则按下已占用的组合会被截走 ——

    private void Hotkey_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_hotkeysSuspended)
            return;
        _hotkeysSuspended = true;
        App.Instance.SuspendHotkeys();
    }

    private void Hotkey_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 焦点移到另一个快捷键框时保持暂停
        if (e.NewFocus is TextBox box && IsHotkeyBox(box))
            return;
        ResumeHotkeys();
    }

    private static bool IsHotkeyBox(TextBox box) => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path
        is "InputTranslate" or "SelectionTranslate" or "Screenshot" or "ScreenshotTranslate" or "History";

    private void ResumeHotkeys()
    {
        if (!_hotkeysSuspended)
            return;
        _hotkeysSuspended = false;
        App.Instance.ResumeHotkeys();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ResumeHotkeys();
        if (!_saved)
        {
            ThemeManager.Apply();
            Loc.Apply(App.Settings.Language);
        }
    }

    private void SetTestResult(string text, string brushKey)
    {
        TestResultText.Text = text;
        TestResultText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    /// <summary>代码里设置的文字（XAML 里的随语言字典自动更新）；切换界面语言时重新设置。</summary>
    private void ApplyCodeTexts()
    {
        ProductLink.ToolTip = Loc.F("Settings.OpenInBrowser", AppInfo.ProductUrl);
        CompanyLink.ToolTip = Loc.F("Settings.OpenInBrowser", AppInfo.CompanyUrl);
        VersionText.Text = Loc.F("Settings.Version", AppInfo.Version);
        // 英文界面大标题已是英文名，下面不再重复
        AboutEnglishName.Visibility = Loc.IsEnglish ? Visibility.Collapsed : Visibility.Visible;
        var ocr = OcrLanguageBox.SelectedValue;
        LoadOcrLanguages();
        OcrLanguageBox.SelectedValue = ocr ?? _settings.Screenshot.OcrLanguage;
        UpdateOcrComponent();
        TargetBox.Items.Refresh();
        UpdateEngineDetail();
    }

    private void LoadOcrLanguages()
    {
        var languages = new[] { new OcrLanguage("auto", Loc.T("Settings.OcrAuto")) }.ToList();
        try
        {
            languages.AddRange(WindowsOcr.GetAvailableLanguages());
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
        }
        OcrLanguageBox.ItemsSource = languages;
    }

    // —— 快捷键录入 ——

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var box = (TextBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return; // 等待主键

        var modifiers = Keyboard.Modifiers;
        string gesture;
        if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete)
            gesture = "";
        else if (modifiers == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24))
            return; // 普通字符键必须配合修饰键，避免影响正常输入
        else
            gesture = FormatGesture(modifiers, key);

        box.Text = gesture;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private static string FormatGesture(ModifierKeys modifiers, Key key)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
        return string.Join("+", parts);
    }

    // —— 翻译引擎 ——

    private EngineConfig? SelectedEngine => EnginesList.SelectedItem as EngineConfig;

    /// <summary>点复选框时也选中这一行（直接设置选中项，焦点移到右侧输入框后不会被撤销）。</summary>
    private void EngineCheckBox_Click(object sender, RoutedEventArgs e) =>
        EnginesList.SelectedItem = ((FrameworkElement)sender).DataContext;

    private void EnginesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEngineDetail();

    private void UpdateEngineDetail()
    {
        var engine = SelectedEngine;
        EngineDetail.Visibility = engine == null ? Visibility.Collapsed : Visibility.Visible;
        TestResultText.Text = "";
        if (engine == null)
            return;

        EngineDetail.DataContext = engine;
        var type = TranslatorFactory.Types.FirstOrDefault(t => t.Type == engine.Type);
        EngineTypeText.Text = type?.DisplayName ?? engine.Type;
        EngineHelpText.Text = type?.Help ?? "";

        var fields = (type?.Fields ?? "").Split(',');
        void Show(FrameworkElement row, string field) =>
            row.Visibility = fields.Contains(field) ? Visibility.Visible : Visibility.Collapsed;
        Show(AppIdRow, "AppId");
        Show(SecretKeyRow, "SecretKey");
        Show(BaseUrlRow, "BaseUrl");
        Show(ApiKeyRow, "ApiKey");
        Show(RegionRow, "Region");
        Show(ModelRow, "Model");
        Show(LlmRows, "Llm");
        AppIdLabel.Text = engine.Type == "tencent" ? "SecretId" : "AppId";
    }

    private void AddEngine_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.ContextMenu!.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void AddEngine(EngineType type)
    {
        var engine = new EngineConfig
        {
            Id = $"{type.Type}-{Guid.NewGuid().ToString("N")[..6]}",
            Type = type.Type,
            Name = type.DisplayName.Split('（', '(')[0].Trim(),
            Enabled = true,
        };
        if (type.Type == "openai")
        {
            engine.Name = Loc.T("Engine.LocalModel");
            engine.BaseUrl = "http://localhost:11434/v1";
        }
        _engines.Add(engine);
        EnginesList.SelectedItem = engine;
    }

    private void RemoveEngine_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEngine is { } engine)
            _engines.Remove(engine);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int offset)
    {
        var index = EnginesList.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _engines.Count)
            return;
        _engines.Move(index, target);
        EnginesList.SelectedIndex = target;
    }

    private void EngineName_LostFocus(object sender, RoutedEventArgs e) => EnginesList.Items.Refresh();

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEngine is not { } engine)
            return;

        SetTestResult(Loc.T("Settings.FetchingModels"), "B.TextMuted");
        try
        {
            var models = await OpenAiCompatibleTranslator.ListModelsAsync(engine, CancellationToken.None);
            if (models.Count == 0)
            {
                SetTestResult(Loc.T("Settings.NoModels"), "B.TextMuted");
                return;
            }
            if (models.Count == 1)
            {
                SetModel(models[0]);
                SetTestResult(Loc.F("Settings.OneModel", models[0]), "B.Success");
                return;
            }

            // 多个模型时弹出菜单选择
            var menu = new ContextMenu { PlacementTarget = FetchModelsButton };
            foreach (var model in models)
            {
                var item = new MenuItem { Header = model, IsChecked = model == engine.Model };
                item.Click += (_, _) => SetModel(model);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
            SetTestResult(Loc.F("Settings.ManyModels", models.Count), "B.TextSub");
        }
        catch (Exception ex)
        {
            SetTestResult("✗ " + ex.Message, "B.Danger");
        }
    }

    private void SetModel(string model)
    {
        ModelBox.Text = model;
        ModelBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private async void TestEngine_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEngine is not { } engine || TranslatorFactory.Create(engine) is not { } translator)
            return;

        SetTestResult(Loc.T("Common.Translating"), "B.TextMuted");
        try
        {
            var result = await translator.TranslateAsync("Hello, this is a translation test.", Languages.Auto, "zh", CancellationToken.None);
            SetTestResult("✓ " + result, "B.Success");
        }
        catch (Exception ex)
        {
            SetTestResult("✗ " + ex.Message, "B.Danger");
        }
    }

    // —— 术语表 ——

    private void AddTerm_Click(object sender, RoutedEventArgs e) => _glossary.Add(new GlossaryEntry());

    private void RemoveTerm_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is GlossaryEntry entry)
            _glossary.Remove(entry);
    }

    private void UpdateGlossaryEmpty() =>
        GlossaryEmpty.Visibility = _glossary.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // —— 其他 ——

    private void BrowseSaveDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Settings.ChooseSaveFolder") };
        if (dialog.ShowDialog(this) == true)
        {
            SaveDirBox.Text = dialog.FolderName;
            SaveDirBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
    }

    // —— 高精度识别组件 ——

    private void UpdateOcrComponent()
    {
        var directory = OcrComponent.FindDirectory();
        OcrComponentStatus.Text = directory == null
            ? Loc.T("Settings.OcrComponentMissing")
            : Loc.F("Settings.OcrComponentInstalled", OcrComponent.Version(directory) ?? "");
        // 只能删数据目录里装的；程序旁边手动放的 ocr 文件夹由用户自己删
        OcrRemoveButton.Visibility = directory != null && Path.GetFullPath(directory).TrimEnd('\\')
            .Equals(Path.GetFullPath(OcrComponent.InstallDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InstallOcr_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Settings.OcrZipFilter") };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            var applied = OcrComponent.Install(dialog.FileName);
            MessageBox.Show(this, Loc.T(applied ? "Settings.OcrInstalled" : "Settings.OcrInstalledRestart"), AppInfo.Name,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, Loc.F("Settings.OcrInstallFailed", ex.Message), AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateOcrComponent();
    }

    private void RemoveOcr_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Loc.T("Settings.OcrConfirmRemove"), AppInfo.Name, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try
        {
            if (!OcrComponent.Remove())
                MessageBox.Show(this, Loc.T("Settings.OcrRemoveRestart"), AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateOcrComponent();
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo((string)((FrameworkElement)sender).Tag) { UseShellExecute = true });

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start("explorer.exe", $"\"{SettingsStore.DataDirectory}\"");

    private void OpenHistoryFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(ClipboardHistory.Directory);
        Process.Start("explorer.exe", $"\"{ClipboardHistory.Directory}\"");
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Loc.T("Settings.ConfirmClearHistory"), AppInfo.Name, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            App.Instance.ClearHistory();
    }

    /// <summary>设置窗口是非模态的，IsCancel 不会自动关闭，需要手动关闭（编辑的是副本，直接丢弃）。</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 让当前焦点输入框的内容先写回
        if (Keyboard.FocusedElement is TextBox focused)
            focused.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        _settings.Engines = _engines.ToList();
        _settings.Glossary = _glossary
            .Where(g => !string.IsNullOrWhiteSpace(g.Source) && !string.IsNullOrWhiteSpace(g.Target))
            .Select(g => new GlossaryEntry { Source = g.Source.Trim(), Target = g.Target.Trim() })
            .ToList();

        // 快捷键不能重复
        var hotkeys = new (string Name, string Gesture)[]
        {
            (Loc.T("Common.InputTranslate"), _settings.Hotkeys.InputTranslate), (Loc.T("Common.SelectionTranslate"), _settings.Hotkeys.SelectionTranslate),
            (Loc.T("Common.Screenshot"), _settings.Hotkeys.Screenshot), (Loc.T("Common.ScreenshotTranslate"), _settings.Hotkeys.ScreenshotTranslate),
            (Loc.T("Common.ClipboardHistory"), _settings.Hotkeys.History),
        };
        var duplicate = hotkeys.Where(h => !string.IsNullOrWhiteSpace(h.Gesture))
            .GroupBy(h => HotkeyManager.Normalize(h.Gesture)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            NavList.SelectedIndex = 1;
            MessageBox.Show(this, Loc.F("Settings.DuplicateHotkey", string.Join(Loc.T("Common.ListSeparator"), duplicate.Select(h => h.Name)), duplicate.First().Gesture),
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 设置窗口打开期间在翻译窗口换过目标语言、而这里没改时，保留翻译窗口的选择
        if (_settings.TargetLanguage == _originalTargetLanguage)
            _settings.TargetLanguage = App.Settings.TargetLanguage;
        _settings.History.MaxItems = Math.Max(10, _settings.History.MaxItems);
        _settings.History.MaxDays = Math.Max(1, _settings.History.MaxDays);

        try
        {
            if (AutoStartBox.IsChecked != AutoStart.IsEnabled)
                AutoStart.Set(AutoStartBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.F("Settings.AutoStartFailed", ex.Message), AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _saved = true;
        App.Instance.ApplySettings(_settings);
        Close();
    }
}
