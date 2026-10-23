using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using ChatTranslate.Core;

namespace ChatTranslate.Services;

/// <summary>一次 Ollama 重启的结果。</summary>
/// <param name="Success">服务已经在限定时间内恢复响应。</param>
/// <param name="Started">进程已经重新拉起；即使 <paramref name="Success"/> 为 false，也可能只是仍在初始化。</param>
/// <param name="Message">可直接展示给用户的中文说明。</param>
/// <param name="Busy">已有另一个重启（手动或自动）正在进行，本次没有执行。</param>
public sealed record OllamaRestartResult(
    bool Success, bool Started, string Message, bool Busy = false);

/// <summary>
/// 本机 Ollama 进程的探测、结束与启动。
/// </summary>
/// <remarks>
/// <para>Ollama 有时进程还在但 HTTP 已经无响应；这时不能再等翻译请求超时，
/// 必须结束进程并重新启动。这里把「怎么判断活着」「怎么杀干净」「怎么重新拉起」
/// 收在一处，主窗口的 15 秒看门狗与设置页的手动按钮共用同一套实现。</para>
///
/// <para>只处理本机地址：配置成远程 Ollama 时，杀掉本机进程既解决不了问题，
/// 还会误伤用户自己的服务。</para>
///
/// <para><b>重启会中断正在生成的翻译</b>，这是功能本身的一部分；调用方必须在界面上说清楚。</para>
/// </remarks>
public static class OllamaProcessManager
{
    // 先杀桌面托管进程，再杀服务进程：反过来时，仍在运行的 ollama app
    // 可能立刻把服务重新拉起来，导致随后的进程检查误判为"没杀干净"。
    private static readonly string[] ProcessNames = ["ollama app", "ollama"];

    private static readonly HttpClient ProbeHttp = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static readonly object Gate = new();
    private static bool _restarting;

    /// <summary>启动后等待 <c>/api/tags</c> 恢复响应的上限。</summary>
    /// <remarks>只是本次等待的上限，不是启动失败的判据；GPU 探测可能持续数分钟。</remarks>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(45);

    /// <summary>单次探活超时。看门狗与重启流程共用，避免两处超时值漂移。</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan PortReleaseDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>是否有重启正在进行（手动与自动之间互斥）。</summary>
    public static bool IsRestarting
    {
        get
        {
            lock (Gate)
            {
                return _restarting;
            }
        }
    }

    /// <summary>判断配置的地址是否指向本机。</summary>
    public static bool IsLocalHost(string? host)
    {
        if (!Uri.TryCreate(host?.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || uri.Host == "127.0.0.1"
               || uri.Host == "::1"
               || uri.Host == "[::1]";
    }

    /// <summary>本机是否还有 Ollama 进程在跑。</summary>
    public static bool IsRunning()
    {
        foreach (var name in ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // 进程列表查询失败时不能当成"没在跑"：返回 true 会让重启流程停在
                // 「无法结束进程」，比误判为已退出后重复拉起一个 Ollama 更安全。
                AppLog.Warn($"查询 {name} 进程失败：{ex.Message}");
                return true;
            }

            try
            {
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 本机 Ollama 进程里最晚的启动时间；没有进程或读不到时返回 null。
    /// </summary>
    /// <remarks>
    /// 用于区分「刚启动、正在做 GPU 探测」和「跑了很久后卡死」。前者本来就不响应 HTTP，
    /// 直接杀掉会把正常初始化打断（本机实测 GPU 探测约 6 分钟）。
    /// </remarks>
    public static DateTime? GetNewestStartTimeUtc()
    {
        DateTime? newest = null;

        foreach (var name in ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    var start = process.StartTime.ToUniversalTime();
                    if (newest is null || start > newest.Value)
                    {
                        newest = start;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // 读不到启动时间不影响探测，按未知处理
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return newest;
    }

    /// <summary>
    /// 探测 Ollama 的 <c>/api/tags</c> 是否能在限定时间内响应。
    /// </summary>
    /// <remarks>
    /// 用独立的 HttpClient 且关闭代理：本机 127.0.0.1 不该经过系统代理，
    /// 代理软件异常时也不能把本机探活误判成 Ollama 挂了。
    /// </remarks>
    public static async Task<bool> ProbeAsync(
        string host, TimeSpan timeout, CancellationToken ct = default)
    {
        var baseUrl = host?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            using var response = await ProbeHttp
                .GetAsync(baseUrl + "/api/tags", HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>
    /// 结束本机 Ollama 进程并重新启动，等待 <c>/api/tags</c> 恢复响应。
    /// </summary>
    /// <param name="host">当前配置的 Ollama 地址，必须指向本机。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<OllamaRestartResult> RestartAsync(
        string host, CancellationToken ct = default)
    {
        if (!IsLocalHost(host))
        {
            return new(false, false, "当前 Ollama 地址不是本机，不能重启本机进程");
        }

        // 进程管理器只会启动默认安装的 Ollama（监听 11434）。地址指向别的端口时，
        // 杀掉默认实例既解决不了问题，还会误伤用户本来正常的服务。
        if (!Uri.TryCreate(host.Trim(), UriKind.Absolute, out var target)
            || target.Port != 11434)
        {
            return new(false, false, "当前 Ollama 地址端口不是 11434，无法确认对应的本机实例，请手动重启或改回默认端口");
        }

        lock (Gate)
        {
            if (_restarting)
            {
                return new(false, false, "已经有重启正在进行，请稍候", Busy: true);
            }

            _restarting = true;
        }

        var started = false;
        try
        {
            var executable = FindExecutable();
            if (executable is null)
            {
                return new(false, false, "未找到 ollama.exe 或 ollama app.exe，请确认 Ollama 已安装");
            }

            AppLog.Info("正在结束 Ollama 进程");
            await Task.Run(KillProcesses, ct).ConfigureAwait(false);

            // 等端口释放。直接拉起新进程时，旧监听可能还没退干净。
            // 这里刻意不响应取消：进程已经结束，如果此刻退出就会把 Ollama 留在停止状态。
            await Task.Delay(PortReleaseDelay).ConfigureAwait(false);
            if (IsRunning())
            {
                return new(false, false, "无法结束 Ollama 进程（可能以管理员权限运行），请手动结束后重试");
            }

            AppLog.Info($"正在启动 Ollama：{executable.FileName}");
            StartProcess(executable);
            started = true;

            var deadline = DateTime.UtcNow + ReadyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await ProbeAsync(host, ProbeTimeout, ct).ConfigureAwait(false))
                {
                    return new(true, true, "Ollama 已重启");
                }

                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }

            // 进程如果启动后立刻退出（端口占用、安装损坏），不能再说"仍在初始化"。
            if (!IsRunning())
            {
                return new(false, false, "Ollama 启动后进程已退出，请检查安装是否损坏或端口是否被占用");
            }

            // 45 秒只是"这次等待"的上限，不是启动失败的判据：实测这台机器光 GPU 探测
            // 就可能持续约 6 分钟。把"已拉起但未就绪"单独报出来，调用方才不会误报失败。
            return new(false, true, "Ollama 已重新启动，但仍在初始化（GPU 探测可能持续数分钟），就绪后会自动恢复");
        }
        catch (OperationCanceledException)
        {
            return new(false, started, started ? "重启已取消，Ollama 已在后台启动" : "重启已取消");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"重启 Ollama 失败：{ex.Message}");
            return new(false, started, ex.Message);
        }
        finally
        {
            lock (Gate)
            {
                _restarting = false;
            }
        }
    }

    private sealed record LaunchTarget(string FileName, bool UseShellExecute, bool Serve);

    /// <summary>
    /// 找可执行文件：优先 Ollama 桌面应用，其次 PATH / 常见安装目录里的 ollama.exe。
    /// </summary>
    private static LaunchTarget? FindExecutable()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var appCandidates = new[]
        {
            Path.Combine(local, "Programs", "Ollama", "ollama app.exe"),
            Path.Combine(programFiles, "Ollama", "ollama app.exe"),
            Path.Combine(programFilesX86, "Ollama", "ollama app.exe"),
        };

        foreach (var candidate in appCandidates)
        {
            if (File.Exists(candidate))
            {
                return new LaunchTarget(candidate, UseShellExecute: true, Serve: false);
            }
        }

        foreach (var candidate in ExecutableCandidates())
        {
            if (File.Exists(candidate))
            {
                return new LaunchTarget(candidate, UseShellExecute: false, Serve: true);
            }
        }

        return null;
    }

    private static IEnumerable<string> ExecutableCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        yield return Path.Combine(local, "Programs", "Ollama", "ollama.exe");
        yield return Path.Combine(programFiles, "Ollama", "ollama.exe");
        yield return Path.Combine(programFilesX86, "Ollama", "ollama.exe");

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            yield return Path.Combine(trimmed, "ollama.exe");
        }
    }

    private static void KillProcesses()
    {
        foreach (var name in ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                AppLog.Warn($"枚举 {name} 进程失败：{ex.Message}");
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    AppLog.Info($"结束 {process.ProcessName}（PID {process.Id}）");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                    // 枚举后进程自己退出了：目标已达成
                }
                catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
                {
                    AppLog.Warn($"结束 {process.ProcessName}（PID {process.Id}）失败：{ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private static void StartProcess(LaunchTarget target)
    {
        var directory = Path.GetDirectoryName(target.FileName);
        var startInfo = new ProcessStartInfo
        {
            FileName = target.FileName,
            WorkingDirectory = directory ?? string.Empty,
            UseShellExecute = target.UseShellExecute,
            CreateNoWindow = !target.UseShellExecute,
            WindowStyle = target.UseShellExecute ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
        };

        if (target.Serve)
        {
            startInfo.ArgumentList.Add("serve");
        }

        var process = Process.Start(startInfo);
        process?.Dispose();
    }
}
