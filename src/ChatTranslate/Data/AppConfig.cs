using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatTranslate.Data;

/// <summary>
/// 应用配置。
///
/// <para>新增配置项时<b>务必给默认值</b>：反序列化旧配置文件时缺失的字段会取默认值，
/// 这样老配置不会因为加了新字段而失效。</para>
/// </summary>
public sealed class AppConfig
{
    /// <summary>Ollama 服务地址。</summary>
    public string OllamaHost { get; set; } = "http://127.0.0.1:11434";

    /// <summary>翻译模型名。</summary>
    public string Model { get; set; } = "hy-mt2:7b-q4km";

    /// <summary>上下文窗口大小。</summary>
    public int NumCtx { get; set; } = 8192;

    /// <summary>输入语言代码；"auto" 表示自动检测。</summary>
    public string SourceLanguage { get; set; } = "auto";

    /// <summary>目标语言代码（见 Languages）。</summary>
    public string TargetLanguage { get; set; } = "zh";

    /// <summary>划词翻译开关。</summary>
    public bool SelectionEnabled { get; set; }

    /// <summary>
    /// 是否记录高频诊断日志（每次鼠标抬起、取词结果等）。
    /// 排查"划词没反应"时打开，平时关闭以免日志膨胀。
    /// </summary>
    public bool DiagnosticLogging { get; set; }

    /// <summary>划词防抖延迟（毫秒）。</summary>
    public int SelectionDelayMs { get; set; } = 350;

    /// <summary>
    /// 划词要排除的进程名（不含 .exe，不区分大小写）。
    /// 默认排除终端与密码管理器：前者选中的文本几乎总是命令而不是待翻译内容，
    /// 后者涉及敏感信息。
    /// </summary>
    public List<string> SelectionBlacklist { get; set; } =
    [
        "WindowsTerminal",
        "powershell",
        "pwsh",
        "cmd",
        "conhost",
        "KeePass",
        "KeePassXC",
        "1Password",
    ];

    /// <summary>
    /// 修正手工编辑配置后可能出现的不合法值。
    /// </summary>
    /// <remarks>
    /// 配置文件是明文 JSON，用户（和我排查问题时）都会直接改它。
    /// 显式写 <c>null</c>、负数、越界值都能让程序在运行时崩掉，
    /// 而这类崩溃发生在启动路径上、用户根本无从判断原因——
    /// 所以读取后统一收敛一次，比在每个使用点做防御更可靠。
    /// </remarks>
    public void Normalize()
    {
        SelectionBlacklist ??= [];
        SelectionBlacklist.RemoveAll(entry => string.IsNullOrWhiteSpace(entry));
        SelectionDelayMs = Math.Clamp(SelectionDelayMs, 0, 5000);
        NumCtx = Math.Clamp(NumCtx, 512, 262144);
        SourceLanguage ??= "auto";
        TargetLanguage = string.IsNullOrWhiteSpace(TargetLanguage) ? "zh" : TargetLanguage;
        OllamaHost = string.IsNullOrWhiteSpace(OllamaHost) ? "http://127.0.0.1:11434" : OllamaHost;
        Model = string.IsNullOrWhiteSpace(Model) ? "hy-mt2:7b-q4km" : Model;
        HotkeyOcr ??= string.Empty;
        HotkeyMainWindow ??= string.Empty;
    }

    /// <summary>截图 OCR 的全局快捷键，空字符串表示不注册。</summary>
    public string HotkeyOcr { get; set; } = "Ctrl+Alt+O";

    /// <summary>输入翻译/主窗口的全局快捷键。</summary>
    public string HotkeyMainWindow { get; set; } = "Ctrl+Alt+T";
}

/// <summary>
/// 配置的读写与路径管理。
///
/// <para><b>所有可写数据一律落在 %APPDATA%\ChatTranslate\ 下，绝不写程序目录。</b>
/// 单文件 exe 可能被放在只读目录（如 Program Files），写程序目录会直接失败。</para>
/// </summary>
public static class AppPaths
{
    /// <summary>数据根目录：%APPDATA%\ChatTranslate\</summary>
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChatTranslate");

    /// <summary>配置文件路径。</summary>
    public static string ConfigFile => Path.Combine(DataRoot, "config.json");

    /// <summary>会话数据库路径。</summary>
    public static string DatabaseFile => Path.Combine(DataRoot, "chat.db");

    /// <summary>OCR 截图存放目录。</summary>
    public static string ImagesDirectory => Path.Combine(DataRoot, "images");

    /// <summary>日志目录。</summary>
    public static string LogsDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>确保所有目录存在。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ImagesDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}

/// <summary>配置的持久化。</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>当前配置。首次访问时自动从磁盘加载（不存在则用默认值）。</summary>
    public AppConfig Current { get; private set; } = new();

    /// <summary>从磁盘读取配置。</summary>
    /// <remarks>
    /// 必须区分两种失败，否则会静默丢掉用户的设置：
    /// <list type="bullet">
    /// <item><b>配置内容损坏</b>（JSON 解析失败）→ 回落到默认值并覆盖写回，这是合理的自愈</item>
    /// <item><b>读取失败</b>（文件被杀软/索引器短暂锁定、权限不足等瞬时 IO 问题）→
    /// <b>保留磁盘上的原文件，不执行 Save</b>，否则会把用户已配好的服务地址、模型、
    /// 快捷键全部用默认值覆盖，且不可逆</item>
    /// </list>
    /// </remarks>
    public AppConfig Load()
    {
        AppPaths.EnsureCreated();

        if (!File.Exists(AppPaths.ConfigFile))
        {
            Current = new AppConfig();
            Save();
            return Current;
        }

        try
        {
            var json = File.ReadAllText(AppPaths.ConfigFile);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (loaded is not null)
            {
                loaded.Normalize();
                Current = loaded;
                return Current;
            }
        }
        catch (JsonException)
        {
            // 配置损坏：回落到默认值并写回一份可用的
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读取失败：保留磁盘原文件，不做任何覆盖
            System.Diagnostics.Debug.WriteLine($"读取配置失败，沿用内存中的默认值：{ex}");
            return Current;
        }

        Current = new AppConfig();
        Save();
        return Current;
    }

    /// <summary>写回配置。先写临时文件再替换，避免写入中断导致配置损坏。</summary>
    public void Save()
    {
        AppPaths.EnsureCreated();

        var json = JsonSerializer.Serialize(Current, JsonOptions);
        var temp = AppPaths.ConfigFile + ".tmp";

        File.WriteAllText(temp, json);
        File.Move(temp, AppPaths.ConfigFile, overwrite: true);
    }
}
