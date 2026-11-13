using System.Buffers;
using System.Runtime.InteropServices;

namespace Lifeguard;

// 需要 <AllowUnsafeBlocks>true</AllowUnsafeBlocks>(使用 char*)。

/// <summary>
/// 以 LibraryImport(編譯期產生 marshalling 程式碼)取得程序的完整執行檔路徑,
/// 相容 Native AOT。
/// </summary>
internal static unsafe partial class ProcessNative
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    // Windows 路徑長度上限(含長路徑)
    private const int ProcessPathBufferSize = 32768;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(
        uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        char* exeName,
        ref uint size);

    // ------------------------------------------------------------------
    // 主控台「快速編輯模式」(QuickEdit)
    // 在主控台視窗內點一下滑鼠就會進入選取狀態,輸出緩衝區被鎖住:
    // Console 寫入(包括 Serilog 的 Console sink)會阻塞,呼叫 log 的監控執行緒跟著卡住,
    // 直到有人按 Esc / Enter。用 RDP 查看 VPS 時很容易誤觸,所以啟動時關掉。
    // ------------------------------------------------------------------
    private const int StdInputHandle = -10;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;   // 修改 QuickEdit 時必須一併設定

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    /// <summary>關閉主控台的快速編輯模式;沒有主控台(或輸入被導向)時不做任何事</summary>
    public static bool TryDisableConsoleQuickEdit()
    {
        IntPtr handle = GetStdHandle(StdInputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        if (!GetConsoleMode(handle, out uint mode)) return false;

        uint newMode = (mode & ~EnableQuickEditMode) | EnableExtendedFlags;
        return newMode == mode || SetConsoleMode(handle, newMode);
    }

    // 只需要 PROCESS_QUERY_LIMITED_INFORMATION,不必列舉模組,對無回應的程序也能成功
    public static string? TryGetProcessPath(int pid)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        char[] buffer = ArrayPool<char>.Shared.Rent(ProcessPathBufferSize);
        try
        {
            uint size = ProcessPathBufferSize;

            fixed (char* p = buffer)
            {
                return QueryFullProcessImageName(handle, 0u, p, ref size)
                    ? new string(p, 0, (int)size)
                    : null;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
            CloseHandle(handle);
        }
    }
}
