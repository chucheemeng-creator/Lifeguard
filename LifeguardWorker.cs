using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lifeguard;

/// <summary>
/// 啟動寬限期過後,依設定啟動各監控並持續巡檢(supervisor)。
/// 監控本身的邏輯在 HaNodeClientMonitor / Mt4Monitor,重啟狀態機在 RestartTracker。
/// </summary>
public class LifeguardWorker(
    ConfigProvider configProvider,
    ILogger<LifeguardWorker> logger,
    ConfigReloadSignal reload,
    StatusReporter statusReporter,
    HaNodeClientMonitor haNodeMonitor,
    Mt4Monitor mt4Monitor) : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var currentConfig = configProvider.Current;
        int delayMs = currentConfig.EffectiveInitialDelayMs;

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("System booting... Grace period of {DelaySeconds} seconds started. Allowing MT4 instances to initialize...",
                delayMs / 1000);

        try
        {
            await Task.Delay(delayMs, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            logger.LogInformation("Shutdown signal received during grace period. Exiting.");
            return;
        }

        logger.LogInformation("Grace period ended. Starting monitoring tasks.");

        // 寬限期結束後才讀設定(寬限期內的修改也會生效),並持續比對設定變更
        await SuperviseMonitorsAsync(stoppingToken);

        logger.LogInformation("Shutdown signal received. All monitoring engines stopped gracefully.");
    }

    // 定期巡檢間隔:補啟動意外結束的監控(設定重載時會立即巡檢,不必等這個間隔)
    private static readonly TimeSpan SupervisorInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 監控任務的 supervisor:
    ///  - 依 config.json 的 MT4Instances 啟動監控;熱重載後新增的實例會立即補啟動
    ///  - 監控任務意外結束(例外)會被記錄,並在下一輪自動重啟
    ///  - 被移出設定的實例,其監控會自行結束,這裡負責清除記錄
    /// </summary>
    private async Task SuperviseMonitorsAsync(CancellationToken token)
    {
        var running = new Dictionary<string, Task>();
        bool warnedNoInstances = false;

        void EnsureRunning(string key, Func<Task> factory)
        {
            if (running.TryGetValue(key, out var existing))
            {
                if (!existing.IsCompleted) return;
                logger.LogInformation("Restarting monitor [{Key}].", key);
            }
            else
            {
                logger.LogInformation("Starting monitor [{Key}].", key);
            }

            running[key] = RunGuarded(key, factory, token);
        }

        while (!token.IsCancellationRequested)
        {
            // 先取得這一輪的重載訊號、再讀設定:
            // 若讀設定的期間又發生重載,下面的等待會立刻返回並重新比對
            var reloadToken = reload.Token;

            try
            {
                var config = configProvider.Current;
                var desired = new HashSet<string>();

                desired.Add("ha-node");
                EnsureRunning("ha-node", () => haNodeMonitor.RunAsync(token));

                if (OperatingSystem.IsWindows())
                {
                    desired.Add("status");
                    EnsureRunning("status", () => statusReporter.RunAsync(token));
                }

                if (config.MT4Instances is { Count: > 0 })
                {
                    warnedNoInstances = false;
                    foreach (string id in config.MT4Instances.Keys)
                    {
                        string key = $"mt4:{id}";
                        desired.Add(key);
                        EnsureRunning(key, () => mt4Monitor.RunAsync(id, token));
                    }
                }
                else if (!warnedNoInstances)
                {
                    // 只提醒一次(設定修正、重載後會重新計算)
                    logger.LogWarning("No MT4Instances configured in config.json. No MT4 instance will be monitored.");
                    warnedNoInstances = true;
                }

                // 清除「已結束、且已不在設定中」的項目(被移除的實例)
                foreach (var key in running
                    .Where(kv => kv.Value.IsCompleted && !desired.Contains(kv.Key))
                    .Select(kv => kv.Key)
                    .ToList())
                {
                    running.Remove(key);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Monitor supervisor pass failed. Will retry.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, reloadToken);
            try
            {
                await Task.Delay(SupervisorInterval, linked.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // 設定重載:立即重新比對
            }
        }

        // 等所有監控自行結束(它們都會觀察 token);RunGuarded 不會把例外丟出來
        await Task.WhenAll(running.Values);
    }

    /// <summary>
    /// 在背景執行緒執行一個監控並攔截所有例外,
    /// 避免某個監控掛掉卻沒有任何人知道(Task.WhenAll 會一直等到關機才拋出)
    /// </summary>
    private Task RunGuarded(string name, Func<Task> body, CancellationToken token)
    {
        return Task.Run(async () =>
        {
            try
            {
                await body();

                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("Monitor [{Name}] exited.", name);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 正常關閉
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Monitor [{Name}] crashed. The supervisor will restart it.", name);
            }
        }, CancellationToken.None);
    }
}
