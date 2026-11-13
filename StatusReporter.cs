using Microsoft.Extensions.Logging;

namespace Lifeguard;

/// <summary>定期更新 Console 標題列,顯示目前整體狀態(僅 Windows)</summary>
public sealed class StatusReporter(MonitorState state, ILogger<StatusReporter> logger)
{
    private static readonly TimeSpan StatusReportInterval = TimeSpan.FromSeconds(1);

    // by Claude and ChatGPT
    public async Task RunAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var timer = new PeriodicTimer(StatusReportInterval);

        try
        {
            do
            {
                try
                {
                    Console.Title = BuildTitle(DateTime.Now);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Console.Title 更新失敗不應影響整個狀態回報循環
                    logger.LogWarning(ex, "Failed to update console title");
                }
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 正常關閉
        }
    }

    private string BuildTitle(DateTime now)
    {
        return $"Lifeguard: {state.GetStatusText(now)} | Time: {now:HH:mm:ss}";
    }
}
