using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ThreeBrookTrans.Views;

/// <summary>单个引擎的翻译结果卡片。</summary>
public sealed class ResultItem : INotifyPropertyChanged
{
    private string _text = "";
    private string _status = "翻译中…";
    private bool _isError;

    public ResultItem(string name) => Name = name;

    public string Name { get; }

    public string Text { get => _text; private set => Set(ref _text, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public bool HasResult => !IsError && Text.Length > 0;

    public void Complete(string text, TimeSpan elapsed)
    {
        Text = text;
        Status = $"{elapsed.TotalSeconds:0.0}s";
        OnPropertyChanged(nameof(HasResult));
    }

    public void Fail(string message)
    {
        IsError = true;
        Text = message;
        Status = "失败";
        OnPropertyChanged(nameof(HasResult));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
