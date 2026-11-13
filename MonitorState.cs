using System.Collections.Concurrent;

namespace Lifeguard;

/// <summary>
/// 各監控共用的狀態(給狀態列顯示用)。
/// 監控只透過方法寫入,不直接碰集合或欄位;所有成員皆可跨執行緒使用。
/// </summary>
public sealed class MonitorState
{
    // 錯誤訊息在標題列的顯示時間
    private static readonly TimeSpan ErrorDisplayDuration = TimeSpan.FromSeconds(60);

    private long _lastErrorTicks = DateTime.MinValue.Ticks;
    private volatile bool _isHaNodeClientServerDown;
    private readonly ConcurrentDictionary<string, byte> _deadInstances = new();

    public bool IsHaNodeClientServerDown
    {
        get => _isHaNodeClientServerDown;
        set => _isHaNodeClientServerDown = value;
    }

    public int DeadInstanceCount => _deadInstances.Count;

    public void MarkError() => Interlocked.Exchange(ref _lastErrorTicks, DateTime.Now.Ticks);

    public void MarkInstanceDead(string instanceId) => _deadInstances.TryAdd(instanceId, 1);

    public void MarkInstanceAlive(string instanceId) => _deadInstances.TryRemove(instanceId, out _);

    public string GetStatusText(DateTime now)
    {
        if (_isHaNodeClientServerDown)
            return "HaNodeClient DOWN";

        int deadCount = _deadInstances.Count;

        if (deadCount > 0)
            return $"CRITICAL ({deadCount} EA Offline)";

        var lastError = new DateTime(Interlocked.Read(ref _lastErrorTicks));

        if (now - lastError <= ErrorDisplayDuration)
            return "ERROR DETECTED";

        return "ALL OK";
    }
}
