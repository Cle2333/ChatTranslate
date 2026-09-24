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

    /// <summary>日志保留天数。超过的 <c>app-yyyyMMdd.log</c> 会被删除。</summary>
    private const int RetentionDays = 14;

    /// <summary>本次进程是否已清理过过期日志（每次运行只做一次）。</summary>
    private static bool _pruned;

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

                // 首次写入时清理过期日志。
                // 日志按天分文件，没有轮转的话会逐日累积且永不删除——
                // 尤其在开了诊断日志之后（每次鼠标抬起都写），
                // 目录会持续膨胀，属于资源未回收。
                if (!_pruned)
                {
                    _pruned = true;
                    PruneOldLogs();
                }

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

    /// <summary>
    /// 删除超过保留期的日志文件。
    /// </summary>
    /// <remarks>
    /// 只认 <c>app-yyyyMMdd.log</c> 这个命名——认不出日期的文件一律不动，
    /// 避免误删用户放进来的东西。
    /// </remarks>
    private static void PruneOldLogs()
    {
        var cutoff = DateTime.Now.Date.AddDays(-RetentionDays);

        foreach (var path in System.IO.Directory.EnumerateFiles(LogDirectory, "app-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path);

            // 形如 app-20260925
            if (name.Length != 12 || !name.StartsWith("app-", StringComparison.Ordinal))
            {
                continue;
            }

            if (!DateTime.TryParseExact(
                    name[4..], "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var date))
            {
                continue;
            }

            if (date < cutoff)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // 文件被别人占用（比如正开着看），跳过即可
                }
            }
        }
    }
}
