using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lifeguard;

/// <summary>通知管理員(目前實作為 Telegram)。監控類別只依賴這個介面</summary>
public interface INotifier
{
    /// <param name="message">Telegram HTML 格式的訊息;動態內容需由呼叫端先做 HtmlEncode</param>
    Task NotifyAsync(string message, CancellationToken token);
}

public sealed partial class TelegramNotifier(
    ConfigProvider configProvider,
    ILogger<TelegramNotifier> logger,
    IHttpClientFactory httpClientFactory) : INotifier
{
    // 單次請求逾時:CPU 吃緊(例如 MT4 剛啟動)時,TLS 握手與連線建立可能很慢,10 秒太短
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // 失敗時最多嘗試 3 次,第 1、2 次失敗後分別等待 2 秒與 5 秒
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    // 不是 Telegram 支援實體開頭的「裸 &」(例如 A & B、R&D、AT&T;)。
    // Telegram 只認 &amp; &lt; &gt; &quot; 與數字字元引用;其他(例如 &T; &copy;)會讓整則訊息被拒絕(400)。
    // 白名單必須和 IsEntityBody 一致。
    [GeneratedRegex(@"&(?!(?:amp|lt|gt|quot|#[0-9]+|#[xX][0-9a-fA-F]+);)")]
    private static partial Regex BareAmpersandRegex();

    /// <summary>把裸 & 轉成 &amp;amp;,已經是實體(&amp;amp; &amp;lt; &amp;#39; …)的不動</summary>
    internal static string EscapeBareAmpersands(string html) => BareAmpersandRegex().Replace(html, "&amp;");

    /// <summary>&amp; 與 ; 之間的內容是否為 Telegram 支援的實體(必須和 BareAmpersandRegex 的白名單一致)</summary>
    private static bool IsEntityBody(ReadOnlySpan<char> s)
    {
        if (s.Length == 0) return false;
        if (s[0] == '#')
        {
            var digits = s[1..];
            if (digits.Length > 0 && (digits[0] == 'x' || digits[0] == 'X'))
                return digits.Length > 1 && digits[1..].IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
            return digits.Length > 0 && digits.IndexOfAnyExceptInRange('0', '9') < 0;
        }
        return s is "amp" or "lt" or "gt" or "quot";
    }

    private const string TruncateSuffix = "\n\n…（消息过长已截断）";

    // 預留給補上的結尾標籤(例如 </b></i>)
    private const int ClosingTagReserve = 32;

    /// <summary>
    /// 安全截斷 Telegram HTML:
    ///  - 只在「不在標籤內、不在 &amp;xxx; 實體內、不切開 emoji(surrogate pair)」的位置切
    ///  - 切點之前還沒關閉的標籤,依相反順序補上結尾標籤
    /// 結果長度不超過 maxLength(含截斷後綴)。
    /// </summary>
    internal static string TruncateHtml(string html, int maxLength)
    {
        if (html.Length <= maxLength) return html;

        int limit = maxLength - TruncateSuffix.Length - ClosingTagReserve;
        var openTags = new List<string>();
        int i = 0;

        // 每次只前進一個完整單位(標籤 / 實體 / 字元);放不下就停在它之前,
        // 所以迴圈結束時 i 一定是安全切點,openTags 也正好是這個切點的狀態
        while (i < limit)
        {
            char c = html[i];

            if (c == '<')
            {
                int end = html.IndexOf('>', i);
                if (end < 0 || end >= limit) break;

                string tag = html.Substring(i + 1, end - i - 1).Trim();
                if (tag.StartsWith('/'))
                {
                    string name = tag[1..].Trim().ToLowerInvariant();
                    int idx = openTags.LastIndexOf(name);
                    if (idx >= 0) openTags.RemoveAt(idx);
                }
                else if (tag.Length > 0)
                {
                    int sp = tag.IndexOfAny([' ', '\t', '\n']);
                    openTags.Add((sp < 0 ? tag : tag[..sp]).ToLowerInvariant());
                }

                i = end + 1;
            }
            else if (c == '&')
            {
                int end = html.IndexOf(';', i);
                bool isEntity = end > i && end - i <= 10 && IsEntityBody(html.AsSpan(i + 1, end - i - 1));

                if (!isEntity)
                {
                    i++;                       // 裸 &:當成一般字元(呼叫端應已轉義,這裡只求不誤切)
                }
                else
                {
                    if (end >= limit) break;   // 實體放不下:停在它之前,不切開
                    i = end + 1;
                }
            }
            else
            {
                int width = char.IsHighSurrogate(c) && i + 1 < html.Length ? 2 : 1;
                if (i + width > limit) break;
                i += width;
            }
        }

        var sb = new StringBuilder(html, 0, i, maxLength);
        for (int t = openTags.Count - 1; t >= 0; t--)
            sb.Append("</").Append(openTags[t]).Append('>');
        sb.Append(TruncateSuffix);
        return sb.ToString();
    }

    // by Grok and ChatGPT
    /// <inheritdoc />
    public async Task NotifyAsync(string message, CancellationToken token)
    {
        var config = configProvider.Current;
        if (string.IsNullOrWhiteSpace(config.TelegramBotToken) ||
            string.IsNullOrWhiteSpace(config.TelegramChatId))
        {
            logger.LogDebug("Telegram notification is not configured.");
            return;
        }

        // 节点名称处理
        string nodeName = string.IsNullOrWhiteSpace(config.NodeId)
            ? "Unknown-Node"
            : config.NodeId.Trim();

        // 限制节点名称长度，避免过长
        const int MaxNodeNameLength = 64;
        if (nodeName.Length > MaxNodeNameLength)
        {
            nodeName = nodeName[..MaxNodeNameLength] + "…";
        }

        // 節點名稱由本方法加上 <b> 標籤,所以必須轉義。
        // message 由呼叫端提供,本身已是 Telegram HTML(含 <b>),不能再轉義,否則標籤會原樣顯示。
        // 呼叫端必須自行對動態內容(例如 instanceId)做 WebUtility.HtmlEncode。
        string safeNodeName = WebUtility.HtmlEncode(nodeName);
        string safeMessage = message ?? string.Empty;

        string finalMessage =
            $"🖥️ <b>主機: {safeNodeName}</b>\n{safeMessage}";

        // 保險:呼叫端漏掉轉義的裸 & 會讓 Telegram 拒收整則告警,這裡統一補上
        finalMessage = EscapeBareAmpersands(finalMessage);

        // Telegram 单条消息上限为 4096 字符，这里保守限制为 4000
        const int MaxMessageLength = 4000;

        if (finalMessage.Length > MaxMessageLength)
        {
            int originalLength = finalMessage.Length;

            // 不能直接切字串:切在 <b> 或 &amp; 中間、或留下沒關閉的 <b>,Telegram 都會回 400,整則通知失敗
            finalMessage = TruncateHtml(finalMessage, MaxMessageLength);

            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(
                    "Telegram message truncated due to length limit. Original length: {Length}",
                    originalLength);
        }

        string url =
            $"https://api.telegram.org/bot{config.TelegramBotToken}/sendMessage";

        var payload = new TelegramMessagePayload
        {
            ChatId = config.TelegramChatId,
            Text = finalMessage,
            ParseMode = "HTML"
        };

        string jsonString = JsonSerializer.Serialize(
            payload,
            LifeguardJsonContext.Default.TelegramMessagePayload);

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var sw = Stopwatch.StartNew();
            bool retryable;

            try
            {
                // StringContent 送出後會被釋放,不能跨重試重用,每次重新建立
                using var content = new StringContent(
                    jsonString,
                    Encoding.UTF8,
                    "application/json");

                var client = httpClientFactory.CreateClient();

                // 設置單次請求逾時,同時尊重外部 CancellationToken
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(RequestTimeout);

                using HttpResponseMessage response = await client.PostAsync(url, content, timeoutCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation(
                        "Telegram notification sent in {ElapsedMs} ms (attempt {Attempt}/{MaxAttempts}).",
                        sw.ElapsedMilliseconds, attempt, MaxAttempts);
                    return;
                }

                string responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                logger.LogWarning(
                    "Telegram notification failed after {ElapsedMs} ms (attempt {Attempt}/{MaxAttempts}). StatusCode: {StatusCode}, Response: {Response}",
                    sw.ElapsedMilliseconds, attempt, MaxAttempts, response.StatusCode, responseBody);

                // 429 與 5xx 值得重試;其他 4xx(token / chat_id 錯誤、格式錯誤)重試也不會成功
                retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                            || (int)response.StatusCode >= 500;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 外部真正取消
                logger.LogDebug("Telegram notification was cancelled.");
                return;
            }
            catch (OperationCanceledException)
            {
                // 逾時導致的取消
                logger.LogWarning(
                    "Telegram notification timed out after {ElapsedMs} ms (attempt {Attempt}/{MaxAttempts}).",
                    sw.ElapsedMilliseconds, attempt, MaxAttempts);
                retryable = true;
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(
                    ex,
                    "Telegram notification failed due to HTTP error after {ElapsedMs} ms (attempt {Attempt}/{MaxAttempts}).",
                    sw.ElapsedMilliseconds, attempt, MaxAttempts);
                retryable = true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error while sending Telegram notification.");
                return;
            }

            if (!retryable || attempt == MaxAttempts)
                return;

            try
            {
                await Task.Delay(RetryDelays[attempt - 1], token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
