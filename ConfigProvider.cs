using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lifeguard;

/// <summary>
/// 包裝 IOptionsMonitor&lt;Config&gt;:設定綁定失敗(例如 "MaxRetries": "ABC")時,
/// 回傳上一份正確的設定,而不是讓每個呼叫端都丟例外。
/// 同一個錯誤訊息只記錄一次(不印 stack trace),避免洗版。
/// </summary>
public sealed class ConfigProvider
{
    private readonly IOptionsMonitor<Config> _monitor;
    private readonly ILogger<ConfigProvider> _logger;
    private volatile Config _lastGood;
    private string? _lastError;

    public ConfigProvider(IOptionsMonitor<Config> monitor, ILogger<ConfigProvider> logger)
    {
        _monitor = monitor;
        _logger = logger;

        try
        {
            _lastGood = monitor.CurrentValue;
        }
        catch (Exception ex)
        {
            // 啟動時就壞掉:沒有舊設定可用,讓程式帶著明確訊息結束
            throw new ConfigInvalidException($"config.json is invalid at startup: {Describe(ex)}", ex);
        }
    }

    public Config Current
    {
        get
        {
            try
            {
                var value = _monitor.CurrentValue;
                _lastGood = value;
                if (_lastError is not null)
                {
                    _lastError = null;
                    _logger.LogInformation("config.json is valid again. Using the new settings.");
                }
                return value;
            }
            catch (Exception ex)
            {
                string message = Describe(ex);
                if (message != _lastError)
                {
                    _lastError = message;
                    _logger.LogError("config.json is invalid, keeping last good settings: {Reason}", message);
                }
                return _lastGood;
            }
        }
    }

    // 取最內層例外的簡短訊息,例如:Failed to convert configuration value 'ABC' at 'MaxRetries' ...
    private static string Describe(Exception ex) =>
        ex is InvalidOperationException { InnerException: not null } ? ex.Message.Split('\n')[0].Trim() : ex.Message;
}

/// <summary>啟動時設定檔無法使用。Program 只印一行訊息,不需要 stack trace</summary>
public sealed class ConfigInvalidException(string message, Exception inner) : Exception(message, inner);
