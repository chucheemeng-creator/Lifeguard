using Microsoft.Extensions.Logging;
using System.Text;
using System.Threading.Channels;
using static Lifeguard.Mt4LogReader;

namespace Lifeguard;

/// <summary>
/// 監看一個 MT4 的 log 資料夾:FileSystemWatcher + 輪詢保底,
/// 讀到 fatal 簽名就 Set errorSignal,由 Mt4Monitor 決定是否重啟。
/// 讀檔、解碼、比對的純邏輯在 Mt4LogReader。
/// </summary>
public sealed class Mt4LogWatcher(ILogger<Mt4LogWatcher> logger)
{
    // ---------------------------------------------------------------------
    // WatchMt4LogFile
    // errorSignal:由 CancellationTokenSource 改為 AutoResetEvent(可重複觸發)
    // startupScanBytes:預設 0 = 從檔尾開始,不回掃舊內容
    // getProcessStartLocal:MT4 已在執行時,改為檢查「該程序啟動後」寫出的既有內容
    // ---------------------------------------------------------------------
    public async Task WatchAsync(
        string instanceId,
        string logDirectory,
        AutoResetEvent errorSignal,
        Func<DateTime?> getProcessStartLocal,
        CancellationToken cancellationToken,
        int startupScanBytes = 0)
    {
        if (string.IsNullOrEmpty(logDirectory))
        {
            logger.LogWarning("[{InstanceId}] Log directory is not configured. Log watcher disabled.", instanceId);
            return;
        }

        try
        {
            // 目錄暫時不存在(MT4 尚未建立)→ 等它出現,而不是永久放棄
            bool warned = false;
            while (!Directory.Exists(logDirectory))
            {
                if (!warned)
                {
                    logger.LogWarning("[{InstanceId}] Log directory does not exist yet, waiting: {Path}", instanceId, logDirectory);
                    warned = true;
                }
                await Task.Delay(PollInterval, cancellationToken);
            }

            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("[{InstanceId}] Log FileWatcher activated on target path.", instanceId);

            var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true
            });

            using var watcher = new FileSystemWatcher(logDirectory, "*.log")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                InternalBufferSize = 65536
            };

            void Notify() => signal.Writer.TryWrite(true);
            watcher.Changed += (_, _) => Notify();
            watcher.Created += (_, _) => Notify();
            watcher.Renamed += (_, _) => Notify();
            watcher.Deleted += (_, _) => Notify();
            watcher.Error += (_, e) =>
            {
                logger.LogWarning(e.GetException(), "[{InstanceId}] FileSystemWatcher error, forcing re-scan.", instanceId);
                Notify();
            };
            watcher.EnableRaisingEvents = true;

            var buffer = new byte[ChunkSize];
            // 依 UTF-8 計算(單一位元組檔案一個位元組就是一個字,比 UTF-16 需要的空間大)
            var charBuffer = new char[Encoding.UTF8.GetMaxCharCount(ChunkSize)];

            // 同一次崩潰常會連續寫多行 fatal,抑制視窗內只通知一次。
            // 視窗必須「短於」舊程序被殺到新程序啟動之間的時間(ProcessController 的 ReleaseDelayAfterKill = 3 秒),
            // 否則新程序一啟動就寫出的 fatal 會落在視窗內被吃掉,而那一行已被讀過,不會再重讀。
            // 重複訊號不需要靠長視窗擋:同一次讀取內的多行只會觸發一次;跨輪詢讀到的舊程序殘留訊號,
            // 會在 Mt4Monitor 的 beforeStart(舊程序已死、新程序未啟動)被 logErrorEvent.Reset() 清掉。
            var suppressWindow = TimeSpan.FromSeconds(2);
            DateTime suppressUntilUtc = DateTime.MinValue;

            void RaiseFatal()
            {
                var now = DateTime.UtcNow;
                if (now < suppressUntilUtc)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug("[{InstanceId}] Fatal signature suppressed (within window).", instanceId);
                    return;
                }

                suppressUntilUtc = now + suppressWindow;

                if (logger.IsEnabled(LogLevel.Critical))
                    logger.LogCritical("🚨 [LogWatcher] Fatal MT4 log signature detected for [{InstanceId}].", instanceId);

                try { errorSignal.Set(); }
                catch (ObjectDisposedException) { }
            }

            bool startupResolved = false;
            TailState? current = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    bool fatal = false;

                    if (!startupResolved)
                    {
                        // 1) 啟動:定位到最新檔案。
                        //    MT4 已在執行 → 檢查它啟動後寫出的內容(Lifeguard 比 MT4 晚啟動、或在寬限期內寫出的 fatal 都抓得到)
                        //    MT4 沒在執行 → 從檔尾開始(之後由 Lifeguard 啟動的 MT4 寫出的內容自然會被讀到)
                        DateTime? processStart = getProcessStartLocal();
                        startupResolved = TryCreateStartupState(logDirectory, startupScanBytes, processStart, out current, out bool startupScan);

                        if (startupResolved && startupScan && logger.IsEnabled(LogLevel.Information))
                            logger.LogInformation("[{InstanceId}] Startup log scan: checking lines written since MT4 started ({StartTime:yyyy-MM-dd HH:mm:ss}).",
                                instanceId, processStart);

                        if (startupResolved && current is not null)
                            fatal |= await ReadAndScanAsync(current, buffer, charBuffer, cancellationToken);
                    }
                    else if (TryGetLatestLogFile(logDirectory, out var latest))
                    {
                        if (latest is null)
                        {
                            current = null; // 確認目錄內真的沒有 log,之後出現新檔才從頭讀
                        }
                        else
                        {
                            // 換檔:先把舊檔案讀到 EOF(避免漏掉最後幾行),再切到新檔從頭讀
                            if (current is not null &&
                                !string.Equals(latest, current.Path, StringComparison.OrdinalIgnoreCase))
                            {
                                fatal |= await ReadAndScanAsync(current, buffer, charBuffer, cancellationToken);
                                current = null;
                            }

                            current ??= new TailState(latest, File.GetCreationTimeUtc(latest)); // Position = 0

                            fatal |= await ReadAndScanAsync(current, buffer, charBuffer, cancellationToken);
                        }
                    }
                    // TryGetLatestLogFile 暫時性失敗:保留 current 與 Position,下個週期再試

                    if (fatal) RaiseFatal();   // 通知後繼續監聽,不 return
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[{InstanceId}] Log watcher iteration failed, will retry.", instanceId);

                    try { await Task.Delay(PollInterval, cancellationToken); }   // 避免例外反覆時空轉
                    catch (OperationCanceledException) { break; }
                }

                // 2) 等待事件 or 輪詢保底
                try { await WaitForSignalOrTimeoutAsync(signal.Reader, PollInterval, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常關閉
        }
    }
}
