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

    /// <summary>划词防抖延迟（毫秒）。</summary>
    public int SelectionDelayMs { get; set; } = 350;

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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

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

    /// <summary>从磁盘读取配置；文件缺失或损坏时回落到默认值并写出一份。</summary>
    public AppConfig Load()
    {
        AppPaths.EnsureCreated();

        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var json = File.ReadAllText(AppPaths.ConfigFile);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded is not null)
                {
                    Current = loaded;
                    return Current;
                }
            }
        }
        catch
        {
            // 配置损坏不应该让程序起不来：回落到默认值，随后覆盖写回
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
