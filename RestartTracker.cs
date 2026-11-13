using Microsoft.Extensions.Logging;

namespace Lifeguard;

/// <summary>
/// 監控對象之間不同的部分:顯示名稱,以及要回寫到 MonitorState 的狀態。
/// MT4 與 HaNodeClient 的重啟邏輯完全相同,只有這些不同。
/// </summary>
internal sealed class RestartTarget
{
    /// <summary>重啟相關日誌的前綴,例如 "[MT4_01]" / "[HaNodeClient]"</summary>
    public required string Tag { get; init; }

    /// <summary>狀態類日誌(CRITICAL / FATAL / RESURRECTED…)的名稱,例如 "MT4 [MT4_01]" / "[HaNodeClient]"</summary>
    public required string LogName { get; init; }

    /// <summary>Telegram 告警與恢復通知的名稱(HTML,動態內容須已轉義)</summary>
    public required string AlertName { get; init; }

    /// <summary>Telegram 每小時提醒的名稱(HTML,動態內容須已轉義)</summary>
    public required string ReminderName { get; init; }

    /// <summary>CRASH LOOP 告警最後一句的建議</summary>
    public required string FlapAdvice { get; init; }

    /// <summary>收到心跳時</summary>
    public required Action OnAlive { get; init; }

    /// <summary>偵測到失敗(心跳逾時 / fatal log)時</summary>
    public required Action OnFailure { get; init; }

    /// <summary>宣告 Dead / Flapping 時</summary>
    public required Action OnDead { get; init; }
}

/// <summary>
/// 重啟狀態機(MT4 與 HaNodeClient 共用):
///  - 連續重啟次數,達 MaxRetries 宣告 Dead
///  - 崩潰循環保護(滑動視窗內成功重啟次數),達上限宣告 Flapping
///  - 重啟失敗後冷卻完直接重試,不等心跳逾時
///  - 重啟成功後第一次等待給較長的啟動寬限期
///  - Dead 之後每隔一段時間提醒、心跳回來時通知恢復
/// 每個監控迴圈各有一個實例,只在該迴圈的執行緒上使用,不需要同步。
/// </summary>
internal sealed class RestartTracker(RestartTarget target, ILogger logger, INotifier notifier)
{
    private int _consecutiveRestartAttempts;
    private bool _isDead;
    private DateTime _deadSinceUtc;

    // 崩潰循環保護:記錄「成功啟動新程序」的時間(滑動視窗)。
    // 刻意不在心跳恢復時清空:崩潰循環偵測要跨越「重啟 → 恢復 → 又崩潰」累計,
    // 每次恢復都清空的話,「每次都恢復、但一直崩潰」的情況就永遠抓不到。
    // 舊紀錄會在超過 RestartFlapWindowMs 後自然移出視窗。
    private readonly Queue<DateTime> _restartTimes = new();

    // 上一次重啟失敗(沒有新程序被啟動):冷卻後直接再試,不必等 heartbeat 逾時
    private bool _retryAfterFailure;

    // 重啟成功後,新程序還在載入(登入、載入圖表與 EA),第一次等待給較長的寬限期,
    // 否則會在它開始送 heartbeat 之前就被當成沒反應而殺掉,形成「剛啟動就被殺」的循環
    private bool _awaitFirstHeartbeat;

    public bool IsDead => _isDead;

    /// <summary>下一次重啟是第幾次嘗試(日誌用)</summary>
    public int NextAttempt => _consecutiveRestartAttempts + 1;

    /// <summary>
    /// 開始一輪等待:決定這一輪的等待時間。
    /// retryNow = 上一次重啟失敗,這一輪零等待檢查一次訊號後直接重試。
    /// </summary>
    public int BeginWait(int timeoutMs, int reminderMs, int startupGraceMs, out bool retryNow)
    {
        // _retryAfterFailure 在 EndWait 才清除:若這一輪被設定重載喚醒(不呼叫 EndWait),下一輪仍會立即重試
        retryNow = _retryAfterFailure && !_isDead;

        bool inStartupGrace = _awaitFirstHeartbeat && !_isDead && !retryNow;

        return retryNow ? 0
            : _isDead ? reminderMs
            : inStartupGrace ? Math.Max(timeoutMs, startupGraceMs)
            : timeoutMs;
    }

    /// <summary>這一輪等待已結束(收到訊號或逾時),寬限期與「失敗後立即重試」都已用掉。設定重載造成的喚醒不要呼叫</summary>
    public void EndWait()
    {
        _awaitFirstHeartbeat = false;
        _retryAfterFailure = false;
    }

    /// <summary>收到心跳</summary>
    public void OnHeartbeat(bool notifyOnRecovery)
    {
        if (_isDead)
        {
            _isDead = false;
            string downTime = FormatDuration(DateTime.UtcNow - _deadSinceUtc);

            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("RESURRECTED: {Target} is back online after {DownTime}!", target.LogName, downTime);

            if (notifyOnRecovery)
                Notify($"✅ <b>RECOVERED</b> ✅\n{target.AlertName} heartbeat is back after <b>{downTime}</b>.");
        }

        target.OnAlive();
        _consecutiveRestartAttempts = 0;
        // 刻意不清空 _restartTimes(見欄位註解)
    }

    /// <summary>已宣告 Dead 且等待逾時:發提醒,不再重啟</summary>
    public void SendReminder()
    {
        string downTime = FormatDuration(DateTime.UtcNow - _deadSinceUtc);

        logger.LogWarning("REMINDER: {Target} is still offline after {DownTime}. Sending repeated alert.", target.LogName, downTime);

        Notify($"⚠️ <b>Persistent Downtime Alert</b> ⚠️\n{target.ReminderName} has not recovered after <b>{downTime}</b>." +
               " Immediate manual intervention required!");
    }

    /// <summary>心跳逾時:檢查程序狀態並記錄,回傳重啟原因</summary>
    public string DescribeFailure(string exePath, bool retryNow)
    {
        var (exists, pid, isResponding) = ProcessController.CheckStatus(exePath);
        string reason;

        if (!exists)
        {
            logger.LogError("CRITICAL: {Target} Process is completely GONE (Crashed/Closed).", target.LogName);
            reason = "Process gone";
        }
        else if (!isResponding)
        {
            logger.LogWarning("WARNING: {Target} Process exists (PID: {ProcessId}) but is NOT RESPONDING (Stuck/High CPU Load).",
                target.LogName, pid);
            reason = "Process not responding";
        }
        else
        {
            logger.LogWarning("WARNING: {Target} Process is RESPONDING but missed heartbeat (Logical hang or delayed).", target.LogName);
            reason = "Heartbeat timeout";
        }

        return retryNow ? "Previous restart failed, retrying" : reason;
    }

    /// <summary>偵測到失敗:回寫狀態(標題列顯示錯誤)</summary>
    public void MarkFailure() => target.OnFailure();

    /// <summary>
    /// 重啟前檢查:已重啟 MaxRetries 次仍未恢復 → Dead;視窗內重啟太多次 → Flapping。
    /// 回傳 true = 可以重啟。
    /// </summary>
    public bool CanRestart(Config config)
    {
        int maxRetries = config.EffectiveMaxRetries;

        // 已經真的重啟過 maxRetries 次仍未恢復 → 宣告死亡
        if (_consecutiveRestartAttempts >= maxRetries)
        {
            DeclareDead(maxRetries);
            return false;
        }

        // 崩潰循環保護:每次重啟後都有 heartbeat(計數被歸零)時,上面的 maxRetries 抓不到。
        // 改看「視窗內成功重啟的次數」,太多就暫停自動重啟並通知。
        int flapWindowMs = config.EffectiveRestartFlapWindowMs;
        int flapMax = config.EffectiveRestartFlapMaxCount;
        var flapWindowStart = DateTime.UtcNow - TimeSpan.FromMilliseconds(flapWindowMs);

        while (_restartTimes.Count > 0 && _restartTimes.Peek() < flapWindowStart)
            _restartTimes.Dequeue();

        if (_restartTimes.Count >= flapMax)
        {
            DeclareFlapping(_restartTimes.Count, Math.Max(1, flapWindowMs / 60000));
            return false;
        }

        return true;
    }

    public void LogRestart(string reason, int maxRetries)
    {
        logger.LogWarning("{Tag} {Reason}. Initiating restart... (Attempt {Attempt}/{MaxRetries})",
            target.Tag, reason, NextAttempt, maxRetries);
    }

    /// <summary>
    /// 記錄重啟結果。回傳 false = 不必再冷卻等待(已宣告 Dead,或已取消),直接進入下一輪。
    /// Cancelled 應由呼叫端自行處理(結束迴圈);就算誤傳進來,也不會被算成一次嘗試。
    /// </summary>
    public bool OnRestartResult(RestartResult result, int maxRetries)
    {
        // 防禦:取消(程式關閉中)不是一次重啟嘗試,不能影響計數
        if (result == RestartResult.Cancelled) return false;

        // 另一個重啟正在進行(Skipped)不算一次嘗試
        if (result != RestartResult.Skipped) _consecutiveRestartAttempts++;

        if (result == RestartResult.Failed)
        {
            logger.LogWarning("{Tag} Restart attempt failed. ({Attempts}/{MaxRetries})",
                target.Tag, _consecutiveRestartAttempts, maxRetries);

            // 最後一次重啟就失敗:沒有新程序被啟動,不會有 heartbeat,
            // 不必再等一個 timeout 週期才宣告死亡,直接通知。
            if (_consecutiveRestartAttempts >= maxRetries)
            {
                DeclareDead(maxRetries);
                return false;
            }

            // 沒有新程序被啟動,不會有 heartbeat:冷卻後直接重試,不等 heartbeat 逾時
            _retryAfterFailure = true;
        }
        else if (result == RestartResult.Started)
        {
            _restartTimes.Enqueue(DateTime.UtcNow);
            _awaitFirstHeartbeat = true;
        }

        return true;
    }

    /// <summary>監控對象被停用(例如 HaNodeClient 的 ExePath 被移除):清除 Dead 與重試計數</summary>
    public void ResetWhenDisabled()
    {
        _isDead = false;
        _consecutiveRestartAttempts = 0;
        target.OnAlive();
    }

    // 宣告死亡:暫停自動重啟並通知管理員
    private void DeclareDead(int attempts)
    {
        _isDead = true;
        _deadSinceUtc = DateTime.UtcNow;
        target.OnDead();

        logger.LogCritical("FATAL: {Target} reached max retries. Suspending restarts.", target.LogName);

        Notify($"🚨 <b>CRITICAL ALERT</b> 🚨\n{target.AlertName} has crashed and automatic restart failed" +
               $" after {attempts} attempts. Please log in and check immediately!");
    }

    // 崩潰循環:短時間內反覆「重啟成功 → 又崩潰」,每次都有 heartbeat,所以 DeclareDead 抓不到
    private void DeclareFlapping(int count, int windowMinutes)
    {
        _isDead = true;
        _deadSinceUtc = DateTime.UtcNow;
        target.OnDead();

        logger.LogCritical("FLAPPING: {Target} was restarted {Count} times within {Minutes} minutes. Suspending restarts.",
            target.LogName, count, windowMinutes);

        Notify($"🔁 <b>CRASH LOOP</b> 🔁\n{target.AlertName} was restarted <b>{count} times</b> within {windowMinutes} minutes." +
               $" Automatic restarts are suspended. {target.FlapAdvice}");
    }

    // 通知不能卡住監控迴圈:發射後不等待(TelegramNotifier 內部已攔截所有例外)
    private void Notify(string message) =>
        _ = Task.Run(() => notifier.NotifyAsync(message, CancellationToken.None), CancellationToken.None);

    /// <summary>停擺時間的顯示:未滿 1 分鐘顯示秒,未滿 1 小時顯示分鐘,其餘顯示「時 + 分」</summary>
    internal static string FormatDuration(TimeSpan d)
    {
        static string Unit(int n, string name) => n == 1 ? $"1 {name}" : $"{n} {name}s";

        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalSeconds < 60) return Unit((int)d.TotalSeconds, "second");
        if (d.TotalMinutes < 60) return Unit((int)d.TotalMinutes, "minute");
        return $"{(int)d.TotalHours} h {d.Minutes} min";
    }
}
