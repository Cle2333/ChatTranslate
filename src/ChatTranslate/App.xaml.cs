using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ChatTranslate;

/// <summary>应用入口：全局异常兜底 + 单实例。</summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    /// <summary>
    /// 在构造函数里注册全局异常处理。
    ///
    /// <para>不能放到 OnStartup：WPF 的启动顺序是 new App() → InitializeComponent()
    /// （加载 App.xaml 里的主题与控件资源）→ Run() → OnStartup，
    /// 若在 InitializeComponent 阶段抛异常（资源字典损坏等），OnStartup 里的订阅根本还没生效，
    /// 异常会直接让程序无声消失。</para>
    /// </summary>
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：已有一个实例在跑时直接退出
        _singleInstanceMutex = new Mutex(true, @"Local\ChatTranslate.SingleInstance", out var created);
        if (!created)
        {
            System.Windows.MessageBox.Show("ChatTranslate 已经在运行了。", "ChatTranslate",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 主题与强调色（理由见 AppTheme）。
        // 必须在这里应用：App.xaml 的资源字典在 InitializeComponent 阶段就已加载，
        // 此刻再写主题与强调色资源，才是最终生效的那一份。
        //
        // 包一层兜底：这是纯观感调整。一旦抛异常（Wpf.Ui 资源缺失、配置读不出来等），
        // OnStartup 会在 base.OnStartup 之前中断，首次启动就直接失败，
        // 用户只会看到程序无声消失——代价远大于"用回默认深色"。
        try
        {
            // 主题要在主窗口创建之前定下来：MainWindow 由 App.xaml 的 StartupUri
            // 在 base.OnStartup 里创建，先定主题则窗口首帧就是目标配色，
            // 不会先深色再闪一下变亮色。App.xaml 里的 ThemesDictionary 是这之前的初始值。
            var config = new Data.ConfigStore().Load();
            Core.AppTheme.Apply(config.Theme);
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn($"应用主题失败，沿用默认深色：{ex}");
        }

        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);

        // 致命异常（内存耗尽、内存访问违规）无法安全恢复：此时进程的内存与 UI 状态可能已损坏，
        // 强行继续运行会进入「弹窗 → 异常 → 再弹窗」的循环，还可能让进行中的原子写文件、
        // SQLite 落库留下不一致状态。这类异常必须向上传播，由系统终止进程。
        var fatal = e.Exception is OutOfMemoryException
            or AccessViolationException
            or StackOverflowException;

        if (fatal)
        {
            return;
        }

        // 外层往往是 XamlParseException 这类包装，真正的原因才在内层
        // （比如"会话库打不开"）。只报外层，用户没法描述、我也无从定位。
        var cause = Innermost(e.Exception);
        var causeText = ReferenceEquals(cause, e.Exception)
            ? e.Exception.Message
            : $"{e.Exception.Message}\n\n原因：{cause.GetType().Name}: {cause.Message}";

        var where = _lastCrashLogPath is null
            ? "（日志目录也写不进去，详细堆栈未能保存）"
            : $"详情已写入：{_lastCrashLogPath}";

        System.Windows.MessageBox.Show(
            $"程序遇到未处理的错误：\n\n{causeText}\n\n{where}",
            "ChatTranslate", MessageBoxButton.OK, MessageBoxImage.Error);

        // 仅对可恢复异常兜底，让用户还有机会保存/复制内容
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            WriteCrashLog(ex);
        }
    }

    private static string? _lastCrashLogPath;

    private static void WriteCrashLog(Exception exception)
    {
        // 数据目录本身可能就写不进去（安全软件拦截、只读），而这恰恰是最需要日志的时候：
        // 先写数据目录，失败就退到用户临时目录。否则「详情已写入日志目录」就是一句空话，
        // 用户报故障时拿不出任何堆栈，只能靠猜。
        var directories = new[]
        {
            Data.AppPaths.LogsDirectory,
            Path.Combine(Path.GetTempPath(), "ChatTranslate-logs"),
        };

        foreach (var directory in directories)
        {
            try
            {
                Directory.CreateDirectory(directory);

                var file = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file,
                    $"""

                    ==================== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====================
                    {exception}

                    """);

                _lastCrashLogPath = file;
                return;
            }
            catch (Exception logFailure)
            {
                // 记录日志失败时不再抛异常（避免二次崩溃），但保留诊断线索
                System.Diagnostics.Debug.WriteLine($"写入崩溃日志失败（{directory}）：{logFailure}");
                System.Diagnostics.Trace.WriteLine($"写入崩溃日志失败（{directory}）：{logFailure}");
            }
        }

        _lastCrashLogPath = null;
    }

    /// <summary>
    /// 取最内层异常。
    /// </summary>
    /// <remarks>
    /// XAML 加载失败会被包成 XamlParseException，真正的原因（比如数据库打不开）只在
    /// InnerException 里。弹窗只显示外层消息的话，用户看到的是一句无从下手的话，
    /// 也没法转述给我。
    /// </remarks>
    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException is { } inner)
        {
            exception = inner;
        }

        return exception;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
