using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ThreeBrookTrans.Services;
using ThreeBrookTrans.Translation;

namespace ThreeBrookTrans.Views;

/// <summary>设置窗口：在配置副本上编辑，点"保存"后写入 settings.json 并立即生效。</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<EngineConfig> _engines;

    public SettingsWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Clone(App.Settings);
        _engines = new ObservableCollection<EngineConfig>(_settings.Engines);
        DataContext = _settings;

        TargetBox.ItemsSource = new[] { new Language(Languages.Auto, "自动（中⇄英）", "") }.Concat(Languages.All).ToList();
        AutoStartBox.IsChecked = AutoStart.IsEnabled;
        DataDirBox.Text = SettingsStore.DataDirectory;
        VersionText.Text = $"ThreeBrook Trans {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

        LoadOcrLanguages();

        var addMenu = new ContextMenu();
        foreach (var type in TranslatorFactory.Types)
        {
            var item = new MenuItem { Header = type.DisplayName, Tag = type };
            item.Click += (_, _) => AddEngine((EngineType)item.Tag);
            addMenu.Items.Add(item);
        }
        AddEngineButton.ContextMenu = addMenu;

        EnginesList.ItemsSource = _engines;
        EnginesList.SelectedIndex = _engines.Count > 0 ? 0 : -1;
        UpdateEngineDetail();
    }

    private void LoadOcrLanguages()
    {
        var languages = new[] { new OcrLanguage("auto", "跟随系统语言") }.ToList();
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
            Name = type.DisplayName.Split('（')[0],
            Enabled = true,
        };
        if (type.Type == "openai")
        {
            engine.Name = "本地大模型";
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

        TestResultText.Foreground = System.Windows.Media.Brushes.Gray;
        TestResultText.Text = "正在获取模型列表…";
        try
        {
            var models = await OpenAiCompatibleTranslator.ListModelsAsync(engine, CancellationToken.None);
            if (models.Count == 0)
            {
                TestResultText.Text = "接口没有返回任何模型";
                return;
            }
            if (models.Count == 1)
            {
                SetModel(models[0]);
                TestResultText.Text = $"已填入唯一的模型：{models[0]}";
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
            TestResultText.Text = $"共 {models.Count} 个模型，请选择";
        }
        catch (Exception ex)
        {
            TestResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            TestResultText.Text = "✗ " + ex.Message;
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

        TestResultText.Foreground = System.Windows.Media.Brushes.Gray;
        TestResultText.Text = "翻译中…";
        try
        {
            var result = await translator.TranslateAsync("Hello, this is a translation test.", Languages.Auto, "zh", CancellationToken.None);
            TestResultText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            TestResultText.Text = "✓ " + result;
        }
        catch (Exception ex)
        {
            TestResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            TestResultText.Text = "✗ " + ex.Message;
        }
    }

    // —— 其他 ——

    private void BrowseSaveDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择截图保存目录" };
        if (dialog.ShowDialog(this) == true)
        {
            SaveDirBox.Text = dialog.FolderName;
            SaveDirBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start("explorer.exe", $"\"{SettingsStore.DataDirectory}\"");

    private void OpenHistoryFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(ClipboardHistory.Directory);
        Process.Start("explorer.exe", $"\"{ClipboardHistory.Directory}\"");
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "确定清空全部剪贴板历史吗？", "ThreeBrook Trans", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
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
        _settings.History.MaxItems = Math.Max(10, _settings.History.MaxItems);
        _settings.History.MaxDays = Math.Max(1, _settings.History.MaxDays);

        try
        {
            if (AutoStartBox.IsChecked != AutoStart.IsEnabled)
                AutoStart.Set(AutoStartBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "设置开机自启失败：" + ex.Message, "ThreeBrook Trans", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        App.Instance.ApplySettings(_settings);
        Close();
    }
}
