using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ChatTranslate;

/// <summary>应用入口：全局异常兜底 + 单实例。</summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：已有一个实例在跑时直接退出（第二个实例拉起的行为由系统把焦点给第一个）
        _singleInstanceMutex = new Mutex(true, @"Local\ChatTranslate.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("ChatTranslate 已经在运行了。", "ChatTranslate",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 兜底：任何未处理异常都要落盘，否则用户只看到程序消失
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show(
            $"程序遇到未处理的错误：\n\n{e.Exception.Message}\n\n详情已写入日志目录。",
            "ChatTranslate", MessageBoxButton.OK, MessageBoxImage.Error);

        // 标记已处理，避免整个进程直接崩掉——用户还有机会保存/复制内容
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
        catch
        {
            // 记录日志失败时不再抛异常，避免二次崩溃
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
