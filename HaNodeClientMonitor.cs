using Microsoft.Extensions.Logging;

namespace Lifeguard;

/// <summary>
/// 監控 HaNodeClient:等待它的 heartbeat 事件,逾時就重啟。
/// 重啟次數、Dead / Flapping、提醒與恢復通知都交給 RestartTracker。
/// config.json 沒有設定 HaNodeClient.ExePath 時不監控,設定後(熱重載)自動開始。
/// </summary>
public sealed class HaNodeClientMonitor(
    ConfigProvider configProvider,
    ILogger<HaNodeClientMonitor> logger,
    INotifier notifier,
    MonitorState state,
    ConfigReloadSignal reload,
    ProcessController processes)
{
    private const string InstanceId = "HaNodeClient";
    private const string EventName = @"Local\HaNodeClient_Heartbeat";

    public async Task RunAsync(CancellationToken token)
    {
        var tracker = new RestartTracker(new RestartTarget
        {
            Tag = "[HaNodeClient]",
            LogName = "[HaNodeClient]",
            AlertName = "<b>[HaNodeClient.exe]</b>",
            ReminderName = "<b>[HaNodeClient]</b>",
            FlapAdvice = "Please check it immediately!",
            OnAlive = () => state.IsHaNodeClientServerDown = false,
            OnFailure = () =>
            {
                state.MarkError();
                state.IsHaNodeClientServerDown = true;
            },
            OnDead = () => state.IsHaNodeClientServerDown = true
        }, logger, notifier);

        bool justWokeUp = true;

        using var heartbeatEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);

        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var currentConfig = configProvider.Current;
                    string? exePath = currentConfig.HaNodeClient?.ExePath;
                    int nodeTimeoutMs = currentConfig.HaNodeClient?.EffectiveHeartbeatTimeoutMs ?? HaNodeClient.DefaultHeartbeatTimeoutMs;

                    if (string.IsNullOrEmpty(exePath))
                    {
                        // 未設定(或被移除)→ 停止監控,並清除狀態,避免標題列殘留 "HaNodeClient DOWN"
                        justWokeUp = true;
                        tracker.ResetWhenDisabled();

                        WaitHandle.WaitAny(new[] { token.WaitHandle, reload.Token.WaitHandle });
                        continue;
                    }

                    if (justWokeUp)
                    {
                        if (logger.IsEnabled(LogLevel.Information))
                            logger.LogInformation("[HaNodeClient] Monitoring activated. Listening on Event '{EventName}'...", EventName);

                        justWokeUp = false;
                    }

                    var reloadToken = reload.Token;
                    int waitTimeout = tracker.BeginWait(
                        nodeTimeoutMs,
                        currentConfig.EffectiveAlertReminderIntervalMs,
                        currentConfig.EffectiveRestartStartupGraceMs,
                        out bool retryNow);

                    // index: 0=heartbeat 1=shutdown 2=reload
                    // 已經由 Task.Run 丟到背景執行緒,這裡用阻塞式 WaitAny 是安全的
                    int waitResult = WaitHandle.WaitAny(
                        new WaitHandle[] { heartbeatEvent, token.WaitHandle, reloadToken.WaitHandle },
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

                    // ---------- 已宣告死亡:只發提醒,不再重啟 ----------
                    if (tracker.IsDead)
                    {
                        tracker.SendReminder();
                        continue;
                    }

                    // ---------- 心跳逾時 ----------
                    string reason = tracker.DescribeFailure(exePath, retryNow);
                    tracker.MarkFailure();

                    if (!tracker.CanRestart(currentConfig))
                        continue;

                    int maxRetries = currentConfig.EffectiveMaxRetries;
                    tracker.LogRestart(reason, maxRetries);

                    // 在「舊程序已死、新程序尚未啟動」之間清除殘留的 heartbeat
                    var result = await processes.RestartAsync(
                        InstanceId, exePath, "", token,
                        beforeStart: () => heartbeatEvent.Reset());

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
                    logger.LogError(ex, "[HaNodeClient] Monitor loop iteration failed. Continuing.");
                    await Task.Delay(5000, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 正常關閉
        }
    }
}
