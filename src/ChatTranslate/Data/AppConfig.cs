using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// 模型在显存里的保留时长（Ollama 的 <c>keep_alive</c>）。
    /// </summary>
    /// <remarks>
    /// Ollama 默认只留 5 分钟，卸载后下一次请求要先把模型重新载入显存
    /// （本机实测 7.05 s，期间界面没有任何反馈）。
    /// 取值形态见 <see cref="TryNormalizeKeepAlive"/>：时长串（<c>30m</c>）、
    /// 秒数（<c>600</c>），以及 <c>-1</c> 常驻不卸载。
    /// </remarks>
    public string OllamaKeepAlive { get; set; } = DefaultKeepAlive;

    /// <summary>输入语言代码；"auto" 表示自动检测。</summary>
    public string SourceLanguage { get; set; } = "auto";

    /// <summary>目标语言代码（见 Languages）。</summary>
    public string TargetLanguage { get; set; } = "zh";

    /// <summary>
    /// 语言下拉里展示的语种代码。
    /// </summary>
    /// <remarks>
    /// 默认只放中英——37 个语种全塞进下拉会让最常用的两个反而难选。
    /// 其余语种在设置里勾选后加入。核心语种（中/英）始终包含，见 <see cref="CoreLanguages"/>。
    /// </remarks>
    public List<string> EnabledLanguages { get; set; } = ["zh", "en"];

    /// <summary>
    /// 始终展示、不可取消的语种。
    /// </summary>
    /// <remarks>
    /// 中英是绝大多数使用场景的目标，让用户能把自己唯一的常用语言取消掉没有意义，
    /// 反而会造出「目标语言为空」的状态。
    /// </remarks>
    [JsonIgnore]
    public static IReadOnlyList<string> CoreLanguages { get; } = ["zh", "en"];

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

        // 校验语言代码本身合法，而不只是非空。
        // 手改成 "xx" 这类非法代码会被原样保留，而 NormalizeEnabledLanguages 里的
        // keep 集合随后被 Languages.All 过滤掉，于是配置长期处于
        //「目标语言不在可选语种列表内」的矛盾状态——正是本方法要避免的情况。
        if (SourceLanguage != "auto" && Services.Languages.ByCode(SourceLanguage) is null)
        {
            SourceLanguage = "auto";
        }

        if (Services.Languages.ByCode(TargetLanguage) is null)
        {
            TargetLanguage = "zh";
        }

        OllamaHost = string.IsNullOrWhiteSpace(OllamaHost) ? "http://127.0.0.1:11434" : OllamaHost;
        Model = string.IsNullOrWhiteSpace(Model) ? "hy-mt2:7b-q4km" : Model;

        // 非法写法不报错、直接回到默认值：配置是明文 JSON，手工改坏时让它继续可用
        // 比让每次翻译都失败更有意义（设置界面会在保存时挡住非法输入）
        TryNormalizeKeepAlive(OllamaKeepAlive, out var keepAlive);
        OllamaKeepAlive = keepAlive;

        HotkeyOcr ??= string.Empty;
        HotkeyMainWindow ??= string.Empty;
        NormalizeEnabledLanguages();
    }

    /// <summary>keep_alive 的默认值：30 分钟。</summary>
    /// <remarks>
    /// 比 Ollama 自带的 5 分钟长，够覆盖一段连续使用——翻译是断续操作，
    /// 5 分钟很容易在两次翻译之间就被卸载，下一次要先等模型重新载入（实测 7.05 s）。
    /// 又不像 <c>-1</c> 那样长期占着显存（本机 8 GB，跑游戏或别的模型时会被挤掉）。
    /// </remarks>
    public const string DefaultKeepAlive = "30m";

    /// <summary>纯整数：按 Ollama 对数字的语义解释为秒；负数表示常驻不卸载。</summary>
    /// <remarks>
    /// 必须写 <c>[0-9]</c> 而不是 <c>\d</c>：.NET 的 <c>\d</c> 等价于 <c>\p{Nd}</c>，
    /// 会匹配全角数字（<c>６００</c>）与阿拉伯-印度数字（<c>٣٠</c>），而
    /// <c>long.TryParse</c> 不认它们（实测两者都返回 false）——
    /// 于是校验放行、发送时却退化成字符串，Go 的 <c>time.ParseDuration</c> 同样不认，
    /// 每次翻译都返回 400。中文输入法在全角状态下很容易敲出全角数字。
    /// </remarks>
    private static readonly Regex SecondsPattern =
        new(@"^-?[0-9]+$", RegexOptions.Compiled);

    /// <summary>
    /// Go <c>time.ParseDuration</c> 认的时长串：带单位的 ASCII 数字，可拼接（<c>1h30m</c>）。
    /// </summary>
    /// <remarks>
    /// 允许前导负号：Go 接受负时长（<c>ParseDuration("-1h")</c> 合法），Ollama 侧的语义是
    /// 「常驻不卸载」。不接受负号的话 <c>-1h</c> 会被静默回退成默认值 <c>30m</c>，
    /// 与用户意图相反，且看不出配置被改写过。
    /// </remarks>
    private static readonly Regex DurationPattern =
        new(@"^-?(?:[0-9]+(?:\.[0-9]+)?(?:ns|us|µs|ms|s|m|h))+$", RegexOptions.Compiled);

    /// <summary><c>keep_alive</c> 的规范形态。</summary>
    /// <param name="IsSeconds">是否为数字形态（Ollama 把数字解释为秒）。</param>
    /// <param name="Seconds">数字形态下的秒数。</param>
    /// <param name="Text">规范化后的原文，时长串形态下作为请求体取值。</param>
    public readonly record struct KeepAliveValue(bool IsSeconds, long Seconds, string Text)
    {
        /// <summary>
        /// 请求体里的取值。
        /// </summary>
        /// <remarks>
        /// 数字形态必须发 JSON <b>数字</b>：Ollama 用 Go 的 <c>time.ParseDuration</c>
        /// 解析字符串，而 <c>"600"</c> / <c>"-1"</c> 都不是合法 duration
        /// （只有 <c>"0"</c> 是特例），发成字符串会 400。时长串才发字符串。
        /// </remarks>
        public object Payload => IsSeconds ? Seconds : Text;
    }

    /// <summary>
    /// 解析 <c>keep_alive</c>；空串与无法识别的写法都返回 false。
    /// </summary>
    /// <remarks>
    /// <para><b>这是 <c>keep_alive</c> 唯一的解析真源</b>——<see cref="TryNormalizeKeepAlive"/>
    /// （配置校验）与 <c>OllamaClient</c>（决定发数字还是字符串）都走这里。</para>
    ///
    /// <para>两边各写一套判断是踩过的坑：校验按"形状"放行、发送按"能否解析"退化，
    /// 于是超范围数字、全角数字这些写法会被校验放行却原样进请求体，
    /// Ollama 解析失败、每次翻译返回 400，界面只显示「翻译失败」，
    /// 用户完全看不出是配置写错了。</para>
    /// </remarks>
    public static bool TryParseKeepAlive(string? value, out KeepAliveValue parsed)
    {
        var text = value?.Trim() ?? string.Empty;
        parsed = default;

        if (text.Length == 0)
        {
            return false;
        }

        if (SecondsPattern.IsMatch(text))
        {
            // 形状对不代表能表示：超出 long 范围的数字串 Go 也解析不了，
            // 因此纯数字形态要求真的解析成功，否则按「不认识」处理
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                return false;
            }

            parsed = new KeepAliveValue(IsSeconds: true, Seconds: seconds, Text: text);
            return true;
        }

        if (DurationPattern.IsMatch(text))
        {
            parsed = new KeepAliveValue(IsSeconds: false, Seconds: 0, Text: text);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 校验并规范化 <c>keep_alive</c>。
    /// </summary>
    /// <param name="value">配置里的原始文本。</param>
    /// <param name="normalized">规范化结果；不合法时为 <see cref="DefaultKeepAlive"/>。</param>
    /// <returns>空串或合法取值返回 true；无法识别的写法返回 false。</returns>
    /// <remarks>
    /// <b>空串按 <see cref="DefaultKeepAlive"/> 处理</b>——界面上清空输入框等于"用默认值"，
    /// 因此配置层永远不产生空串。想彻底不干预 <c>keep_alive</c>（用 Ollama 自己的默认值）
    /// 目前没有界面入口，那属于 <c>OllamaClient</c> 构造器的契约，供直接构造时使用。
    /// </remarks>
    public static bool TryNormalizeKeepAlive(string? value, out string normalized)
    {
        if (TryParseKeepAlive(value, out var parsed))
        {
            normalized = parsed.Text;
            return true;
        }

        normalized = DefaultKeepAlive;

        // 空串视为合法（用默认值）；其余无法识别的写法一律非法，由界面提示
        return string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// 收敛可选语种：剔除非法代码与重复项，补齐核心语种，并保证当前选中的
    /// 输入 / 目标语言一定在列表内。
    /// </summary>
    /// <remarks>
    /// <para>必须保证"当前选中的语言在列表里"：若用户先把目标语言设成日语、
    /// 之后又在设置里取消勾选日语，ComboBox 绑定不上会被清空，
    /// 界面显示空白、保存后目标语言变成无——用户会以为设置坏了。</para>
    ///
    /// <para>顺序按语种表的规范顺序重排，中英自然排在最前（它们在表里就是前两位），
    /// 用户取消再勾选也不会把顺序打乱。</para>
    /// </remarks>
    private void NormalizeEnabledLanguages()
    {
        EnabledLanguages ??= [];

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in EnabledLanguages)
        {
            if (!string.IsNullOrWhiteSpace(code) && Services.Languages.ByCode(code) is not null)
            {
                keep.Add(code);
            }
        }

        // 核心语种必选
        foreach (var core in CoreLanguages)
        {
            keep.Add(core);
        }

        // 当前选中的语言必须在列表内，否则界面会出现空白选项
        if (SourceLanguage is not ("auto" or ""))
        {
            keep.Add(SourceLanguage);
        }

        if (!string.IsNullOrWhiteSpace(TargetLanguage))
        {
            keep.Add(TargetLanguage);
        }

        EnabledLanguages = Services.Languages.All
            .Where(l => keep.Contains(l.Code))
            .Select(l => l.Code)
            .ToList();
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
