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

        // 强调色固定为品牌蓝，不跟随 Windows 系统强调色（理由见 AppTheme）。
        // 必须在这里应用：App.xaml 的资源字典在 InitializeComponent 阶段就已加载，
        // 此刻再写强调色资源，才是最终生效的那一份。
        //
        // 包一层兜底：这是纯观感调整。Apply 一旦抛异常（Wpf.Ui 资源缺失、版本不一致等），
        // OnStartup 会在 base.OnStartup 之前中断，首次启动就直接失败，
        // 用户只会看到程序无声消失——代价远大于"用回系统强调色"。
        try
        {
            Core.AppTheme.Apply();
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn($"应用强调色失败，回退系统强调色：{ex}");
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

        System.Windows.MessageBox.Show(
            $"程序遇到未处理的错误：\n\n{e.Exception.Message}\n\n详情已写入日志目录。",
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

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var directory = Data.AppPaths.LogsDirectory;
            Directory.CreateDirectory(directory);

            var file = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(file,
                $"""

                ==================== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====================
                {exception}

                """);
        }
        catch (Exception logFailure)
        {
            // 记录日志失败时不再抛异常（避免二次崩溃），但保留诊断线索：
            // 日志目录不可写时，至少调试器/调试输出里能看到原因。
            System.Diagnostics.Debug.WriteLine($"写入崩溃日志失败：{logFailure}");
            System.Diagnostics.Trace.WriteLine($"写入崩溃日志失败：{logFailure}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
