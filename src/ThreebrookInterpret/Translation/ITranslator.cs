using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ThreebrookInterpret.Translation;

/// <summary>
/// 翻译引擎接口。新增引擎只需实现此接口并在 TranslatorFactory 中注册。
/// </summary>
public interface ITranslator
{
    /// <param name="from">源语言代码，"auto" 表示自动检测。</param>
    /// <param name="to">目标语言代码（见 <see cref="Languages"/>）。</param>
    Task<string> TranslateAsync(string text, string from, string to, CancellationToken ct);
}

public sealed class TranslationException : Exception
{
    public TranslationException(string message, Exception? inner = null) : base(message, inner) { }
}

internal static class Http
{
    public static HttpClient Client { get; } = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        return client;
    }

    /// <summary>为单次请求加上超时，超时后抛出 TranslationException 而不是普通的取消。</summary>
    public static async Task<T> WithTimeout<T>(TimeSpan timeout, CancellationToken ct, Func<CancellationToken, Task<T>> action)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await action(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranslationException($"请求超时（{timeout.TotalSeconds:0} 秒）");
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException("网络错误：" + ex.Message, ex);
        }
    }
}
