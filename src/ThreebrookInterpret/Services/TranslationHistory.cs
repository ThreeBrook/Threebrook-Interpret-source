using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ThreebrookInterpret.Services;

/// <summary>一次翻译：原文、目标语言和各引擎成功的译文。</summary>
public sealed class TranslationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public string Source { get; set; } = "";

    /// <summary>目标语言代码，如 zh / en。</summary>
    public string Target { get; set; } = "";

    public List<TranslationResultRecord> Results { get; set; } = [];

    /// <summary>列表里显示的第一条译文。</summary>
    [JsonIgnore] public string Preview => Results.Count > 0 ? Results[0].Text : "";

    [JsonIgnore] public string TargetName => Translation.Languages.Get(Target).DisplayName;
}

public sealed class TranslationResultRecord
{
    public string Engine { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
/// 翻译历史：每次翻译完成后记下原文和译文，只存在本机（数据目录的 translations.json）。
/// 同样的原文译成同一种语言再翻一次时，更新那条记录并移到最前。
/// </summary>
public sealed class TranslationHistory : IDisposable
{
    /// <summary>原文最多保存的字数，避免整篇长文把索引撑大。</summary>
    private const int MaxSourceLength = 20_000;

    private static string FilePath => Path.Combine(SettingsStore.DataDirectory, "translations.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly bool _persist;
    private Task _saving = Task.CompletedTask;

    public ObservableCollection<TranslationRecord> Entries { get; } = new();

    public TranslationHistory() : this(persist: true)
    {
    }

    /// <summary>界面预览用：不读写本机文件。</summary>
    internal static TranslationHistory CreatePreview() => new(persist: false);

    private TranslationHistory(bool persist)
    {
        _persist = persist;
        if (!persist)
            return;
        Load();
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };
    }

    private static TranslationHistorySettings Settings => App.Settings.TranslationHistory;

    /// <summary>记录一次翻译（没有成功的译文、或关闭了翻译历史时不记）。</summary>
    public void Add(string source, string target, IEnumerable<(string Engine, string Text)> results)
    {
        var list = results.Where(r => r.Text.Length > 0).Select(r => new TranslationResultRecord { Engine = r.Engine, Text = r.Text }).ToList();
        if (!Settings.Enabled || list.Count == 0 || string.IsNullOrWhiteSpace(source))
            return;
        if (source.Length > MaxSourceLength)
            source = source[..MaxSourceLength];

        var existing = Entries.FirstOrDefault(e => e.Target == target && e.Source == source);
        if (existing != null)
            Entries.Remove(existing);
        Entries.Insert(0, new TranslationRecord { Id = existing?.Id ?? Guid.NewGuid().ToString("N"), Source = source, Target = target, Results = list });
        Trim();
        ScheduleSave();
    }

    public void Delete(TranslationRecord record)
    {
        Entries.Remove(record);
        ScheduleSave();
    }

    public void Clear()
    {
        Entries.Clear();
        Save();
    }

    /// <summary>按条数和天数清理旧记录。</summary>
    public void Trim()
    {
        var cutoff = DateTime.Now.AddDays(-Math.Max(1, Settings.MaxDays));
        var max = Math.Max(10, Settings.MaxItems);
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (i >= max || Entries[i].Time < cutoff)
                Entries.RemoveAt(i);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            var entries = JsonSerializer.Deserialize<List<TranslationRecord>>(File.ReadAllText(FilePath), JsonOptions) ?? [];
            foreach (var entry in entries.Where(e => e.Source.Length > 0 && e.Results is { Count: > 0 }))
                Entries.Add(entry);
            Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 文件损坏或无权读取时从空历史开始，不影响翻译
            DiagnosticLog.Write("translation history load failed: " + ex.Message, "history.log");
        }
    }

    private void ScheduleSave()
    {
        if (!_persist)
            return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>界面线程只取快照，序列化和写盘放到后台依次进行。</summary>
    private void Save()
    {
        if (!_persist)
            return;
        var snapshot = Entries.ToList();
        _saving = _saving.ContinueWith(_ => Write(snapshot), TaskScheduler.Default);
    }

    private static void Write(List<TranslationRecord> snapshot)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("translation history save failed: " + ex.Message, "history.log");
        }
    }

    public void Dispose()
    {
        if (!_persist)
            return;
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            Save();
        }
        _saving.Wait(TimeSpan.FromSeconds(3)); // 退出前等最后一次保存写完
    }
}
