using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Lifeguard;

/// <summary>單一 log 檔案的讀取狀態</summary>
internal sealed class TailState(string path, DateTime creationTimeUtc)
{
    public string Path { get; } = path;
    public DateTime CreationTimeUtc = creationTimeUtc;
    public long Position;                       // 已「消耗」的位元組數（含 Decoder 暫存的奇數位元組）
    // 編碼在第一次讀檔時依內容判斷後才建立:
    //   MQL4\Logs 是單一位元組(ANSI,無 BOM),終端機 logs 可能是 UTF-16LE(FF FE)。
    public Decoder? Decoder;
    public int BomLength;                       // 檔頭 BOM 的位元組數(讀取時跳過)
    public bool IsUtf16;
    public string Pending = string.Empty;       // 尚未遇到換行的半行
    public bool SkipFirstPartialLine;           // 從檔案中間開始讀時，丟掉第一個殘缺行
    public TimeSpan? IgnoreLinesBefore;          // 啟動掃描:時間早於此值的行不檢查(屬於上一個 MT4 程序)

    /// <summary>檔案被截斷或重建：從頭開始，所有解碼/行緩衝狀態一併歸零</summary>
    public void Rewind()
    {
        Position = 0;
        Decoder = null;                         // 重建的檔案重新判斷編碼
        BomLength = 0;
        IsUtf16 = false;
        Pending = string.Empty;
        SkipFirstPartialLine = false;
        IgnoreLinesBefore = null;
    }
}

/// <summary>MT4 log 檔的讀取、解碼與致命字串掃描(純邏輯,不依賴 logger,可單元測試)</summary>
internal static class Mt4LogReader
{
    // Monitor MT4 log files for fatal error signatures
    // by Claude and ChatGPT
    internal const int ChunkSize = 64 * 1024;
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    internal static readonly string[] RawFatalSignatures =
    [
        "requires active MQL5 account",
        "Initialization failed"
    ];

    internal static readonly string[] FatalSignatures =
            [.. RawFatalSignatures.Where(s => !string.IsNullOrWhiteSpace(s))];

    internal static readonly int MaxSignatureLength = FatalSignatures.Length > 0
        ? FatalSignatures.Max(s => s.Length)
        : 1;
    // MaxPendingChars 必須大於 MaxSignatureLength,否則
    // st.Pending[^(MaxSignatureLength - 1)..] 可能越界,例如:
    internal static readonly int MaxPendingChars = Math.Max(64 * 1024, MaxSignatureLength * 4);

    /// <summary>
    /// 啟動定位。
    /// processStartLocal 有值(MT4 已在執行)且檔名是 YYYYMMDD.log 時:
    ///   從檔頭讀,但只檢查「這個 MT4 程序啟動之後」寫出的行,
    ///   這樣 Lifeguard 比 MT4 晚啟動(或在寬限期內)寫出的 fatal 也抓得到,舊程序留下的 fatal 不會被誤認。
    /// 否則維持原本行為:從檔尾開始(scanBytes = 0)。
    /// </summary>
    /// <param name="startupScan">true = 這次會掃描 MT4 啟動後的既有內容</param>
    /// <returns>true = 啟動定位完成（含目錄內確實沒有檔案）；false = 暫時性失敗，需重試</returns>
    internal static bool TryCreateStartupState(string dir, int scanBytes, DateTime? processStartLocal,
        out TailState? state, out bool startupScan)
    {
        state = null;
        startupScan = false;
        if (!TryGetLatestLogFile(dir, out var file)) return false;
        if (file is null) return true; // 目錄內沒有 log：之後出現的新檔從 0 讀是正確的

        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var created = File.GetCreationTimeUtc(fs.SafeFileHandle);

            if (processStartLocal is { } since && TryGetLogDate(file, out var fileDate) && fileDate >= since.Date)
            {
                state = new TailState(file, created)
                {
                    Position = 0,
                    // 檔案日期晚於程序啟動日:整個檔案都是這個程序寫的,全部檢查
                    // 同一天:只檢查啟動時間之後的行(往前留 2 秒,避免時間精度造成漏看)
                    IgnoreLinesBefore = fileDate > since.Date
                        ? (TimeSpan?)null
                        : since.TimeOfDay > TimeSpan.FromSeconds(2) ? since.TimeOfDay - TimeSpan.FromSeconds(2) : TimeSpan.Zero
                };
                startupScan = true;
                return true;
            }

            long length = fs.Length;
            long start = scanBytes <= 0 ? length : Math.Max(0, length - scanBytes);

            bool utf16 = TryDetectEncoding(fs, out bool isUtf16, out int bomLength) && isUtf16;
            if (utf16) start &= ~1L;   // UTF-16 必須從偶數位置開始

            state = new TailState(file, created)
            {
                Position = start,
                SkipFirstPartialLine = scanBytes > 0 && start > 0 && !IsAtLineStart(fs, start, utf16, bomLength)
            };
            return true;
        }
        catch (FileNotFoundException) { return false; } // 剛好被刪除:下個週期再試
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>從 YYYYMMDD.log 的檔名取得日期</summary>
    internal static bool TryGetLogDate(string path, out DateTime date)
    {
        date = default;
        string name = Path.GetFileName(path);
        return IsDateLogName(name) &&
               DateTime.TryParseExact(name.AsSpan(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>MT4 log 行的格式:"&lt;等級&gt;\t&lt;HH:mm:ss.fff&gt;\t&lt;內容&gt;",取出時間欄位</summary>
    internal static bool TryParseLineTime(ReadOnlySpan<char> line, out TimeSpan time)
    {
        time = default;
        int tab1 = line.IndexOf('\t');
        if (tab1 < 0) return false;

        var rest = line[(tab1 + 1)..];
        int tab2 = rest.IndexOf('\t');
        if (tab2 < 0) return false;

        return TimeSpan.TryParseExact(rest[..tab2], @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out time);
    }

    /// <summary>
    /// 啟動掃描用:丟掉時間早於 IgnoreLinesBefore 的行。
    /// 找到第一個時間符合的行之後就解除過濾(log 時間是遞增的,之後的行都要檢查)。
    /// </summary>
    internal static string DropLinesBeforeThreshold(TailState st, string complete)
    {
        if (st.IgnoreLinesBefore is not { } threshold || complete.Length == 0) return complete;

        int pos = 0;
        while (pos < complete.Length)
        {
            int nl = complete.IndexOf('\n', pos);
            int end = nl < 0 ? complete.Length : nl + 1;

            if (TryParseLineTime(complete.AsSpan(pos, end - pos), out var t) && t >= threshold)
            {
                st.IgnoreLinesBefore = null;
                return complete[pos..];
            }

            pos = end;
        }

        return string.Empty;
    }

    /// <summary>
    /// 判斷 log 的編碼(只看檔頭,不移動 fs 的目前位置):
    ///   FF FE           → UTF-16LE(有 BOM)
    ///   EF BB BF        → UTF-8(有 BOM)
    ///   第二個位元組是 00 → UTF-16LE(無 BOM)
    ///   其他            → 單一位元組(MT4 的 MQL4\Logs 是 ANSI;簽名都是英文,用 UTF-8 解碼即可比對)
    /// </summary>
    /// <returns>false = 檔案太短還無法判斷,稍後再試</returns>
    internal static bool TryDetectEncoding(FileStream fs, out bool isUtf16, out int bomLength)
    {
        isUtf16 = false;
        bomLength = 0;

        Span<byte> head = stackalloc byte[3];
        long saved = fs.Position;
        fs.Seek(0, SeekOrigin.Begin);
        int n = 0;
        while (n < head.Length)
        {
            int r = fs.Read(head[n..]);
            if (r == 0) break;
            n += r;
        }
        fs.Seek(saved, SeekOrigin.Begin);

        if (n < 2) return false;

        if (head[0] == 0xFF && head[1] == 0xFE) { isUtf16 = true; bomLength = 2; return true; }
        if (head[0] == 0xEF && head[1] == 0xBB)
        {
            if (n < 3) return false;              // BOM 還沒寫完
            if (head[2] == 0xBF) { bomLength = 3; return true; }
        }

        isUtf16 = head[0] != 0x00 && head[1] == 0x00;
        return true;
    }

    /// <summary>檢查 start 前一個字元是否為換行（或剛好在 BOM 之後），是則 start 正好在行首</summary>
    internal static bool IsAtLineStart(FileStream fs, long start, bool isUtf16, int bomLength)
    {
        if (start <= bomLength) return true;

        int width = isUtf16 ? 2 : 1;
        if (start < width) return true;

        Span<byte> prev = stackalloc byte[2];
        fs.Seek(start - width, SeekOrigin.Begin);
        if (fs.Read(prev[..width]) < width) return false;

        return isUtf16
            ? prev[0] == 0x0A && prev[1] == 0x00   // '\n' (UTF-16LE)
            : prev[0] == 0x0A;                      // '\n'
    }

    /// <summary>
    /// 以固定 chunk 把檔案從 state.Position 讀到「開檔時的檔尾」，邊解碼邊掃描。
    /// 回傳 true 代表發現致命字串。
    /// 命中後仍會讀完到檔尾:否則剩下的舊內容(同一次崩潰的後續行)會在下一輪才被讀到,
    /// 若那時新程序已經啟動,就會把剛重啟好的 MT4 誤殺一次。
    /// </summary>
    internal static async Task<bool> ReadAndScanAsync(
        TailState st, byte[] buffer, char[] charBuffer, CancellationToken ct)
    {
        using var fs = await TryOpenAsync(st.Path, ct);
        if (fs is null) return false; // 檔案已消失或暫時打不開，下個週期再試

        // 截斷 / 刪除重建偵測
        DateTime created;

        try
        {
            created = File.GetCreationTimeUtc(st.Path);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        long end = fs.Length;

        if (created != st.CreationTimeUtc || end < st.Position)
        {
            st.Rewind();
            st.CreationTimeUtc = created;
        }

        if (st.Position == 0 && end < 2) return false; // 連 BOM 都還沒寫完，等下次
        if (st.Position >= end) return false;

        // 第一次讀這個檔案:依檔頭判斷編碼
        if (st.Decoder is null)
        {
            if (!TryDetectEncoding(fs, out bool isUtf16, out int bomLength)) return false;

            st.IsUtf16 = isUtf16;
            st.BomLength = bomLength;
            st.Decoder = isUtf16 ? Encoding.Unicode.GetDecoder() : Encoding.UTF8.GetDecoder();
        }

        Decoder decoder = st.Decoder;
        bool fatal = false;

        fs.Seek(st.Position, SeekOrigin.Begin);

        while (st.Position < end)
        {
            ct.ThrowIfCancellationRequested();

            int want = (int)Math.Min(buffer.Length, end - st.Position);
            int read = await ReadFullAsync(fs, buffer, want, ct); // 不假設一次讀完
            if (read == 0) break; // 讀取途中檔案被截斷，交給下個週期的截斷偵測

            // 跳過 BOM(只出現在檔頭)
            int offset = st.Position < st.BomLength ? (int)Math.Min(read, st.BomLength - st.Position) : 0;

            st.Position += read;

            // 有狀態的 Decoder：奇數位元組 / surrogate pair 跨 chunk 都會自動銜接
            int charCount = decoder.GetChars(buffer, offset, read - offset, charBuffer, 0, flush: false);
            if (charCount == 0) continue;

            fatal |= ProcessText(st, st.Pending + new string(charBuffer, 0, charCount));
        }

        return fatal;
    }

    /// <summary>只掃描完整的行，半行留到下個 chunk；回傳是否命中致命字串</summary>
    internal static bool ProcessText(TailState st, string combined)
    {
        int lastNl = combined.LastIndexOf('\n');

        string complete;
        if (lastNl < 0)
        {
            complete = string.Empty;
            st.Pending = combined;
        }
        else
        {
            complete = combined[..(lastNl + 1)];
            st.Pending = combined[(lastNl + 1)..];
        }

        // 從檔案中間開始讀：丟掉第一個換行以前的殘缺內容
        if (st.SkipFirstPartialLine)
        {
            if (lastNl < 0)
            {
                st.Pending = string.Empty; // 仍在殘缺行內，整段丟棄
                return false;
            }
            complete = complete[(complete.IndexOf('\n') + 1)..];
            st.SkipFirstPartialLine = false;
        }

        // 啟動掃描:跳過上一個 MT4 程序留下的行
        complete = DropLinesBeforeThreshold(st, complete);

        bool fatal = ContainsFatal(complete);

        // 單行異常過長（一直沒有換行）：先掃一次，再只保留可能跨界的尾端，避免無限成長
        if (st.Pending.Length > MaxPendingChars)
        {
            if (st.IgnoreLinesBefore is null)   // 啟動掃描還在略過舊行時不檢查
                fatal |= ContainsFatal(st.Pending);
            st.Pending = st.Pending[^(MaxSignatureLength - 1)..];
        }

        return fatal;
    }

    internal static bool ContainsFatal(string text)
    {
        if (text.Length == 0) return false;
        foreach (var sig in FatalSignatures)
            if (text.Contains(sig, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>讀滿 count 位元組或到 EOF 為止（Stream.ReadAsync 可能只回傳部分資料）</summary>
    internal static async Task<int> ReadFullAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(total, count - total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    internal static async Task<FileStream?> TryOpenAsync(string path, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) { await Task.Delay(50 * attempt, ct); }
            catch (UnauthorizedAccessException) { await Task.Delay(50 * attempt, ct); }
        }
        return null;
    }

    internal static async Task WaitForSignalOrTimeoutAsync(
        ChannelReader<bool> reader, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* 逾時 → 輪詢 */ }
    }

    // ---------------------------------------------------------------------
    // B. TryGetLatestLogFile
    // 符合 YYYYMMDD.log 的檔案優先,依檔名由新到舊;其餘檔案才依寫入時間。
    // ---------------------------------------------------------------------
    internal static bool IsDateLogName(string name) =>
        name.Length == 12 &&
        name.AsSpan(0, 8).IndexOfAnyExceptInRange('0', '9') < 0 &&
        name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

    internal static bool TryGetLatestLogFile(string dir, out string? latest)
    {
        latest = null;
        try
        {
            latest = new DirectoryInfo(dir).EnumerateFiles("*.log")
                .OrderByDescending(f => IsDateLogName(f.Name))
                .ThenByDescending(f => IsDateLogName(f.Name) ? f.Name : string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
            return true;
        }
        catch (DirectoryNotFoundException) { return true; }  // 目錄消失視為沒有檔案
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
