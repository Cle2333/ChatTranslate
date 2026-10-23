using System.IO;
using System.Windows;
using System.Windows.Controls;
using ChatTranslate.Core;
using ChatTranslate.Data;
using ChatTranslate.Services;
using Wpf.Ui.Controls;

// WPF 的 FrameworkElement 自带 Language 属性（类型 XmlLanguage），
// 在窗口类内引用 `Language` 会被该成员遮蔽，因此业务模型必须用别名。
using AppLanguage = ChatTranslate.Services.Language;

namespace ChatTranslate.Views;

/// <summary>设置窗口：外观、语言、服务、快捷键、划词、数据位置。</summary>
public partial class SettingsWindow : FluentWindow
{
    /// <summary>主题下拉的一项。</summary>
    private sealed record ThemeOption(string Mode, string Name);

    /// <summary>翻译服务商下拉的一项。</summary>
    private sealed record ProviderOption(string Value, string Name)
    {
        public override string ToString() => Name;
    }

    private static readonly ThemeOption[] ThemeOptions =
    [
        new(AppTheme.ModeSystem, "跟随系统"),
        new(AppTheme.ModeLight, "亮色"),
        new(AppTheme.ModeDark, "深色"),
    ];

    private static readonly ProviderOption[] ProviderOptions =
    [
        new(AppConfig.ProviderOllama, "本地 Ollama（离线）"),
        new(AppConfig.ProviderDeepSeek, "DeepSeek 云端 API"),
    ];

    private readonly ConfigStore _config;
    private readonly List<LanguageChip> _languageChips;

    /// <summary>打开设置窗口那一刻的主题模式。</summary>
    /// <remarks>
    /// 主题是「选中即预览」的，所以必须记住原值：用户点「取消」或直接关窗口时把它恢复回去。
    /// 否则界面停在预览配色、配置里还是旧值，下次启动又变回去——看着像设置没保存。
    /// </remarks>
    private readonly string _originalTheme;

    /// <summary>是否点过「保存」。只有点过才保留预览出来的主题。</summary>
    private bool _committed;

    /// <summary>填充下拉时会触发 SelectionChanged，那一次不能当成用户操作。</summary>
    private bool _themeInitializing;

    /// <summary>填充服务商下拉时不要触发面板切换/网络探测。</summary>
    private bool _providerInitializing;

    /// <summary>用户是否点了「清除 API Key」；保存时才真正落到配置。</summary>
    private bool _clearDeepSeekApiKey;

    private static readonly TimeSpan LocalModelListTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloudModelListTimeout = TimeSpan.FromSeconds(30);

    /// <summary>模型清单请求是否在途，防止刷新按钮与服务商切换并发覆盖结果。</summary>
    private bool _modelListBusy;

    /// <summary>
    /// 本次设置窗口里是否重新拉起过 Ollama 进程。
    /// </summary>
    /// <remarks>
    /// 主窗口在 <c>ShowDialog</c> 返回后据此刷新状态：用户可能只重启、不保存就点「取消」，
    /// 这时配置没变但服务状态已经变了，不能因为 DialogResult 不是 true 就忽略。
    /// 进程已拉起但 API 仍在初始化时同样为 true，由主窗口继续等它恢复。
    /// </remarks>
    public bool OllamaRestarted { get; private set; }

    /// <summary>重启后已经能在限定时间内响应（不只是进程被拉起）。</summary>
    public bool OllamaRestartReady { get; private set; }

    /// <summary>本次设置窗口里是否执行过重启尝试（成功与否都算）。</summary>
    public bool OllamaRestartAttempted { get; private set; }

    /// <summary>手动重启的结果说明；主窗口在用户点「取消」后用它解释服务为什么还没恢复。</summary>
    public string OllamaRestartMessage { get; private set; } = string.Empty;

    public SettingsWindow(ConfigStore config)
    {
        InitializeComponent();
        _config = config;

        var current = _config.Current;
        HostBox.Text = current.OllamaHost;
        ModelBox.Text = current.Model;
        NumCtxBox.Text = current.NumCtx.ToString();
        KeepAliveBox.Text = current.OllamaKeepAlive;
        DeepSeekEndpointBox.Text = current.DeepSeekEndpoint;
        DeepSeekModelBox.Text = current.DeepSeekModel;
        DeepSeekContextBox.Text = current.DeepSeekContextTokens.ToString();
        HotkeyOcrBox.Text = current.HotkeyOcr;
        HotkeyWindowBox.Text = current.HotkeyMainWindow;
        DelayBox.Text = current.SelectionDelayMs.ToString();
        MaxCharsBox.Text = current.SelectionMaxChars.ToString();
        DiagnosticToggle.IsChecked = current.DiagnosticLogging;

        _providerInitializing = true;
        try
        {
            ProviderBox.ItemsSource = ProviderOptions;
            ProviderBox.SelectedItem =
                ProviderOptions.FirstOrDefault(o => o.Value == current.Provider) ?? ProviderOptions[0];
        }
        finally
        {
            _providerInitializing = false;
        }

        UpdateProviderPanels();
        UpdateDeepSeekKeyHint();

        _originalTheme = AppTheme.Normalize(current.Theme);
        _themeInitializing = true;
        try
        {
            ThemeBox.ItemsSource = ThemeOptions;
            ThemeBox.DisplayMemberPath = nameof(ThemeOption.Name);
            ThemeBox.SelectedItem =
                ThemeOptions.FirstOrDefault(o => o.Mode == _originalTheme) ?? ThemeOptions[2];
        }
        finally
        {
            _themeInitializing = false;
        }

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

    /// <summary>
    /// 主题选中即预览。
    /// </summary>
    /// <remarks>
    /// 刻意做成即时生效而不是等「保存」：配色是对着眼前这个窗口调的，
    /// 让人按了保存才看到效果没法判断调得对不对。
    /// 代价是"取消"必须负责还原，见 <see cref="OnClosing"/>。
    /// </remarks>
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_themeInitializing || ThemeBox.SelectedItem is not ThemeOption option)
        {
            return;
        }

        AppTheme.Apply(option.Mode);
    }

    /// <summary>
    /// 关窗口时，没点过「保存」就把预览出来的主题撤回去。
    /// </summary>
    /// <remarks>
    /// 必须走 OnClosing 而不是只在「取消」按钮里还原：用右上角 × 关窗口时
    /// DialogResult 不会为 true，但主题预览已经生效了。
    /// </remarks>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (OllamaProcessManager.IsRestarting)
        {
            e.Cancel = true;
            RestartOllamaHint.Text = "Ollama 正在重启，请等待完成后再关闭。";
            base.OnClosing(e);
            return;
        }

        base.OnClosing(e);

        if (!_committed)
        {
            AppTheme.Apply(_originalTheme);
            Core.AppLog.Info($"设置未保存，主题已恢复为 {_originalTheme}");
        }
    }

    /// <summary>当前下拉选中的主题模式。</summary>
    private string SelectedTheme =>
        (ThemeBox.SelectedItem as ThemeOption)?.Mode ?? _originalTheme;

    /// <summary>当前下拉选中的服务商。</summary>
    private string SelectedProvider =>
        (ProviderBox.SelectedItem as ProviderOption)?.Value ?? AppConfig.ProviderOllama;

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_providerInitializing)
        {
            return;
        }

        UpdateProviderPanels();

        // 切到云端时顺手拉一次清单；失败只更新提示，不影响保存。
        _ = PopulateModelListAsync();
    }

    private void UpdateProviderPanels()
    {
        var cloud = SelectedProvider == AppConfig.ProviderDeepSeek;
        OllamaServicePanel.Visibility = cloud ? Visibility.Collapsed : Visibility.Visible;
        DeepSeekServicePanel.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClearDeepSeekKeyClick(object sender, RoutedEventArgs e)
    {
        _clearDeepSeekApiKey = true;
        DeepSeekKeyBox.Password = string.Empty;
        UpdateDeepSeekKeyHint();
    }

    private void OnDeepSeekKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(DeepSeekKeyBox.Password))
        {
            _clearDeepSeekApiKey = false;
        }

        UpdateDeepSeekKeyHint();
    }

    private void UpdateDeepSeekKeyHint()
    {
        if (_clearDeepSeekApiKey)
        {
            DeepSeekKeyHint.Text = "保存后将删除已存 API Key。重新输入新 Key 可取消这次删除。";
            return;
        }

        if (!string.IsNullOrEmpty(DeepSeekKeyBox.Password))
        {
            DeepSeekKeyHint.Text = "保存后替换现有 API Key。";
            return;
        }

        var stored = _config.Current.DeepSeekApiKeyProtected;
        DeepSeekKeyHint.Text = !_config.Current.HasDeepSeekApiKey
            ? "尚未设置 API Key。Key 使用 Windows DPAPI 加密后写入配置，不会以明文落盘。"
            : SecretProtector.Unprotect(stored) is null
                ? "已保存的 API Key 无法被当前 Windows 用户解密，请重新填写。"
                : "已保存 API Key。留空表示保持不变；Key 使用 Windows DPAPI 加密，界面不回显。";
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
        if (_modelListBusy)
        {
            return;
        }

        _modelListBusy = true;

        try
        {
            if (SelectedProvider == AppConfig.ProviderDeepSeek)
            {
                await PopulateDeepSeekModelListAsync();
                return;
            }

            RefreshModelsButton.IsEnabled = false;

            using var cts = new CancellationTokenSource(LocalModelListTimeout);
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
                    host, current, _config.Current.NumCtx, _config.Current.OllamaKeepAlive, cts.Token);

                ModelBox.ItemsSource = models;
                ModelBox.Text = current;

                // 只有一项 = 只拿到兜底的当前值，说明没探测到清单
                ModelListHint.Text = models.Count > 1
                    ? $"已找到 {models.Count} 个已安装模型，可从下拉选择。"
                    : "没获取到已安装模型（Ollama 未启动或地址不对），可直接手动输入模型名。";
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                ModelListHint.Text =
                    $"获取模型清单超时（{LocalModelListTimeout.TotalSeconds:F0} 秒），Ollama 可能无响应。";
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
        finally
        {
            _modelListBusy = false;
        }
    }

    private async void OnRefreshModelsClick(object sender, RoutedEventArgs e) =>
        await PopulateModelListAsync();

    /// <summary>结束本机 Ollama 进程并重新启动。</summary>
    /// <remarks>
    /// 地址框里有合法值就按它探测，否则回退配置值；远程地址不执行，因为杀本机进程解决不了远程服务的问题。
    /// 重启期间禁用按钮，避免连续点击堆出多个等待循环。
    /// </remarks>
    private async void OnRestartOllamaClick(object sender, RoutedEventArgs e)
    {
        if (OllamaProcessManager.IsRestarting)
        {
            OllamaRestartAttempted = true;
            OllamaRestarted = true;
            OllamaRestartReady = false;
            OllamaRestartMessage = "已有重启正在进行，请稍候";
            RestartOllamaHint.Text = OllamaRestartMessage;
            return;
        }

        var host = HostBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(host) || !IsValidHost(host))
        {
            host = _config.Current.OllamaHost;
        }

        if (!OllamaProcessManager.IsLocalHost(host))
        {
            RestartOllamaHint.Text = "当前地址不是本机，不能重启本机 Ollama。";
            return;
        }

        OllamaRestartAttempted = true;
        OllamaRestartReady = false;
        OllamaRestartMessage = "正在重启…";
        RestartOllamaButton.IsEnabled = false;
        RestartOllamaButton.Content = "正在重启…";
        ProviderBox.IsEnabled = false;
        RefreshModelsButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        RestartOllamaHint.Text =
            $"正在结束并重新启动 Ollama，最多等待 {OllamaProcessManager.ReadyTimeout.TotalSeconds:F0} 秒…";

        try
        {
            var result = await OllamaProcessManager.RestartAsync(host);
            OllamaRestartMessage = result.Message;
            if (result.Busy)
            {
                // 看门狗或另一个入口正在重启：这里不重复执行，等它完成。
                OllamaRestarted = true;
                RestartOllamaHint.Text = result.Message;
            }
            else if (result.Success)
            {
                OllamaRestarted = true;
                OllamaRestartReady = true;
                RestartOllamaHint.Text = "Ollama 已重启，正在刷新模型清单…";
                await PopulateModelListAsync();
                RestartOllamaHint.Text = "Ollama 已重启。";
            }
            else if (result.Started)
            {
                // 进程已经换了新的，但这台机器光 GPU 探测就可能持续数分钟；
                // 不能把「尚未就绪」显示成「重启失败」，否则用户会以为按钮没生效。
                OllamaRestarted = true;
                RestartOllamaHint.Text = result.Message;
            }
            else
            {
                RestartOllamaHint.Text = $"重启 Ollama 失败：{result.Message}";
            }
        }
        finally
        {
            RestartOllamaButton.Content = "重启 Ollama";
            RestartOllamaButton.IsEnabled = true;
            ProviderBox.IsEnabled = true;
            RefreshModelsButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private async Task PopulateDeepSeekModelListAsync()
    {
        RefreshDeepSeekModelsButton.IsEnabled = false;
        using var cts = new CancellationTokenSource(CloudModelListTimeout);

        try
        {
            var endpoint = DeepSeekEndpointBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(endpoint) || !IsValidHost(endpoint))
            {
                endpoint = _config.Current.DeepSeekEndpoint;
            }

            var key = DeepSeekKeyBox.Password?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                key = _config.Current.DeepSeekApiKey;
            }

            DeepSeekModelHint.Text = "正在获取云端模型…";

            var current = DeepSeekModelBox.Text?.Trim() ?? string.Empty;
            var models = await ModelCatalog.ListDeepSeekAsync(
                endpoint,
                key,
                current,
                _config.Current.DeepSeekContextTokens,
                cts.Token);

            DeepSeekModelBox.ItemsSource = models;
            DeepSeekModelBox.Text = current;

            DeepSeekModelHint.Text = models.Count > 1
                ? $"已找到 {models.Count} 个 DeepSeek 模型，可从下拉选择。"
                : string.IsNullOrWhiteSpace(key)
                    ? "尚未配置 API Key，无法获取清单；可直接输入模型名。"
                    : "没获取到云端模型（地址、API Key 或网络可能有问题），可直接输入模型名。";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            DeepSeekModelHint.Text =
                $"获取云端模型超时（{CloudModelListTimeout.TotalSeconds:F0} 秒），不影响保存；可直接输入模型名。";
        }
        catch (Exception ex)
        {
            DeepSeekModelHint.Text = $"获取云端模型失败（不影响保存）：{ex.Message}";
        }
        finally
        {
            RefreshDeepSeekModelsButton.IsEnabled = true;
        }
    }

    private async void OnRefreshDeepSeekModelsClick(object sender, RoutedEventArgs e) =>
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
        var provider = SelectedProvider;

        var host = HostBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            host = "http://127.0.0.1:11434";
        }
        else if (provider == AppConfig.ProviderOllama && !IsValidHost(host))
        {
            ShowError("Ollama 地址格式不正确，应形如 http://127.0.0.1:11434");
            return;
        }

        var endpoint = DeepSeekEndpointBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = "https://api.deepseek.com";
        }
        else if (provider == AppConfig.ProviderDeepSeek && !IsValidHost(endpoint))
        {
            ShowError("DeepSeek API 地址格式不正确，应形如 https://api.deepseek.com");
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

        if (!TryReadInt(DeepSeekContextBox.Text, 4096, 1048576, out var cloudContext, out error))
        {
            ShowError($"DeepSeek 上下文预算{error}");
            return;
        }

        // 先算出将要写入的密文，不在校验通过前改 _config.Current ——
        // 用户如果因为 Key 校验失败而点取消，已保存的 Key 不能被清掉。
        var protectedKey = _config.Current.DeepSeekApiKeyProtected;
        var keyInput = DeepSeekKeyBox.Password?.Trim() ?? string.Empty;
        if (_clearDeepSeekApiKey)
        {
            protectedKey = string.Empty;
        }
        else if (keyInput.Length > 0)
        {
            try
            {
                protectedKey = SecretProtector.Protect(keyInput);
            }
            catch (Exception ex)
            {
                ShowError($"无法加密 DeepSeek API Key：{ex.Message}");
                return;
            }
        }

        if (provider == AppConfig.ProviderDeepSeek
            && string.IsNullOrWhiteSpace(SecretProtector.Unprotect(protectedKey)))
        {
            ShowError("DeepSeek 云端 API 需要有效的 API Key。请填写，或先切回本地 Ollama。");
            return;
        }

        if (!TryReadInt(DelayBox.Text, 0, 5000, out var delay, out error))
        {
            ShowError($"防抖延迟{error}");
            return;
        }

        if (!TryReadInt(MaxCharsBox.Text,
                AppConfig.MinSelectionMaxChars,
                AppConfig.MaxSelectionMaxChars,
                out var maxChars,
                out error))
        {
            ShowError($"最长字符数{error}");
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

        target.Provider = provider;
        target.OllamaHost = host;
        target.Model = string.IsNullOrWhiteSpace(ModelBox.Text)
            ? "hy-mt2:7b-q4km"
            : ModelBox.Text.Trim();
        target.NumCtx = numCtx;
        target.OllamaKeepAlive = keepAlive;
        target.DeepSeekEndpoint = endpoint.TrimEnd('/');
        target.DeepSeekModel = string.IsNullOrWhiteSpace(DeepSeekModelBox.Text)
            ? "deepseek-chat"
            : DeepSeekModelBox.Text.Trim();
        target.DeepSeekContextTokens = cloudContext;
        target.DeepSeekApiKeyProtected = protectedKey;
        target.SelectionDelayMs = delay;
        target.SelectionMaxChars = maxChars;
        target.DiagnosticLogging = DiagnosticToggle.IsChecked == true;
        target.Theme = SelectedTheme;

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

        // 只有到这里才算数：主题预览从此刻起不必再还原
        _committed = true;
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
