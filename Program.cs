using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using System.Text;

// 必須最先執行:Serilog 的相對路徑(Logs/...)要以程式所在資料夾解析,
// 從工作排程器、捷徑或其他資料夾啟動時才不會寫到錯的地方
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// 每個監控會在 WaitAny 上阻塞一條執行緒;執行緒池預設最小執行緒數等於 CPU 核心數,
// 核心數少、CPU 忙碌時,非同步工作(Telegram、log 掃描、冷卻計時)會排隊等執行緒
ThreadPool.SetMinThreads(workerThreads: 16, completionPortThreads: 16);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    // HttpClient 的 Information 等級會把完整請求網址(含 Telegram bot token)寫進 log
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}",
        theme: AnsiConsoleTheme.Code
    )
    .WriteTo.File("Logs/Lifeguard_Log_.txt",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

const string appMutexName = @"Global\LifeguardSystem_SingleInstance_Mutex";

Mutex? appMutex = null;
bool isNewInstance;
bool mutexAccessDenied = false;

try
{
    appMutex = new Mutex(
        initiallyOwned: true,
        name: appMutexName,
        createdNew: out isNewInstance);
}
catch (UnauthorizedAccessException ex)
{
    Log.Information(
        ex,
        "Unable to access the global mutex due to insufficient permissions. " +
        "Single-instance protection will treat this as an existing instance.");

    isNewInstance = false;
    mutexAccessDenied = true;
}

if (!isNewInstance)
{
    appMutex?.Dispose();

    Log.Warning(
        "⚠️ Lifeguard is already running. " +
        "Single-instance protection triggered. This instance will close automatically.");

    Log.CloseAndFlush();

    Console.ForegroundColor = ConsoleColor.Yellow;
    if (mutexAccessDenied)
    {
        // 鎖存在但沒有權限開啟:通常是另一個以系統管理員(或其他使用者)身分執行的 Lifeguard
        Console.WriteLine("\n[WARNING] Another Lifeguard (started as administrator or by another user) appears to be running.");
        Console.WriteLine("If you are sure none is running, start this one with 'Run as administrator'.");
    }
    else
    {
        Console.WriteLine("\n[WARNING] The Lifeguard system is already running.");
    }
    Console.WriteLine("To prevent conflicting monitoring logic, this window will automatically close in 3 seconds...");
    Console.ResetColor();

    Thread.Sleep(3000);
    return;
}

// 必須持有到程式結束:沒有這一行,appMutex 在上面的 if 之後就不再被使用,
// Release / AOT 的 JIT 會把它視為可回收,GC 一旦執行,終結器就會關閉 Mutex,
// 之後啟動的 Lifeguard 就擋不住了
using var mutexGuard = appMutex!;

bool fatalExit = false; // 致命錯誤時多停留幾秒,雙擊 .exe 才看得到訊息
try
{
    AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
    {
        var ex = args.ExceptionObject as Exception;
        Log.Fatal(ex, "🔥 [CRITICAL ERROR] An uncaught background exception occurred. Lifeguard is crashing!");
        Log.CloseAndFlush(); // 行程即將終止,Main 的 finally 不會執行,必須在這裡 flush
    };

    // 發射後不等待(_ = Task.Run(...))的工作若拋出例外,沒有人 await 它就會靜默消失。
    // 這裡至少留下紀錄(在該 Task 被 GC 回收時觸發,時間點會晚一些)。
    // .NET Core 之後預設不會因此終止程式,SetObserved() 只是明確表示已處理。
    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        Log.Error(e.Exception, "🔥 Unobserved task exception (fire-and-forget task faulted).");
        e.SetObserved();
    };

    try { if (!Console.IsOutputRedirected) Console.OutputEncoding = Encoding.UTF8; } catch { } // On error resume next

    // 關閉主控台快速編輯模式:避免在視窗內誤點滑鼠就讓 log 輸出(與監控執行緒)整個卡住
    if (OperatingSystem.IsWindows() && !Console.IsInputRedirected && !Lifeguard.ProcessNative.TryDisableConsoleQuickEdit())
        Log.Warning("Could not disable console QuickEdit mode. Clicking inside the console window may pause Lifeguard until Esc is pressed.");

    Console.WriteLine($"Starting Universal Lifeguard System (Worker Mode)...\n");
    Log.Information("🚀 Lifeguard Watchdog is starting up...");

    var builder = Host.CreateApplicationBuilder(args);

    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(Log.Logger);

    // .env 放在 config.json 之後加入:同名設定以 .env 為準(例如 TelegramBotToken)。
    // .env 不支援熱重載;修改後需重新啟動 Lifeguard。
    string envPath = Path.Combine(AppContext.BaseDirectory, ".env");
    builder.Configuration.SetBasePath(AppContext.BaseDirectory)
                         .AddJsonFile("config.json", optional: false, reloadOnChange: true)
                         .AddInMemoryCollection(Lifeguard.DotEnv.Load(envPath));

    builder.Services.Configure<Lifeguard.Config>(builder.Configuration);
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<Lifeguard.ConfigProvider>();
    builder.Services.AddSingleton<Lifeguard.MonitorState>();
    builder.Services.AddSingleton<Lifeguard.ConfigReloadSignal>();
    builder.Services.AddSingleton<Lifeguard.ProcessController>();
    builder.Services.AddSingleton<Lifeguard.StatusReporter>();
    builder.Services.AddSingleton<Lifeguard.Mt4LogWatcher>();
    builder.Services.AddSingleton<Lifeguard.Mt4Monitor>();
    builder.Services.AddSingleton<Lifeguard.HaNodeClientMonitor>();
    builder.Services.AddSingleton<Lifeguard.INotifier, Lifeguard.TelegramNotifier>();
    builder.Services.AddHostedService<Lifeguard.LifeguardWorker>();

    using var host = builder.Build();

    // 啟動時先驗證設定;若 config.json 型別錯誤會在此明確失敗
    var startupConfig = host.Services.GetRequiredService<Lifeguard.ConfigProvider>().Current;

    // Telegram 未設定:不是錯誤(程式照常監控),但要讓人一眼看到不會有通知
    bool noToken  = string.IsNullOrWhiteSpace(startupConfig.TelegramBotToken);
    bool noChatId = string.IsNullOrWhiteSpace(startupConfig.TelegramChatId);
    if (noToken || noChatId)
    {
        string missing = noToken && noChatId ? "TelegramBotToken and TelegramChatId"
                       : noToken ? "TelegramBotToken" : "TelegramChatId";
        Log.Warning("⚠️ Telegram notifications DISABLED: {Missing} is not set (check .env).", missing);
    }

    Log.Information("Lifeguard System Application Started.");
    host.Run();
}
catch (Lifeguard.ConfigInvalidException ex)
{
    Console.WriteLine($"Fatal Error: {ex.Message}");
    Log.Fatal("💥 {Message}", ex.Message);
    Environment.ExitCode = 1;
    fatalExit = true;
}
catch (Exception ex)
{
    Console.WriteLine($"Fatal Error: {ex.Message}");
    Log.Fatal(ex, "💥 [FATAL ERROR] Lifeguard Watchdog Main Thread terminated unexpectedly!");
    fatalExit = true;
}
finally
{
    Log.Information("🛑 Lifeguard has completely shut down.");
    Log.CloseAndFlush();

    if (fatalExit)
        Thread.Sleep(TimeSpan.FromSeconds(3));
}
