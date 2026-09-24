using System.IO;
using System.Text;

namespace ChatTranslate.Core;

/// <summary>
/// 轻量文件日志。写入应用数据目录的 logs/，按天分文件。
///
/// <para><b>为什么需要它</b>：划词是被动触发的，没有点击就没有反馈。
/// 出问题时用户只会看到"划词没反应"，既无法自助排查、也无法向我描述现象。
/// 有了日志才能回答"钩子装上了吗 / 取词取到了吗 / 被哪条规则拦下的"。</para>
///
/// <para><b>永不抛异常</b>：日志失败绝不能影响主流程。</para>
/// </summary>
public static class AppLog
{
    // net8.0 没有 System.Threading.Lock（.NET 9 才有），用 object 作监视器
    private static readonly object Gate = new();

    // 注意不要把这个字段命名为 Directory：会遮蔽 System.IO.Directory
    private static readonly string LogDirectory = Data.AppPaths.LogsDirectory;

    /// <summary>是否记录高频诊断信息（每次鼠标抬起等）。默认关闭。</summary>
    public static bool Verbose { get; set; }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    /// <summary>诊断信息，仅在 <see cref="Verbose"/> 打开时记录。</summary>
    public static void Trace(string message)
    {
        if (Verbose)
        {
            Write("TRACE", message);
        }
    }

    public static void Error(string message, Exception? exception = null)
    {
        var text = exception is null
            ? message
            : $"{message} :: {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}";

        Write("ERROR", text);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(LogDirectory);
                var file = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");

                var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不进去（目录只读、磁盘满等）时静默放弃，
            // 绝不能因为日志问题打断翻译主流程
        }
    }
}
