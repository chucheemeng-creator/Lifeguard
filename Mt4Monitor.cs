using Microsoft.Extensions.Logging;
using System.Net;

namespace Lifeguard;

/// <summary>
/// 監控一個 MT4 實例:等待 EA 的 heartbeat 事件,並由 Mt4LogWatcher 監看 log 的 fatal 簽名;
/// 心跳逾時或出現 fatal 就重啟。
/// 重啟次數、Dead / Flapping、提醒與恢復通知都交給 RestartTracker。
/// 每個實例呼叫一次 RunAsync,狀態都在該次呼叫內,所以這個類別本身可以是單例。
/// </summary>
public sealed class Mt4Monitor(
    ConfigProvider configProvider,
    ILogger<Mt4Monitor> logger,
    INotifier notifier,
    MonitorState state,
    ConfigReloadSignal reload,
    ProcessController processes,
    Mt4LogWatcher logWatcher)
{
    public async Task RunAsync(string instanceId, CancellationToken token)
    {
        var startupInstances = configProvider.Current.MT4Instances;
        if (startupInstances is null || !startupInstances.TryGetValue(instanceId, out var instanceConfig)) return;

        string exePath = instanceConfig.ExePath;
        string logDir = instanceConfig.LogDirectory;

        string eventName = $@"Local\EA_Heartbeat_{instanceId}";

        // Telegram 訊息是 HTML,動態內容必須轉義
        string safeId = WebUtility.HtmlEncode(instanceId);

        var tracker = new RestartTracker(new RestartTarget
        {
            Tag = $"[{instanceId}]",
            LogName = $"MT4 [{instanceId}]",
            AlertName = $"MT4 Instance <b>[{safeId}]</b>",
            ReminderName = $"<b>[{safeId}]</b>",
            FlapAdvice = "Please check the EA / account / log immediately!",
            OnAlive = () => state.MarkInstanceAlive(instanceId),
            OnFailure = state.MarkError,
            OnDead = () => state.MarkInstanceDead(instanceId)
        }, logger, notifier);

        using var heartbeatEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        using var logErrorEvent = new AutoResetEvent(false);          // watcher → 主迴圈:強制重啟
        // LogDirectory 熱重載時會換成新的,所以不用 using,改在 finally 釋放
        var watcherCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task watcherTask = Task.CompletedTask;

        // 啟動 log watcher;watcher 內部所有例外都在這裡被記錄,不會變成 unobserved exception
        Task StartWatcher(string dir, CancellationToken ct) => Task.Run(async () =>
        {
            try
            {
                // exePath 會隨設定重載更新;啟動掃描時讀當下的值
                await logWatcher.WatchAsync(instanceId, dir, logErrorEvent, () => ProcessController.TryGetStartTime(exePath), ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{InstanceId}] Log watcher terminated unexpectedly.", instanceId);
            }
        }, CancellationToken.None);

        // LogDirectory 熱重載:停掉舊 watcher(等它完全結束),再對新目錄啟動一個新的
        async Task RestartWatcherAsync(string newDir)
        {
            watcherCts.Cancel();

            // StartWatcher 內已攔截所有例外,這裡只是保險:
            // 就算舊 watcher 收尾失敗,也要換到新目錄,不能卡在舊狀態每 5 秒重試
            try { await watcherTask; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{InstanceId}] Previous log watcher failed to stop cleanly.", instanceId);
            }
            watcherCts.Dispose();

            // 舊目錄留下的 fatal 訊號不該讓新目錄一啟動就重啟 MT4。
            // 必須在舊 watcher 確定結束之後才 Reset,否則它可能在 Reset 之後又 Set。
            // 若是真的 fatal 而且也寫在新目錄,新 watcher 的啟動掃描會再抓到。
            logErrorEvent.Reset();

            logDir = newDir;
            watcherCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            watcherTask = StartWatcher(logDir, watcherCts.Token);
        }

        try
        {
            watcherTask = StartWatcher(logDir, watcherCts.Token);

            // 錯開多個實例同時啟動
            await Task.Delay(Random.Shared.Next(10, 500), token);

            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("[{InstanceId}] Monitoring activated. Listening on Event '{EventName}'...", instanceId, eventName);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var currentConfig = configProvider.Current;

                    // 實例被移出設定 → 結束;否則每輪重新讀取 exePath(設定重載後生效)
                    var instances = currentConfig.MT4Instances;
                    if (instances is null || !instances.TryGetValue(instanceId, out var latestInstance))
                    {
                        logger.LogWarning("[{InstanceId}] Instance removed from configuration. Stopping monitor.", instanceId);
                        state.MarkInstanceAlive(instanceId);   // 避免標題列殘留 "EA Offline"
                        break;
                    }
                    exePath = latestInstance.ExePath;

                    // LogDirectory 被修改 → 改監看新目錄
                    if (!string.Equals(latestInstance.LogDirectory, logDir, StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogInformation("[{InstanceId}] LogDirectory changed: {OldDir} -> {NewDir}. Restarting log watcher.",
                            instanceId, logDir, latestInstance.LogDirectory);
                        await RestartWatcherAsync(latestInstance.LogDirectory);
                    }

                    var reloadToken = reload.Token;
                    int waitTimeout = tracker.BeginWait(
                        currentConfig.EffectiveTimeoutMs,
                        currentConfig.EffectiveAlertReminderIntervalMs,
                        currentConfig.EffectiveRestartStartupGraceMs,
                        out bool retryNow);

                    // index: 0=heartbeat 1=shutdown 2=reload 3=log fatal
                    int waitResult = WaitHandle.WaitAny(
                        new WaitHandle[] { heartbeatEvent, token.WaitHandle, reloadToken.WaitHandle, logErrorEvent },
                        waitTimeout);

                    if (waitResult == 1 || token.IsCancellationRequested) break;

                    if (waitResult == 2 || reloadToken.IsCancellationRequested)
                    {
                        // 若 ConfigReloadSignal 尚未換成新的 Token,避免 busy loop
                        await Task.Delay(100, token);
                        continue;
                    }

                    tracker.EndWait();

                    // ---------- 收到心跳 ----------
                    if (waitResult == 0)
                    {
                        tracker.OnHeartbeat(currentConfig.NotifyOnRecovery);
                        continue;
                    }

                    bool logTriggered = (waitResult == 3);

                    // ---------- 已宣告死亡:只發提醒,不再重啟 ----------
                    if (tracker.IsDead)
                    {
                        if (logTriggered)
                        {
                            if (logger.IsEnabled(LogLevel.Debug))
                                logger.LogDebug("[{InstanceId}] Log fatal signal ignored (restarts suspended).", instanceId);
                            continue;
                        }

                        tracker.SendReminder();
                        continue;
                    }

                    // ---------- 失敗觸發:心跳逾時 或 Log 出現 fatal ----------
                    string reason;

                    if (logTriggered)
                    {
                        logger.LogCritical("FATAL LOG: MT4 [{InstanceId}] fatal signature detected in log. Forcing restart.", instanceId);
                        reason = "Fatal log signature";
                    }
                    else
                    {
                        reason = tracker.DescribeFailure(exePath, retryNow);
                    }

                    tracker.MarkFailure();

                    if (!tracker.CanRestart(currentConfig))
                        continue;

                    int maxRetries = currentConfig.EffectiveMaxRetries;
                    tracker.LogRestart(reason, maxRetries);

                    // 清除重啟流程期間可能殘留的舊 heartbeat / fatal 訊號。
                    // 時機必須在「舊程序已死、新程序尚未啟動」之間，
                    // 避免在新程序啟動後 Reset 而吃掉新的 heartbeat / fatal。
                    bool forceAutoTradeOff = currentConfig.ForceAutoTradeOffOnStart;

                    var result = await processes.RestartAsync(
                        instanceId, exePath, "/portable", token,
                        beforeStart: () =>
                        {
                            heartbeatEvent.Reset();
                            logErrorEvent.Reset();

                            // 舊程序已結束、新程序尚未啟動:此時修改 experts.ini 才有效
                            if (forceAutoTradeOff)
                                Mt4AutoTrade.ForceOff(exePath, instanceId, logger);
                        });

                    if (result == RestartResult.Cancelled) break;

                    if (!tracker.OnRestartResult(result, maxRetries))
                        continue;

                    await Task.Delay(currentConfig.EffectiveRestartCooldownMs, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 任何非預期例外:記錄後稍等再繼續,不讓監控迴圈死掉
                    logger.LogError(ex, "[{InstanceId}] Monitor loop iteration failed. Continuing.", instanceId);
                    await Task.Delay(5000, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 正常關閉
        }
        finally
        {
            // 先停 watcher 並等它結束,再釋放 watcherCts;logErrorEvent 由 using 釋放
            watcherCts.Cancel();
            await watcherTask;
            watcherCts.Dispose();
        }
    }
}
