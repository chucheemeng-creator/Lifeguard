using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lifeguard;

/// <summary>
/// 設定檔熱重載的通知來源。
/// 每次重載:先換成新的 CancellationTokenSource,再取消舊的,
/// 所以任何時刻讀到的 Token 都不會是「已取消卻尚未更換」的,等待者不會因此空轉。
/// </summary>
public sealed class ConfigReloadSignal : IDisposable
{
    private readonly ILogger<ConfigReloadSignal> _logger;
    private readonly IDisposable? _subscription;
    private volatile CancellationTokenSource _cts = new();

    public ConfigReloadSignal(IOptionsMonitor<Config> configMonitor, ILogger<ConfigReloadSignal> logger)
    {
        _logger = logger;

        _subscription = configMonitor.OnChange(_ =>
        {
            _logger.LogInformation("🔄 HOT RELOAD: config.json changes applied dynamically.");

            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            oldCts.Cancel();

            // 刻意不 Dispose oldCts:監控執行緒可能正在 WaitAny 它的 WaitHandle,
            // 提早 Dispose 會讓等待端拿到已關閉的 handle。
            // 這不會永久洩漏:oldCts 已不再被引用,GC 回收時 SafeWaitHandle 的終結器會關閉 kernel handle。
            // 設定重載是人為操作(一天幾次),就算回收前暫存幾個 handle 也微不足道。
        });
    }

    /// <summary>目前這一輪的重載訊號:設定重載時會被取消(並已換成新的)</summary>
    public CancellationToken Token => _cts.Token;

    public void Dispose()
    {
        // 不 Dispose CancellationTokenSource:可能還有執行緒正在等它的 WaitHandle
        _subscription?.Dispose();
    }
}
