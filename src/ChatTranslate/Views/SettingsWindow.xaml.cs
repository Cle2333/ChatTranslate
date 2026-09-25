using System.IO;
using System.Windows;
using ChatTranslate.Data;
using ChatTranslate.Services;
using Wpf.Ui.Controls;

// WPF 的 FrameworkElement 自带 Language 属性（类型 XmlLanguage），
// 在窗口类内引用 `Language` 会被该成员遮蔽，因此业务模型必须用别名。
using AppLanguage = ChatTranslate.Services.Language;

namespace ChatTranslate.Views;

/// <summary>设置窗口：语言、服务、快捷键、划词、数据位置。</summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly ConfigStore _config;
    private readonly List<LanguageChip> _languageChips;

    public SettingsWindow(ConfigStore config)
    {
        InitializeComponent();
        _config = config;

        var current = _config.Current;
        HostBox.Text = current.OllamaHost;
        ModelBox.Text = current.Model;
        NumCtxBox.Text = current.NumCtx.ToString();
        KeepAliveBox.Text = current.OllamaKeepAlive;
        HotkeyOcrBox.Text = current.HotkeyOcr;
        HotkeyWindowBox.Text = current.HotkeyMainWindow;
        DelayBox.Text = current.SelectionDelayMs.ToString();
        DiagnosticToggle.IsChecked = current.DiagnosticLogging;

        // null 防御：配置文件被外部编辑成 null 时不能让设置窗口崩掉
        BlacklistBox.Text = string.Join(
            Environment.NewLine, current.SelectionBlacklist ?? []);
        DataPathBox.Text = AppPaths.DataRoot;

        // 语言下拉只列出「可选语种」；勾选区列出全部语种供增删
        var enabled = new HashSet<string>(
            current.EnabledLanguages ?? [], StringComparer.OrdinalIgnoreCase);

        // 当前正在使用的语言不能取消——取消后 Normalize 会静默把它加回来，
        // 用户会以为设置没生效。干脆置灰，让它一眼可见地不可改。
        var locked = new HashSet<string>(AppConfig.CoreLanguages, StringComparer.OrdinalIgnoreCase);
        if (current.SourceLanguage is not ("auto" or ""))
        {
            locked.Add(current.SourceLanguage);
        }

        locked.Add(current.TargetLanguage);

        _languageChips = Services.Languages.All
            .Select(l => new LanguageChip(
                l,
                isSelected: enabled.Contains(l.Code) || locked.Contains(l.Code),
                canToggle: !locked.Contains(l.Code)))
            .ToList();

        // 语种选择挪到独立窗口（LanguagePickerWindow），这里只在状态栏给一行摘要
        var pickable = Services.Languages.Pick(
            _languageChips.Where(c => c.IsSelected).Select(c => c.Code).ToList());

        if (pickable.Count == 0)
        {
            pickable = Services.Languages.Pick(AppConfig.CoreLanguages);
        }

        LanguageBox.ItemsSource = pickable;
        LanguageBox.DisplayMemberPath = nameof(AppLanguage.ChineseName);
        LanguageBox.SelectedItem =
            pickable.FirstOrDefault(l => l.Code == current.TargetLanguage) ?? pickable[0];

        // 输入语言：自动检测 + 可选语种
        var sources = new List<AppLanguage> { AutoDetectOption };
        sources.AddRange(pickable);
        SourceLanguageBox.ItemsSource = sources;
        SourceLanguageBox.DisplayMemberPath = nameof(AppLanguage.ChineseName);
        SourceLanguageBox.SelectedItem =
            sources.FirstOrDefault(l => l.Code == current.SourceLanguage) ?? AutoDetectOption;

        // 两个下拉改动后要重算"哪些语种必须保留"：
        // 否则用户把目标语言改成日语、再取消勾选日语，保存时配置校验又会把日语
        // 静默加回来——用户会以为设置没生效。
        LanguageBox.SelectionChanged += (_, _) => RefreshChipLocks();
        SourceLanguageBox.SelectionChanged += (_, _) => RefreshChipLocks();
        RefreshChipLocks();

        // 打开时拉一次已安装模型填下拉。不 await：
        // 这只是填充"可选项"，不该让设置窗口的显示等着一次网络请求
        _ = PopulateModelListAsync();
    }

    /// <summary>
    /// 重算每个语种的"是否必须保留"，并强制勾选被锁定的项。
    /// </summary>
    private void RefreshChipLocks()
    {
        // 核心语种始终保留
        var required = new HashSet<string>(AppConfig.CoreLanguages, StringComparer.OrdinalIgnoreCase);

        // 当前正在使用的语言也必须保留：不在可选列表里的话，
        // 主界面的下拉会绑定不上而被清空
        if (SourceLanguageBox.SelectedItem is AppLanguage source && source.Code != "auto")
        {
            required.Add(source.Code);
        }

        if (LanguageBox.SelectedItem is AppLanguage target)
        {
            required.Add(target.Code);
        }

        foreach (var chip in _languageChips)
        {
            var mustKeep = required.Contains(chip.Code);
            chip.CanToggle = !mustKeep;

            if (mustKeep)
            {
                chip.IsSelected = true;
            }
        }

        // 摘要要跟着变：改了目标语言后，"必须保留"的语种集合会变
        UpdateLanguageSummary();
    }

    /// <summary>
    /// 在「下拉里显示哪些语种」右侧显示一行摘要。
    /// </summary>
    /// <remarks>
    /// 语种平铺已挪进独立窗口，设置页只剩一行位置。全部列出来会被截断，
    /// 所以超过 4 种就只列前 4 个再给总数 —— 完整清单在弹窗里看。
    /// 顺序沿用 <see cref="Services.Languages.All"/>，与下拉里的一致。
    /// </remarks>
    private void UpdateLanguageSummary()
    {
        // 置灰项也算选中，否则摘要里的条数与实际保存结果对不上
        var names = _languageChips
            .Where(c => c.IsSelected || !c.CanToggle)
            .Select(c => c.Name)
            .ToList();

        LanguageSummary.Text = names.Count switch
        {
            0 => "（未选择）",
            <= 4 => string.Join("、", names),
            _ => $"{string.Join("、", names.Take(4))} 等 {names.Count} 种",
        };
    }

    /// <summary>
    /// 打开语种选择窗口，把结果合并回当前的勾选状态。
    /// </summary>
    /// <remarks>
    /// <para>只改内存里的 <see cref="_languageChips"/>，不写配置 —— 落盘仍由「保存」统一做，
    /// 这样用户在设置窗口点「取消」时，语种改动会一起作废，
    /// 不会出现"语种生效了、其它设置没生效"的半吊子状态。</para>
    ///
    /// <para>回来后再跑一次 <see cref="RefreshChipLocks"/>：弹窗里的置灰是按打开那一刻
    /// 的语言算的，虽然它是模态窗口期间改不了语言，但这一步能保证两条规则
    /// （核心语种 + 当前语言）始终由单一处决定。</para>
    /// </remarks>
    private void OnPickLanguagesClick(object sender, RoutedEventArgs e)
    {
        var picker = new LanguagePickerWindow(_languageChips) { Owner = this };
        if (picker.ShowDialog() != true)
        {
            return;
        }

        var chosen = new HashSet<string>(picker.SelectedCodes, StringComparer.OrdinalIgnoreCase);
        foreach (var chip in _languageChips)
        {
            chip.IsSelected = chosen.Contains(chip.Code);
        }

        RefreshChipLocks();
    }

    /// <summary>输入语言下拉里的"自动检测"哨兵项。</summary>
    internal static readonly AppLanguage AutoDetectOption =
        new("auto", "自动检测", "Auto Detect");

    /// <summary>
    /// 重新获取「模型」下拉里的已安装模型。
    /// </summary>
    /// <remarks>
    /// <para>用<b>地址框里当前填的地址</b>去探测，而不是配置里的旧地址：
    /// 用户改完地址紧接着点刷新，期望看到的是新地址上的模型。</para>
    ///
    /// <para>取不到清单时保留输入框里已有的内容，只更新下面那行提示 ——
    /// 这不影响保存，不该拦着用户。</para>
    /// </remarks>
    private async Task PopulateModelListAsync()
    {
        RefreshModelsButton.IsEnabled = false;

        try
        {
            var host = HostBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(host) || !IsValidHost(host))
            {
                host = _config.Current.OllamaHost;
            }

            ModelListHint.Text = "正在获取已安装模型…";

            // 先取当前文本：换了 ItemsSource 之后选中状态会丢，
            // 必须把用户已填的内容原样放回去，否则会变成空白
            var current = ModelBox.Text?.Trim() ?? string.Empty;

            var models = await ModelCatalog.ListAsync(
                host, current, _config.Current.NumCtx, _config.Current.OllamaKeepAlive);

            ModelBox.ItemsSource = models;
            ModelBox.Text = current;

            // 只有一项 = 只拿到兜底的当前值，说明没探测到清单
            ModelListHint.Text = models.Count > 1
                ? $"已找到 {models.Count} 个已安装模型，可从下拉选择。"
                : "没获取到已安装模型（Ollama 未启动或地址不对），可直接手动输入模型名。";
        }
        catch (Exception ex)
        {
            ModelListHint.Text = $"获取模型清单失败（不影响保存）：{ex.Message}";
        }
        finally
        {
            RefreshModelsButton.IsEnabled = true;
        }
    }

    private async void OnRefreshModelsClick(object sender, RoutedEventArgs e) =>
        await PopulateModelListAsync();

    /// <summary>
    /// 校验 Ollama 地址。
    /// </summary>
    /// <remarks>
    /// 不校验的话，像 <c>localhost:11434</c>（漏写 scheme）会被 .NET 解析成
    /// scheme 为 "localhost" 的绝对 URI 并落盘，直到发起请求时才以
    /// 「不支持该 scheme」这类晦涩信息暴露在状态栏。
    /// </remarks>
    private static bool IsValidHost(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is "http" or "https";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            host = "http://127.0.0.1:11434";
        }
        else if (!IsValidHost(host))
        {
            ShowError("Ollama 地址格式不正确，应形如 http://127.0.0.1:11434");
            return;
        }

        if (!TryReadInt(NumCtxBox.Text, 512, 262144, out var numCtx, out var error))
        {
            ShowError($"上下文窗口{error}");
            return;
        }

        if (!AppConfig.TryNormalizeKeepAlive(KeepAliveBox.Text, out var keepAlive))
        {
            ShowError("模型保留时长无法识别，应形如 30m、2h、600（秒）或 -1（常驻不卸载）");
            return;
        }

        if (!TryReadInt(DelayBox.Text, 0, 5000, out var delay, out error))
        {
            ShowError($"防抖延迟{error}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(HotkeyOcrBox.Text)
            && MainWindow.ParseHotkey(HotkeyOcrBox.Text) is null)
        {
            ShowError("截图 OCR 快捷键格式无法解析，应形如 Ctrl+Alt+O");
            return;
        }

        if (!string.IsNullOrWhiteSpace(HotkeyWindowBox.Text)
            && MainWindow.ParseHotkey(HotkeyWindowBox.Text) is null)
        {
            ShowError("呼出主窗口快捷键格式无法解析，应形如 Ctrl+Alt+T");
            return;
        }

        var target = _config.Current;

        target.OllamaHost = host;
        target.Model = string.IsNullOrWhiteSpace(ModelBox.Text)
            ? "hy-mt2:7b-q4km"
            : ModelBox.Text.Trim();
        target.NumCtx = numCtx;
        target.OllamaKeepAlive = keepAlive;
        target.SelectionDelayMs = delay;
        target.DiagnosticLogging = DiagnosticToggle.IsChecked == true;

        // 逐行解析并去掉空行/首尾空白；重复项也去掉，避免名单里同一进程出现多次
        target.SelectionBlacklist = (BlacklistBox.Text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        target.HotkeyOcr = HotkeyOcrBox.Text.Trim();
        target.HotkeyMainWindow = HotkeyWindowBox.Text.Trim();

        if (LanguageBox.SelectedItem is AppLanguage language)
        {
            target.TargetLanguage = language.Code;
        }

        if (SourceLanguageBox.SelectedItem is AppLanguage source)
        {
            target.SourceLanguage = source.Code;
        }

        // 可选语种：置灰项也算选中，避免把它们丢掉
        var selectedCodes = _languageChips
            .Where(c => c.IsSelected || !c.CanToggle)
            .Select(c => c.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        target.EnabledLanguages = Languages.All
            .Where(l => selectedCodes.Contains(l.Code))
            .Select(l => l.Code)
            .ToList();

        // 收敛一次再落盘：剔除非法代码、补齐核心语种与当前选中的语言。
        // 不在保存路径上做这一步，就可能写进「目标语言不在可选列表里」这种自相矛盾的配置。
        target.Normalize();

        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            ShowError($"保存配置失败：{ex.Message}");
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool TryReadInt(string text, int min, int max, out int value, out string error)
    {
        if (!int.TryParse(text?.Trim(), out value))
        {
            error = "必须是整数";
            return false;
        }

        if (value < min || value > max)
        {
            error = $"需在 {min} 到 {max} 之间";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void ShowError(string message) =>
        System.Windows.MessageBox.Show(this, message, "设置有误",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
}
