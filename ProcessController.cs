using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Lifeguard;

public enum RestartResult
{
    Started,    // 已啟動新程序
    Skipped,    // 已有另一個重啟在進行,本次略過(不計入失敗)
    Failed,     // 找不到 exe / 舊程序殺不掉 / 啟動失敗 / 例外
    Cancelled   // 被取消(程式關閉中)
}

/// <summary>程序狀態檢查、結束與重啟</summary>
public sealed class ProcessController(ILogger<ProcessController> logger)
{
    // 每個實例同一時間只允許一個重啟流程
    // by Claude and ChatGPT
    // 刻意不在實例被移除時清除:
    //  - 每個 instanceId 只有一個 SemaphoreSlim,沒用到 AvailableWaitHandle 就沒有 kernel handle,不 Dispose 也無妨
    //  - 若移除時舊監控還在重啟中、同名實例又被加回來,新舊會拿到不同的鎖,反而可能同時重啟同一個程序
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _restartLocks = new();

    // 重啟流程的等待時間。先集中成常數;若實測不夠再開放到 config.json
    // Kill 後等程序結束的時間(逾時視為未確認結束)
    private static readonly TimeSpan KillConfirmTimeout = TimeSpan.FromSeconds(5);
    // 第一輪沒確認全部結束時,等多久再重新掃描一次
    private static readonly TimeSpan KillRescanDelay = TimeSpan.FromSeconds(2);
    // 舊程序確認結束後、啟動新程序前,留給 OS / 防毒 / 檔案系統釋放資源的時間
    private static readonly TimeSpan ReleaseDelayAfterKill = TimeSpan.FromSeconds(3);

    // Check if a process exists, get its PID, and check if it's responding
    // by Claude and ChatGPT
    public static (bool Exists, int? Pid, bool IsResponding) CheckStatus(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return (false, null, false);

        try
        {
            string fullPath = Path.GetFullPath(exePath);
            string processName = Path.GetFileNameWithoutExtension(fullPath);

            int currentSessionId;
            using (Process current = Process.GetCurrentProcess())
                currentSessionId = current.SessionId;

            // 路徑讀不到的同名程序:身份未確認,只當備援。
            // 目的是避免把「卡死、無法查詢路徑」的程序誤報成 GONE。
            (int Pid, bool IsResponding)? unverified = null;

            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited ||
                            process.SessionId != currentSessionId)
                        {
                            continue;
                        }

                        // 不用 MainModule:程序卡死或高負載時它很容易失敗
                        string? processPath = ProcessNative.TryGetProcessPath(process.Id);

                        if (processPath is null)
                        {
                            unverified ??= (process.Id, process.Responding);
                            continue;
                        }

                        if (!string.Equals(
                                Path.GetFullPath(processPath),
                                fullPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        return (
                            true,
                            process.Id,
                            process.Responding
                        );
                    }
                    catch (InvalidOperationException)
                    {
                        // Process exited while being queried.
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // Process information is unavailable.
                    }
                }
            }

            if (unverified is { } fallback)
                return (true, fallback.Pid, fallback.IsResponding);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CheckProcessStatus failed: {ex}");
        }

        return (false, null, false);
    }

    /// <summary>
    /// 取得這個執行檔目前程序的啟動時間(本地時間)。
    /// 沒有在執行、或無法確認是同一個路徑的程序時回傳 null(寧可不知道,也不要拿到別的實例的時間)。
    /// </summary>
    public static DateTime? TryGetStartTime(string exePath)
    {
        var (exists, pid, _) = CheckStatus(exePath);
        if (!exists || pid is not int id) return null;

        try
        {
            // CheckStatus 在路徑讀不到時會回傳「未確認身份」的同名程序,這裡要再確認一次路徑
            string? processPath = ProcessNative.TryGetProcessPath(id);
            if (processPath is null ||
                !string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            using var process = Process.GetProcessById(id);
            return process.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    public async Task<RestartResult> RestartAsync(
        string instanceId,
        string exePath,
        string arguments = "/portable",
        CancellationToken token = default,
        Action? beforeStart = null)
    {
        var gate = _restartLocks.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(0, token))
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("[{InstanceId}] Restart already in progress, skipping.", instanceId);

            return RestartResult.Skipped;
        }

        try
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("RECOVERY: Initiating restart sequence for [{InstanceId}]...", instanceId);

            exePath = Path.GetFullPath(exePath);

            if (!File.Exists(exePath))
            {
                logger.LogError("[{InstanceId}] Executable not found: {Path}", instanceId, exePath);
                return RestartResult.Failed;
            }

            // 1. 清理舊程序(同步 API,丟到背景執行緒避免卡住呼叫端)
            bool allClosed = await Task.Run(() => KillMatchingProcesses(instanceId, exePath), token);

            // 沒確認全部結束時,常見的是暫時性狀況(程序正在結束、路徑一時讀不到):
            // 等 2 秒重新掃描一次,避免為此浪費一次重啟額度和 15 秒冷卻。
            // 對正在結束的程序再 Kill 一次是安全的。
            if (!allClosed)
            {
                logger.LogWarning("[{InstanceId}] Kill not confirmed, rescanning in {Seconds}s.", instanceId, KillRescanDelay.TotalSeconds);
                await Task.Delay(KillRescanDelay, token);
                allClosed = await Task.Run(() => KillMatchingProcesses(instanceId, exePath), token);
            }

            if (!allClosed)
            {
                logger.LogError("[{InstanceId}] Old process still running, aborting restart.", instanceId);
                return RestartResult.Failed;
            }

            // 2. 給 OS 一點時間釋放資源
            await Task.Delay(ReleaseDelayAfterKill, token);

            // 舊程序已確認結束、新程序尚未啟動:此刻清除殘留訊號最安全
            beforeStart?.Invoke();

            // 3. 啟動新程序
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
                WindowStyle = ProcessWindowStyle.Minimized,
                UseShellExecute = true
            };

            using var started = Process.Start(startInfo);

            if (started is null)
            {
                logger.LogError("[{InstanceId}] RECOVERY FAILED: Process.Start returned null.", instanceId);
                return RestartResult.Failed;
            }

            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("[{InstanceId}] RECOVERY STARTED (PID: {Pid}).", instanceId, started.Id);

            return RestartResult.Started;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("[{InstanceId}] Restart cancelled.", instanceId);

            return RestartResult.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{InstanceId}] RECOVERY FAILED.", instanceId);
            return RestartResult.Failed;
        }
        finally
        {
            gate.Release();
        }
    }


    private bool KillMatchingProcesses(string instanceId, string exePath)
    {
        bool allClosed = true;
        int killed = 0;
        int otherPathSameName = 0;

        string targetPath = Path.GetFullPath(exePath);
        string procName = Path.GetFileNameWithoutExtension(targetPath);

        using var current = Process.GetCurrentProcess();
        int currentSessionId = current.SessionId;

        foreach (var proc in Process.GetProcessesByName(procName))
        {
            using (proc)
            {
                // 1) 判斷是不是目標程序
                string? processPath;
                try
                {
                    if (proc.Id == current.Id || proc.SessionId != currentSessionId)
                        continue;

                    processPath = ProcessNative.TryGetProcessPath(proc.Id);

                    if (processPath is not null)
                        processPath = Path.GetFullPath(processPath);
                }
                catch (InvalidOperationException)
                {
                    continue; // 檢查期間程序已自行退出
                }
                catch (Win32Exception ex)
                {
                    allClosed = false; // 無法確認身份 → fail-safe
                    logger.LogWarning(ex, "[{InstanceId}] Cannot inspect PID {Pid}, skipping.", instanceId, proc.Id);
                    continue;
                }

                if (processPath is null)
                {
                    // 路徑取不到:可能剛好退出,也可能權限不足
                    bool exited;
                    try { exited = proc.HasExited; }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { exited = false; }

                    if (exited) continue;

                    allClosed = false; // 無法確認身份 → fail-safe
                    logger.LogWarning("[{InstanceId}] Cannot read image path of PID {Pid}, skipping.", instanceId, proc.Id);
                    continue;
                }

                if (!string.Equals(processPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    otherPathSameName++; // 同名但不是這個實例(多個 MT4 並存)
                    continue;
                }

                // 2) 已確定是目標程序,才進行 termination
                try
                {
                    logger.LogWarning("[{InstanceId}] Terminating target process PID {Pid}.", instanceId, proc.Id);

                    proc.Kill(entireProcessTree: true);

                    if (proc.WaitForExit(KillConfirmTimeout))
                    {
                        killed++;
                    }
                    else
                    {
                        allClosed = false;
                        // Warning 即可:RestartAsync 會重新掃描一次;最終仍失敗時由
                        // "Old process still running, aborting restart." 記 Error,一次失敗只有一行 Error
                        logger.LogWarning("[{InstanceId}] Failed to confirm termination of PID {Pid}.", instanceId, proc.Id);
                    }
                }
                catch (Win32Exception ex)
                {
                    allClosed = false;
                    logger.LogWarning(ex, "[{InstanceId}] Unable to terminate PID {Pid}.", instanceId, proc.Id);
                }
                catch (InvalidOperationException ex)
                {
                    // 程序可能在 Kill / WaitForExit 前後自行退出
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug(ex, "[{InstanceId}] Process PID {Pid} is no longer available.", instanceId, proc.Id);
                }
                catch (AggregateException ex)
                {
                    // Process tree 中有程序無法終止
                    allClosed = false;
                    logger.LogWarning(ex, "[{InstanceId}] Failed to kill process tree of PID {Pid}.", instanceId, proc.Id);
                }
            }
        }

        // 沒殺到任何程序、但同名程序存在:提示可能是路徑設定不符(之後會直接啟動新程序)
        if (killed == 0 && otherPathSameName > 0 && logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[{InstanceId}] No process matched {Path}; {Count} same-name process(es) at other paths left untouched.",
                instanceId, targetPath, otherPathSameName);

        return allClosed;
    }
}
