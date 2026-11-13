namespace Lifeguard;

public class HaNodeClient
{
    public const int DefaultHeartbeatTimeoutMs = 15000;

    public string? ExePath { get; set; }
    public int HeartbeatTimeoutMs { get; set; } = DefaultHeartbeatTimeoutMs;

    /// <summary>實際生效的逾時;設定為 0 或負數時回到預設值</summary>
    public int EffectiveHeartbeatTimeoutMs => HeartbeatTimeoutMs > 0 ? HeartbeatTimeoutMs : DefaultHeartbeatTimeoutMs;
}

public class Mt4InstanceConfig
{
    public string ExePath { get; set; } = string.Empty;
    public string LogDirectory { get; set; } = string.Empty;
}

/// <summary>
/// 設定快照。每次 config.json 重載都會產生新的 Config 物件,ConfigProvider 會把它當成「上一份正確設定」保存,
/// 多個監控執行緒同時讀取。請勿在程式中就地修改任何屬性,否則會影響所有讀到同一份快照的地方。
/// </summary>
public class Config
{
    // 預設值的唯一來源:屬性的預設值,以及「設定值 <= 0 時」的備援,都使用這些常數
    public const int DefaultTimeoutMs = 10000;
    public const int DefaultInitialDelayMs = 30000;
    public const int DefaultMaxRetries = 3;
    public const int DefaultRestartCooldownMs = 15000;
    public const int DefaultRestartStartupGraceMs = 30000;
    public const int DefaultRestartFlapWindowMs = 1800000;
    public const int DefaultRestartFlapMaxCount = 5;
    public const int DefaultAlertReminderIntervalMs = 3600000;

    public string? NodeId { get; set; } = "LifeguardNode";
    public required Dictionary<string, Mt4InstanceConfig> MT4Instances { get; set; } // 💡 升級為強型別字典
    public HaNodeClient? HaNodeClient { get; set; }
    public int TimeoutMilliseconds { get; set; } = DefaultTimeoutMs;
    public int InitialDelayMs { get; set; } = DefaultInitialDelayMs;
    public int MaxRetries { get; set; } = DefaultMaxRetries;
    public int RestartCooldownMs { get; set; } = DefaultRestartCooldownMs;
    public int RestartStartupGraceMs { get; set; } = DefaultRestartStartupGraceMs;   // 重啟成功後,等待第一個 heartbeat 的寬限期(預設 30 秒)
    public int RestartFlapWindowMs { get; set; } = DefaultRestartFlapWindowMs;       // 崩潰循環偵測視窗(預設 30 分鐘)
    public int RestartFlapMaxCount { get; set; } = DefaultRestartFlapMaxCount;       // 視窗內成功重啟達此次數 → 暫停自動重啟
    public int AlertReminderIntervalMs { get; set; } = DefaultAlertReminderIntervalMs;

    // 啟動 MT4 前把 config\experts.ini 的 AutoTrade 改成關閉(HA 節點用;由 HaNodeClient 決定何時打開)
    public bool ForceAutoTradeOffOnStart { get; set; } = false;

    // Dead / Flapping 之後恢復(心跳回來)時發 Telegram 通知
    public bool NotifyOnRecovery { get; set; } = true;
    public string? TelegramBotToken { get; set; }
    public string? TelegramChatId { get; set; }

    // 實際生效的值:設定為 0 或負數(或誤填)時回到預設值。監控程式一律讀這些,不再各自寫備援
    public int EffectiveTimeoutMs => TimeoutMilliseconds > 0 ? TimeoutMilliseconds : DefaultTimeoutMs;
    public int EffectiveInitialDelayMs => InitialDelayMs > 0 ? InitialDelayMs : DefaultInitialDelayMs;
    public int EffectiveMaxRetries => MaxRetries > 0 ? MaxRetries : DefaultMaxRetries;
    public int EffectiveRestartCooldownMs => RestartCooldownMs > 0 ? RestartCooldownMs : DefaultRestartCooldownMs;
    public int EffectiveRestartStartupGraceMs => RestartStartupGraceMs > 0 ? RestartStartupGraceMs : DefaultRestartStartupGraceMs;
    public int EffectiveRestartFlapWindowMs => RestartFlapWindowMs > 0 ? RestartFlapWindowMs : DefaultRestartFlapWindowMs;
    public int EffectiveRestartFlapMaxCount => RestartFlapMaxCount > 0 ? RestartFlapMaxCount : DefaultRestartFlapMaxCount;
    public int EffectiveAlertReminderIntervalMs => AlertReminderIntervalMs > 0 ? AlertReminderIntervalMs : DefaultAlertReminderIntervalMs;
}
