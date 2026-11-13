using Microsoft.Extensions.Logging;

namespace Lifeguard;

/// <summary>
/// 在 MT4 啟動前,把 &lt;MT4 資料夾&gt;\config\experts.ini 裡的 AutoTrade 旗標改成關閉。
/// experts.ini 是 MetaQuotes 未公開的二進位格式(實測:4388 bytes、開頭 90 01 00 00、
/// 位移 0x24 是 AutoTrade,0 = 關閉 / 1 = 開啟)。
/// 任何一項不符合就不修改,只記警告,避免 MT4 更新格式後把檔案改壞。
/// </summary>
internal static class Mt4AutoTrade
{
    private const int ExpectedSize = 4388;
    private const int AutoTradeOffset = 0x24;

    public static void ForceOff(string exePath, string instanceId, ILogger logger)
    {
        string? mt4Dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
        if (mt4Dir is null) return;

        string iniPath = Path.Combine(mt4Dir, "config", "experts.ini");

        try
        {
            if (!File.Exists(iniPath))
            {
                logger.LogWarning("[{InstanceId}] experts.ini not found, AutoTrade NOT forced OFF: {Path}", instanceId, iniPath);
                return;
            }

            byte[] bytes = File.ReadAllBytes(iniPath);

            bool formatOk = bytes.Length == ExpectedSize &&
                            bytes[0] == 0x90 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00 &&
                            bytes[AutoTradeOffset] <= 1;

            if (!formatOk)
            {
                logger.LogWarning("[{InstanceId}] experts.ini format not recognised (size {Size}). AutoTrade NOT forced OFF.",
                    instanceId, bytes.Length);
                return;
            }

            if (bytes[AutoTradeOffset] == 0)
            {
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("[{InstanceId}] AutoTrade is already OFF in experts.ini.", instanceId);
                return;
            }

            // 只就地改寫這 1 個位元組,不重寫整個檔案:
            // 不會截斷檔案,就算寫入當下斷電,最壞也只是這個位元組沒改到,檔案本身仍完整。
            // (比「寫暫存檔再 Move」更單純:不改變檔案身分與權限)
            using (var fs = new FileStream(iniPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                fs.Seek(AutoTradeOffset, SeekOrigin.Begin);
                fs.WriteByte(0);
                fs.Flush(flushToDisk: true);
            }

            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("[{InstanceId}] AutoTrade forced OFF in experts.ini before start.", instanceId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "[{InstanceId}] Failed to update experts.ini. AutoTrade NOT forced OFF.", instanceId);
        }
    }
}
