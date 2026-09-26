using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatTranslate.Core;
using ChatTranslate.Data;
using ChatTranslate.Services;
using Wpf.Ui.Controls;

// WPF 的 FrameworkElement 自带 Language 属性（类型 XmlLanguage），在类内引用 `Language`
// 会被该成员遮蔽，因此业务模型必须换一个不冲突的别名。
using AppLanguage = ChatTranslate.Services.Language;
using Languages = ChatTranslate.Services.Languages;

namespace ChatTranslate.Views;

/// <summary>
/// 主窗口：侧边栏（历史 / 划词开关 / 设置）+ 对话区 + 状态栏。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly ConfigStore _config = new();
    private readonly ChatStore _store;
    private readonly TranslationService _service;
    private readonly HotkeyManager _hotkeys = new();
    private readonly SelectionWatcher _selectionWatcher;

    private readonly ObservableCollection<ThreadViewModel> _threads = [];
    private readonly ObservableCollection<BubbleViewModel> _messages = [];

    /// <summary>当前划词浮窗。同一时间只保留一个，弹新的先关旧的。</summary>
    private TranslatePopup? _popup;

    /// <summary>当前 OCR 结果窗口。同样只保留一个。</summary>
    private ResultWindow? _resultWindow;

    /// <summary>
    /// OCR 批量翻译期间的占位文案前缀。
    /// </summary>
    /// <remarks>
    /// 定义成常量是因为它同时被两处使用：写入占位文案，以及判断"是否仍在等待模型载入"。
    /// 分别写字面量的话，改一处就会让加载提示静默失效。
    /// </remarks>
    private const string OcrProgressPrefix = "正在翻译";

    /// <summary>"自动检测"在输入语言下拉里的哨兵值。</summary>
    private static readonly AppLanguage AutoDetect =
        new("auto", "自动检测", "Auto Detect");

    private long _currentThreadId;
    private bool _busy;
    private bool _initializing;
    private int _ocrHotkeyId = -1;
    private int _windowHotkeyId = -1;

    public MainWindow()
    {
        InitializeComponent();

        AppPaths.EnsureCreated();
        _config.Load();

        _store = new ChatStore();
        _service = new TranslationService(_store, _config);
        _selectionWatcher = new SelectionWatcher(_config);
        _selectionWatcher.SelectionDetected += OnSelectionDetected;
        _selectionWatcher.ContentTooLong += OnSelectionTooLong;
        _selectionWatcher.GlobalLeftClick += OnGlobalLeftClick;

        ThreadList.ItemsSource = _threads;
        MessageList.ItemsSource = _messages;

        // 欢迎词的显隐完全由消息集合驱动：一处订阅覆盖所有路径
        // （新建会话、切换会话、发送翻译、OCR 结果写入），
        // 不必在每个增删消息的地方各写一次显隐逻辑。
        _messages.CollectionChanged += (_, _) => UpdateWelcomeState();

        _initializing = true;
        SelectionToggle.IsChecked = _config.Current.SelectionEnabled;
        InitializeLanguageSelectors();
        InitializeModelSelector();
        _initializing = false;

        // 诊断日志开关由配置决定，必须在启用监听前设好
        AppLog.Verbose = _config.Current.DiagnosticLogging;

        // 事件订阅只做一次。
        // 放进 RegisterHotkeys() 会导致每次保存设置（都会重注册热键）都多挂一份订阅，
        // 于是按一下热键触发 N 次回调、订阅链无限增长。
        _hotkeys.HotkeyPressed += OnHotkeyPressed;

        Loaded += OnLoaded;
    }

    /// <summary>
    /// 填充输入/目标语言下拉，并按配置选中当前值。
    /// </summary>
    /// <remarks>
    /// 下拉内容来自配置里的「可选语种」，默认只有中文和英语——
    /// 37 个语种全塞进来会让最常用的两个反而难选。其余语种在设置里勾选后加入。
    /// 设置保存后需要重新调用本方法，让改动立即生效。
    /// </remarks>
    private void InitializeLanguageSelectors()
    {
        var enabled = Languages.Pick(_config.Current.EnabledLanguages);

        // 兜底：可选语种为空时至少保留中英，否则下拉会是空的、无法选语言
        if (enabled.Count == 0)
        {
            enabled = Languages.Pick(AppConfig.CoreLanguages);
        }

        // 输入语言：自动检测 + 可选语种
        var sources = new List<AppLanguage> { AutoDetect };
        sources.AddRange(enabled);
        SourceLangBox.ItemsSource = sources;
        SourceLangBox.DisplayMemberPath = nameof(AppLanguage.ChineseName);
        SourceLangBox.SelectedItem =
            sources.FirstOrDefault(l => l.Code == _config.Current.SourceLanguage) ?? AutoDetect;

        // 目标语言：可选语种（不含自动检测——目标必须明确）
        TargetLangBox.ItemsSource = enabled;
        TargetLangBox.DisplayMemberPath = nameof(AppLanguage.ChineseName);

        // 当前目标语言若不在可选列表里（理论上 Normalize 已保证，这里再兜一层），
        // 直接赋 SelectedItem 会绑定不上、界面显示空白
        var target = Languages.ByCode(_config.Current.TargetLanguage) ?? Languages.Default;
        if (!enabled.Any(l => l.Code == target.Code))
        {
            var extended = new List<AppLanguage>(enabled) { target };
            TargetLangBox.ItemsSource = extended;
        }

        TargetLangBox.SelectedItem =
            (TargetLangBox.ItemsSource as IEnumerable<AppLanguage>)?.FirstOrDefault(l => l.Code == target.Code)
            ?? target;
    }

    // ---------------------------------------------------------------- 启动

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StatusVersion.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";
        StatusSpeed.Text = "-- tok/s";
        StatusContext.Text = $"上下文 0 / {_config.Current.NumCtx}";
        StatusOllama.Text = "正在连接…";
        UpdateTargetHint(new LanguagePair(_service.ConfiguredSource, _service.ConfiguredTarget, false));
        RefreshLanguageHint();

        RefreshThreads();

        // 打开最近一个会话；没有就新建
        _currentThreadId = _store.GetOrCreateLatestThread();
        RefreshThreads();
        SelectThreadInList(_currentThreadId);
        LoadThreadMessages(_currentThreadId);

        RegisterHotkeys();
        ApplySelectionWatcherState();

        // 模板此时已应用，输入框内部的 ScrollViewer 才存在
        HookInputScrollViewer();

        // 主窗口已就绪：跟随系统主题（若选了那一档）需要窗口句柄才能监听系统主题变化
        Core.AppTheme.AttachWindow(this);

        AppLog.Info($"===== 启动：版本 {typeof(MainWindow).Assembly.GetName().Version} =====");
        await CheckOllamaAsync();

        // 拉一次已装模型填下拉。不 await：Ollama 没启动时要等到超时，
        // 不该让输入框一直不能聚焦
        _ = RefreshModelListAsync();

        InputBox.Focus();
    }

    private void RefreshThreads()
    {
        _threads.Clear();
        foreach (var thread in _store.ListThreads())
        {
            _threads.Add(ThreadViewModel.From(thread));
        }

        UpdateArchiveButtonHint();
    }

    /// <summary>
    /// 把已归档的条数写进按钮提示。
    /// </summary>
    /// <remarks>
    /// 归档之后条目会从侧边栏消失，用户第一个疑问就是「东西还在不在」——
    /// 让按钮直接把条数说出来，比让他点开窗口去确认真实。
    /// </remarks>
    private void UpdateArchiveButtonHint()
    {
        var count = _store.CountArchived();
        ArchiveButton.ToolTip = count > 0 ? $"已归档的对话（{count}）" : "已归档的对话";
    }

    private void SelectThreadInList(long threadId)
    {
        var target = _threads.FirstOrDefault(t => t.Id == threadId);
        if (target is not null)
        {
            ThreadList.SelectedItem = target;
        }
    }

    private void LoadThreadMessages(long threadId)
    {
        _messages.Clear();
        foreach (var message in _store.ListMessages(threadId))
        {
            _messages.Add(BubbleViewModel.From(message));
        }

        // 集合从有到无（或本来为空）时，上面的订阅可能不会触发，这里兜一次底
        UpdateWelcomeState();
        ScrollToBottom();
    }

    // ---------------------------------------------------------------- 欢迎词

    /// <summary>
    /// 按当前是否为空会话显隐欢迎词。
    /// </summary>
    private void UpdateWelcomeState()
    {
        var empty = _messages.Count == 0;

        if (empty)
        {
            WelcomeGreeting.Text = BuildGreeting();
            WelcomeHint.Text = "今天想做些什么";
        }

        WelcomePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 按时段生成问候语。
    /// </summary>
    /// <remarks>
    /// 每次显示时重新计算，而不是启动时算一次：程序可能连续开着跨过时段边界
    /// （例如下午打开、一直用到晚上），问候语不该停留在启动时那个时段。
    /// </remarks>
    private static string BuildGreeting()
    {
        var (emoji, text) = DateTime.Now.Hour switch
        {
            >= 5 and < 9 => ("🌅", "早上好"),
            >= 9 and < 12 => ("☀️", "上午好"),
            >= 12 and < 14 => ("🍜", "中午好"),
            >= 14 and < 18 => ("☕", "下午好"),
            >= 18 and < 23 => ("👋", "晚上好"),
            _ => ("🌙", "夜深了"),
        };

        return $"{emoji} {text}";
    }

    // ---------------------------------------------------------------- 模型选择

    /// <summary>
    /// 初始化模型下拉：先只放配置里的模型，已安装清单随后异步补齐。
    /// </summary>
    /// <remarks>
    /// <b>启动时不阻塞界面去拉 <c>/api/tags</c></b>：Ollama 没启动时那是一次要等到超时的请求，
    /// 会让窗口迟迟不可用。清单在「窗口加载完成」与「展开下拉」时各补一次。
    /// </remarks>
    private void InitializeModelSelector()
    {
        var previous = _initializing;
        _initializing = true;
        try
        {
            var configured = _config.Current.Model;
            ModelBox.ItemsSource = new[] { configured };
            ModelBox.SelectedItem = configured;
        }
        finally
        {
            // 用保存/恢复而不是直接置 false：本方法会被已有 _initializing 的调用点包住，
            // 直接置 false 会把外层的保护一起撤掉
            _initializing = previous;
        }
    }

    /// <summary>
    /// 从 Ollama 重新取已安装模型，填充下拉并保持当前选中项。
    /// </summary>
    /// <remarks>
    /// <para>只影响下拉里能看到什么，<b>不写配置</b>——刷新不该改变"用哪个模型"。</para>
    /// <para>探测失败时保持现状：宁可清单旧一点，也不要把用户配好的模型从界面上抹掉。</para>
    /// </remarks>
    private async Task RefreshModelListAsync()
    {
        try
        {
            var config = _config.Current;
            var models = await ModelCatalog.ListAsync(
                config.OllamaHost, config.Model, config.NumCtx, config.OllamaKeepAlive);

            // 只拿到配置值本身 = 没探测到清单（Ollama 未启动等），保持现状
            if (models.Count <= 1)
            {
                return;
            }

            var current = ModelBox.SelectedItem as string ?? config.Model;

            // 内容没变就什么都不做：重建 ItemsSource 会清掉选中项，
            // 每次展开下拉都闪一下选中态，很难看
            if (ModelBox.ItemsSource is IEnumerable<string> existing
                && existing.SequenceEqual(models, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            var previous = _initializing;
            _initializing = true;
            try
            {
                ModelBox.ItemsSource = models;

                // 当前选中项若已不在清单里（被 ollama rm 掉了），直接赋 SelectedItem
                // 会绑定不上、界面变空白 —— 退回配置值，ModelCatalog 保证它一定会出现
                ModelBox.SelectedItem =
                    models.FirstOrDefault(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase))
                    ?? models.FirstOrDefault(m => string.Equals(m, config.Model, StringComparison.OrdinalIgnoreCase))
                    ?? models[0];
            }
            finally
            {
                _initializing = previous;
            }
        }
        catch (Exception ex)
        {
            // 刷新失败不影响翻译，只留一条诊断线索
            AppLog.Trace($"刷新模型清单失败：{ex.Message}");
        }
    }

    /// <summary>展开下拉时补一次清单，让刚 <c>ollama create</c> 出来的模型能立刻出现。</summary>
    private async void OnModelDropDownOpened(object sender, EventArgs e) =>
        await RefreshModelListAsync();

    /// <summary>
    /// 切换本地模型。
    /// </summary>
    /// <remarks>
    /// 只改配置，不做别的：<see cref="TranslationService"/> 的客户端缓存指纹包含模型名，
    /// 下一次翻译会自动按新模型重建客户端，因此不必重启、也不必手动清缓存。
    /// 新模型首次使用要先载入显存（实测 7–10 s），那段时间由气泡的
    /// 「模型加载中…」提示兜住。
    /// </remarks>
    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        // _busy 期间下拉是被禁用的（见 SetInputEnabled），这里是第二道防线：
        // 一旦漏掉，界面会显示已切换而实际仍按旧模型翻译，且毫无提示
        if (_initializing || _busy)
        {
            return;
        }

        if (ModelBox.SelectedItem is not string model || string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        if (string.Equals(model, _config.Current.Model, StringComparison.Ordinal))
        {
            return;
        }

        _config.Current.Model = model;
        _config.Save();
        AppLog.Info($"已切换本地模型：{model}");

        // 状态栏右侧显示的就是当前模型，切完立刻刷新，让用户看到改动生效
        _ = CheckOllamaAsync();
    }

    // ---------------------------------------------------------------- 输入翻译

    /// <summary>
    /// 回车发送，Shift+回车换行。
    /// </summary>
    /// <remarks>
    /// 必须挂在 **PreviewKeyDown**（隧道事件）而不是 KeyDown：
    /// 输入框的 <c>AcceptsReturn</c> 为 true（否则粘贴多行会被截断），
    /// 而 TextBox 的类处理器会先于实例处理器执行并插入换行 ——
    /// 挂在 KeyDown 上会导致每按一次回车先多插入一个空行才发送。
    /// 走隧道阶段可以先一步拦下回车。
    ///
    /// Shift+回车不拦截，交给 TextBox 自己插入换行。
    /// </remarks>
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    /// <summary>
    /// 刷新状态栏里的输入字数。
    /// </summary>
    /// <remarks>
    /// 输入框本身没有字数上限（实测灌 10000 字不被截断），但它有高度上限——
    /// 超出可见范围后内容会藏在滚动条里。把字数显示出来，
    /// 用户才能判断"还能不能继续粘"，而不是靠猜输入框是不是满了。
    /// </remarks>
    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        var text = InputBox.Text ?? string.Empty;

        if (text.Length == 0)
        {
            StatusInputCount.Visibility = Visibility.Collapsed;
            StatusInputScroll.Visibility = Visibility.Collapsed;
            return;
        }

        var lines = text.Count(c => c == '\n') + 1;
        StatusInputCount.Text = lines > 1
            ? $"{text.Length:N0} 字 · {lines} 行"
            : $"{text.Length:N0} 字";
        StatusInputCount.Visibility = Visibility.Visible;

        UpdateInputOverflowHint();
    }

    /// <summary>输入框内部的滚动宿主，用于判断内容是否超出可视区。</summary>
    private ScrollViewer? _inputScrollViewer;

    /// <summary>
    /// 挂上输入框内部滚动宿主的监听。
    /// </summary>
    /// <remarks>
    /// 为什么要自己判断"内容有没有超出可见高度"：输入框内部的滚动条被 Wpf.Ui 的控件模板
    /// 压掉了——把 <c>VerticalScrollBarVisibility</c> 设成 Auto 甚至 Visible 都不会画出滚动条
    /// （已按像素逐列核对过，右边缘只有清空按钮）。
    /// 滚轮是能用的，但"看不见还能滚"等于用户以为内容被截断了，所以由状态栏把这件事说出来。
    ///
    /// ScrollViewer 藏在控件模板内部，只能从视觉树里取；模板应用之后才存在，故在 Loaded 时挂。
    /// </remarks>
    private void HookInputScrollViewer()
    {
        InputBox.ApplyTemplate();
        _inputScrollViewer = FindDescendant<ScrollViewer>(InputBox);
        if (_inputScrollViewer is not null)
        {
            _inputScrollViewer.ScrollChanged += (_, _) => UpdateInputOverflowHint();
        }

        UpdateInputOverflowHint();
    }

    /// <summary>在视觉树里找第一个指定类型的后代。找不到返回 null。</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                return typed;
            }

            var found = FindDescendant<T>(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>内容超出可视高度时，在状态栏说明"哪个方向还有内容"。</summary>
    private void UpdateInputOverflowHint()
    {
        if (_inputScrollViewer is null || _inputScrollViewer.ScrollableHeight <= 0.5)
        {
            StatusInputScroll.Visibility = Visibility.Collapsed;
            return;
        }

        var atTop = _inputScrollViewer.VerticalOffset <= 0.5;
        var atBottom = _inputScrollViewer.VerticalOffset >= _inputScrollViewer.ScrollableHeight - 0.5;

        StatusInputScroll.Text = (atTop, atBottom) switch
        {
            (true, _) => "↓ 下方还有内容，滚轮可滚动",
            (_, true) => "↑ 上方还有内容，滚轮可滚动",
            _ => "↑↓ 上下还有内容，滚轮可滚动",
        };
        StatusInputScroll.Visibility = Visibility.Visible;
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => _ = SendAsync();

    private async Task SendAsync()
    {
        if (_busy)
        {
            return;
        }

        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        InputBox.Text = string.Empty;

        // 用户气泡立即出现，不等模型
        _messages.Add(new BubbleViewModel
        {
            Text = text,
            IsUser = true,
            TimeText = DateTime.Now.ToString("HH:mm"),
        });
        ScrollToBottom();

        await RunTranslationAsync(text, imagePath: null);
    }

    /// <summary>执行翻译并把结果填进气泡。</summary>
    private async Task RunTranslationAsync(string original, string? imagePath)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetInputEnabled(false);

        var bubble = BubbleViewModel.Streaming();
        _messages.Add(bubble);
        ScrollToBottom();

        // 与翻译并行探测模型是否已驻留显存：只影响提示文案，绝不阻塞本次请求。
        // 判据是"气泡还空着"——首块译文一到就不再宣称还在加载。
        _ = ApplyModelLoadHintAsync(bubble, () => bubble.IsStreaming && bubble.Text.Length == 0);

        try
        {
            await _service.TranslateAsync(
                _currentThreadId,
                original,
                imagePath,
                new TranslationCallbacks
                {
                    OnProgress = t =>
                    {
                        bubble.Text = t;

                        // 首块内容到达说明模型已经在工作，把「模型加载中…」切回「生成中」。
                        // 不复位的话，译文都开始滚了，提示还停在“加载中”。
                        bubble.StreamingLabel = BubbleViewModel.StreamingIdleLabel;
                        ScrollToBottom();
                    },
                    OnCompleted = metrics =>
                    {
                        StatusSpeed.Text = $"{metrics.TokensPerSecond:F1} tok/s";
                        StatusContext.Text =
                            $"上下文 {metrics.PromptEvalCount} / {metrics.NumCtx ?? _config.Current.NumCtx}";

                        // 显示本次实际使用的语言对，便于确认是否符合预期
                        var pair = _service.ResolveLanguages(original);
                        UpdateTargetHint(pair);

                        // 必须放在 UpdateTargetHint 之后：后者在不换向时会 HideLanguageHint，
                        // 放前面会被它清掉。
                        // 达到输出上限说明这段译文是**截断**的，不说的话用户只会以为模型翻成这样。
                        if (metrics.Truncated)
                        {
                            ShowLanguageHint(
                                $"译文达到输出上限（{OllamaClient.MaxOutputTokens} token），可能不完整");
                        }
                    },
                });
        }
        catch (Exception ex)
        {
            bubble.Text = $"翻译失败：{ex.Message}";
            bubble.IsStreaming = false;
        }
        finally
        {
            bubble.IsStreaming = false;
            _busy = false;
            SetInputEnabled(true);
            InputBox.Focus();

            // 标题可能因首条消息而更新，刷新列表
            RefreshThreads();
            SelectThreadInList(_currentThreadId);
        }
    }

    /// <summary>
    /// 模型不在显存里时，把气泡的流式提示改成「模型加载中…」。
    /// </summary>
    /// <param name="bubble">目标气泡。</param>
    /// <param name="stillWaiting">
    /// 判断该气泡是否仍在等待。
    /// <b>探测结果可能晚于首块内容到达</b>，那时再改成「模型加载中…」就是在说谎；
    /// 各路径的"等待中"判据不同，由调用方给出。
    /// </param>
    /// <remarks>
    /// 与翻译并行发起，自身不阻塞请求：探测只是为了让用户知道
    /// “现在等的这几秒是在把模型载入显存”，探测不出结果就保持默认文案。
    /// </remarks>
    private async Task ApplyModelLoadHintAsync(BubbleViewModel bubble, Func<bool> stillWaiting)
    {
        try
        {
            if (await _service.NeedsModelLoadAsync() && stillWaiting())
            {
                bubble.StreamingLabel = BubbleViewModel.ModelLoadingLabel;
            }
        }
        catch (Exception ex)
        {
            // 探测失败不影响翻译，只留一条诊断线索
            AppLog.Trace($"探测模型驻留状态失败：{ex.Message}");
        }
    }

    /// <summary>翻译进行中禁用输入，避免并发写同一个会话。</summary>
    private void SetInputEnabled(bool enabled)
    {
        InputBox.IsEnabled = enabled;
        SendButton.IsEnabled = enabled;
        OcrButton.IsEnabled = enabled;

        // 语言选框与互换按钮也必须一起禁用：
        // 它们的选中项变化由 WPF 直接生效，而 OnLanguageChanged 在 _busy 时会跳过 _config.Save()，
        // 于是会出现「界面显示已切到英语、实际仍按旧语言翻译」且毫无提示。
        SourceLangBox.IsEnabled = enabled;
        TargetLangBox.IsEnabled = enabled;
        SwapLangButton.IsEnabled = enabled;

        // 模型下拉同理：翻译中途换模型会让"这次翻译到底用了哪个模型"变得不可知
        ModelBox.IsEnabled = enabled;
    }

    /// <summary>更新状态栏的语言提示。</summary>
    private void UpdateTargetHint(LanguagePair pair)
    {
        var source = pair.Source?.ChineseName ?? "自动";
        StatusTarget.Text = $"{source} → {pair.Target.ChineseName}";

        // 只有本次真的发生了互换才提示；否则必须清掉上一次的提示，
        // 不然它会一直挂在那里，让用户以为每次都换了语言。
        if (pair.Swapped)
        {
            var origin = pair.ActualSource?.ChineseName ?? "原文";
            ShowLanguageHint($"原文是{origin}，已自动译为{pair.Target.ChineseName}");
        }
        else
        {
            HideLanguageHint();
        }
    }

    private void ScrollToBottom() =>
        Dispatcher.BeginInvoke(new Action(() => ChatScroll.ScrollToEnd()),
            System.Windows.Threading.DispatcherPriority.Background);

    // ---------------------------------------------------------------- 会话切换

    private void OnNewThreadClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        // 当前已经是空会话时不再新建：否则连点几次就会在历史里留下一串空的「新对话」条目。
        if (_messages.Count == 0)
        {
            InputBox.Focus();
            return;
        }

        _currentThreadId = _store.CreateThread();
        RefreshThreads();
        SelectThreadInList(_currentThreadId);
        _messages.Clear();
        UpdateWelcomeState();
        InputBox.Focus();
    }

    private void OnThreadSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_busy || ThreadList.SelectedItem is not ThreadViewModel selected)
        {
            return;
        }

        if (selected.Id == _currentThreadId)
        {
            return;
        }

        _currentThreadId = selected.Id;
        LoadThreadMessages(selected.Id);
    }

    // ---------------------------------------------------------------- 归档与删除

    /// <summary>右键先选中被点到的那一行。</summary>
    /// <remarks>
    /// 不这么做的话，菜单会作用于「上一次左键选中的那一项」，与用户的直觉正好相反 ——
    /// 而那一项可能已经被滚出视野，用户根本看不到自己正在归档或删除哪个对话。
    /// </remarks>
    private void OnThreadRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 翻译进行中不改选中项：OnThreadSelected 在 _busy 时会跳过切换，
        // 而选中态是 WPF 直接改的 —— 会出现「高亮在这一行、内容还是上一个会话」的错位。
        if (_busy)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(ThreadList, source) is ListBoxItem item)
        {
            item.IsSelected = true;
        }
    }

    /// <summary>没有可作用的对话时不弹菜单（列表为空，或正被清空）。</summary>
    private void OnThreadMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_busy || ThreadList.SelectedItem is not ThreadViewModel)
        {
            e.Handled = true;
        }
    }

    private void OnArchiveThreadMenuClick(object sender, RoutedEventArgs e)
    {
        if (ThreadList.SelectedItem is ThreadViewModel target)
        {
            ArchiveThread(target);
        }
    }

    private void OnDeleteThreadMenuClick(object sender, RoutedEventArgs e)
    {
        if (ThreadList.SelectedItem is ThreadViewModel target)
        {
            DeleteThread(target);
        }
    }

    /// <summary>归档一个对话：移出侧边栏，内容保留，可在「已归档的对话」窗口里恢复。</summary>
    private void ArchiveThread(ThreadViewModel target)
    {
        if (!CanModify(target))
        {
            return;
        }

        if (_store.SetArchived(target.Id, archived: true))
        {
            AppLog.Info($"归档对话：id={target.Id}「{target.Title}」");
        }

        if (target.Id == _currentThreadId)
        {
            SwitchAwayFromCurrentThread();
        }

        RefreshThreads();
        SelectThreadInList(_currentThreadId);
    }

    /// <summary>删除一个对话（连同消息与截图文件），删除前二次确认。</summary>
    private void DeleteThread(ThreadViewModel target)
    {
        if (!CanModify(target) || !ThreadDialogs.ConfirmDelete(this, target))
        {
            return;
        }

        _store.DeleteThread(target.Id);
        AppLog.Info($"删除对话：id={target.Id}「{target.Title}」");

        if (target.Id == _currentThreadId)
        {
            SwitchAwayFromCurrentThread();
        }

        RefreshThreads();
        SelectThreadInList(_currentThreadId);
    }

    /// <summary>
    /// 这个对话此刻能不能被归档 / 删除。
    /// </summary>
    /// <remarks>
    /// 只拦「正在翻译的当前对话」：翻译途中它会先落用户消息、流式结束后再落译文，
    /// 中途删掉会让第二次写入撞上外键约束抛异常；归档则让译文写进一个用户已经收起来的对话里。
    /// 归档窗口里的对话不可能是当前对话（归档时就已经从它那里切走了），那条路径不需要这道判断。
    /// </remarks>
    private bool CanModify(ThreadViewModel target)
    {
        if (_busy && target.Id == _currentThreadId)
        {
            ThreadDialogs.ShowBusy(this);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 当前对话被归档 / 删除后，把界面切到另一个可用对话上。
    /// </summary>
    /// <remarks>
    /// 必须切换而不是留在原地：留在已经不在列表里的对话上，下一条消息会写进一个
    /// 用户以为已经收起（或以为已经删掉）的对话里，表现为「消息发出去了，历史里却找不到」。
    /// </remarks>
    private void SwitchAwayFromCurrentThread()
    {
        // 优先接住列表里的第一个（_threads 此刻已经不含被归档 / 删除的那个）
        _currentThreadId = _threads.Count > 0
            ? _threads[0].Id
            // 一个都不剩时开一个新的：界面上总要留一个能接收输入的地方
            : _store.CreateThread();

        RefreshThreads();
        SelectThreadInList(_currentThreadId);
        LoadThreadMessages(_currentThreadId);
        InputBox.Focus();
    }

    private void OnArchiveListClick(object sender, RoutedEventArgs e)
    {
        var window = new ArchiveWindow(_store) { Owner = this };
        window.ShowDialog();

        // 窗口里恢复或删除过对话才需要重读；没改动就连查询都不发
        if (window.Changed)
        {
            RefreshThreads();
        }

        // 兜底：归档列表里本不该出现当前对话，但万一它对不上了，
        // 宁可切走，也不要让后续消息写进一个界面上看不见的对话
        if (!_threads.Any(t => t.Id == _currentThreadId))
        {
            SwitchAwayFromCurrentThread();
            return;
        }

        SelectThreadInList(_currentThreadId);
    }

    // ---------------------------------------------------------------- 截图 OCR

    private void OnOcrClick(object sender, RoutedEventArgs e) => _ = CaptureAndTranslateAsync();

    /// <summary>截图 → OCR → 翻译，结果并入当前会话。</summary>
    private async Task CaptureAndTranslateAsync()
    {
        if (_busy)
        {
            return;
        }

        string? imagePath = null;
        var previousState = WindowState;

        try
        {
            // 最小化主窗口，避免把自己截进去。
            // windowRestored 保证任何异常路径（截图、裁剪、OCR 失败）都会复原窗口状态，
            // 否则窗口会一直停在最小化状态，用户既看不到错误气泡也找不回窗口。
            WindowState = WindowState.Minimized;
            await Task.Delay(220);

            var screenshot = ScreenCapture.CaptureVirtualScreen();

            RestoreWindow(ref previousState);

            var region = CaptureOverlay.PickRegion(screenshot);
            if (region is null)
            {
                return;
            }

            var cropped = ScreenCapture.Crop(screenshot, region.Value);

            // 存到应用数据目录，供会话长期回溯（不引用临时文件）
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            using var memory = new MemoryStream();
            encoder.Save(memory);
            imagePath = TranslationService.SaveImageForThread(memory.ToArray());

            var ocr = await OcrService.RecognizeFileAsync(imagePath);
            var recognized = ocr.Text.Replace("\r", " ").Replace("\n", " ").Trim();

            if (string.IsNullOrWhiteSpace(recognized))
            {
                AppLog.Info($"OCR 未识别到文字（区域 {region.Value.Width}×{region.Value.Height}）");
                _messages.Add(new BubbleViewModel
                {
                    Text = "（截图中没有识别到文字）",
                    IsUser = false,
                    TimeText = DateTime.Now.ToString("HH:mm"),
                });
                ScrollToBottom();
                return;
            }

            AppLog.Info($"OCR 识别到 {ocr.Lines.Count} 行、{recognized.Length} 字符");

            // 逐行记录包围盒：替换渲染的字号完全由框高决定，
            // 字号不对时必须有原始数据可查，否则只能靠猜。
            //
            // 用 Trace（受「诊断日志」开关约束）而不是 Info：
            // 这里记的是截图里识别出的<b>原文内容</b>，无条件落盘等于把用户截图里的
            // 文字持续写进磁盘。项目为保护隐私已默认排除密码管理器，这条日志与那个取向矛盾。
            for (var i = 0; i < ocr.Lines.Count; i++)
            {
                var b = ocr.Lines[i].BoundingBox;
                AppLog.Trace($"  行{i + 1} 框=({b.X:F0},{b.Y:F0}) {b.Width:F0}×{b.Height:F0}  [{ocr.Lines[i].Text}]");
            }

            await TranslateOcrAsync(cropped, ocr, recognized, imagePath);
        }
        catch (Exception ex)
        {
            AppLog.Error("截图识别失败", ex);
            _messages.Add(new BubbleViewModel
            {
                Text = $"截图识别失败：{ex.Message}",
                IsUser = false,
                TimeText = DateTime.Now.ToString("HH:mm"),
            });
            ScrollToBottom();
        }
        finally
        {
            // 兜底：无论成功、失败还是提前 return，窗口都必须回到用户原来的状态
            RestoreWindow(ref previousState);
        }
    }

    /// <summary>
    /// OCR 结果的翻译：<b>按行批量翻译一次</b>，同一份结果同时供三处使用。
    /// </summary>
    /// <remarks>
    /// <para>为什么要按行批量，而不是把整段文字丢给模型翻一次：</para>
    /// <list type="bullet">
    /// <item>替换渲染需要知道<b>每一行</b>对应什么译文，才能放回原来的位置；
    /// 整段翻译后无法可靠地切回各行</item>
    /// <item>按行逐条发请求则要十几次往返，很慢。用编号协议一次翻完，
    /// 只有解析失败时才退回逐行</item>
    /// </list>
    /// <para>同一份逐行译文被复用于：替换图、对照列表、复制文本，
    /// 因此只花一次请求。</para>
    /// </remarks>
    private async Task TranslateOcrAsync(
        BitmapSource cropped, OcrOutput ocr, string recognized, string imagePath)
    {
        _busy = true;
        SetInputEnabled(false);

        // 用户气泡（原文 + 截图）必须在翻译前就加入视图。
        // 落库时写的就是这两条，若视图少一条，用户会看到「刚翻完只有译文气泡，
        // 切走再切回却冒出一张截图」的不一致——视图与已持久化内容必须一致。
        _messages.Add(new BubbleViewModel
        {
            Text = recognized,
            IsUser = true,
            TimeText = DateTime.Now.ToString("HH:mm"),
            ImagePath = imagePath,
        });

        var bubble = BubbleViewModel.Streaming();
        _messages.Add(bubble);
        ScrollToBottom();

        try
        {
            var pair = _service.ResolveLanguages(recognized);
            var lineTexts = ocr.Lines.Select(l => l.Text).ToList();

            bubble.Text = $"{OcrProgressPrefix} {lineTexts.Count} 行…";

            // OCR 走的是同一个模型，首次请求同样要先等它载入显存（实测 7.05 s）。
            // 不加这一步的话，同一个窗口里"输入翻译"会提示而"截图 OCR"不会，行为不一致。
            // 判据是占位文案还在：批量翻译没有流式分块，完成时 Text 会被整体替换。
            _ = ApplyModelLoadHintAsync(
                bubble,
                () => bubble.IsStreaming
                      && bubble.Text.StartsWith(OcrProgressPrefix, StringComparison.Ordinal));

            var translations = await BatchTranslator.TranslateLinesAsync(
                _service.GetClient(),
                lineTexts,
                pair.Target,
                pair.Source);

            var items = new List<TranslationItem>(ocr.Lines.Count);
            for (var i = 0; i < ocr.Lines.Count; i++)
            {
                items.Add(new TranslationItem(ocr.Lines[i], translations[i]));
            }

            // 复制/对照用的纯译文：丢掉空行，避免一片空行影响阅读
            var plainText = string.Join(
                Environment.NewLine,
                translations.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()));

            bubble.Text = plainText;

            // 流式指示必须手动收尾：气泡由 Streaming() 创建时 IsStreaming = true，
            // 模板里「生成中」的可见性直接绑定该属性。不复位的话翻译完成后
            // 会一直显示「生成中」。（RunTranslationAsync 在 finally 中收尾，这里没有。）
            bubble.IsStreaming = false;
            ScrollToBottom();

            // 全部行都没翻出内容 = 模型异常，必须让用户看到，而不是给一个空气泡
            if (plainText.Length == 0)
            {
                bubble.Text = "翻译失败：模型没有返回任何译文";
            }

            // 渲染替换图：失败不致命，退回对照模式仍然可用
            BitmapSource? rendered = null;
            string? renderedPath = null;
            try
            {
                rendered = TranslationRenderer.Render(cropped, items);
                AppLog.Info($"替换图渲染完成 {rendered.PixelWidth}×{rendered.PixelHeight}");

                // 一并存档：会话历史里能找回成品图，不必重跑一遍 OCR
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rendered));
                using var memory = new MemoryStream();
                encoder.Save(memory);
                renderedPath = TranslationService.SaveImageForThread(memory.ToArray());
            }
            catch (Exception ex)
            {
                AppLog.Error("替换图渲染失败", ex);
            }

            // 与 P1 行为一致：OCR 结果计入会话历史（划词则不入，见 SelectionWatcher）
            _store.AddMessage(_currentThreadId, isUser: true, recognized, imagePath);
            _store.AddMessage(_currentThreadId, isUser: false, plainText, renderedPath);
            _store.UpdateTitleIfDefault(_currentThreadId, recognized);

            // RefreshThreads 内部会 _threads.Clear()，这会把 ThreadList 的选中项一并清掉
            // （侧边栏高亮消失，用户看不出当前在哪个会话）。RunTranslationAsync 同一位置
            // 显式补了选中，这里也要补。
            RefreshThreads();
            SelectThreadInList(_currentThreadId);

            ShowResultWindow(cropped, rendered, items, plainText);
        }
        catch (Exception ex)
        {
            AppLog.Error("OCR 翻译失败", ex);
            bubble.Text = $"翻译失败：{ex.Message}";
            bubble.IsStreaming = false;
        }
        finally
        {
            _busy = false;
            SetInputEnabled(true);
        }
    }

    /// <summary>显示结果窗口。同一时间只保留一个，避免连点堆出一串窗口。</summary>
    private void ShowResultWindow(
        BitmapSource original, BitmapSource? rendered,
        IReadOnlyList<TranslationItem> items, string plainText)
    {
        if (_resultWindow is { IsLoaded: true })
        {
            _resultWindow.Close();
        }

        var window = new ResultWindow(original, rendered, items, plainText) { Owner = this };
        _resultWindow = window;
        window.Show();
    }

    /// <summary>把主窗口恢复到截图前的状态并激活。已在正常状态时不做任何事。</summary>
    private void RestoreWindow(ref WindowState previousState)
    {
        if (WindowState != WindowState.Minimized)
        {
            return;
        }

        WindowState = previousState == WindowState.Minimized ? WindowState.Normal : previousState;
        Activate();
    }

    // ---------------------------------------------------------------- 划词开关

    private void OnSelectionToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _config.Current.SelectionEnabled = SelectionToggle.IsChecked == true;
        _config.Save();

        ApplySelectionWatcherState();
    }

    /// <summary>
    /// 按配置启用/停用划词监听。
    /// </summary>
    /// <remarks>
    /// 安装失败（钩子被安全软件拦截等）时必须把开关拨回去，
    /// 否则界面显示"已开启"但实际不工作，用户会以为功能坏了。
    /// </remarks>
    private void ApplySelectionWatcherState()
    {
        var wantEnabled = _config.Current.SelectionEnabled;

        if (wantEnabled == _selectionWatcher.IsEnabled)
        {
            return;
        }

        if (wantEnabled)
        {
            var error = _selectionWatcher.Enable();
            if (error is not null)
            {
                _initializing = true;
                SelectionToggle.IsChecked = false;
                _initializing = false;

                _config.Current.SelectionEnabled = false;
                _config.Save();

                ShowLanguageHint($"无法开启划词监听：{error}");
            }
        }
        else
        {
            _selectionWatcher.Disable();
            HideLanguageHint();

            // 关掉开关后全局钩子不再触发，"点外部关闭"也就失效了，
            // 此时若浮窗还开着就会一直留在屏幕上——顺手收掉。
            _popup?.CloseIfNotPinned();
        }
    }

    /// <summary>检测到可翻译的划词内容：在光标处弹窗。</summary>
    private void OnSelectionDetected(SelectionHit hit)
    {
        // 取词通道一并传入：Ctrl+C 回退通道会短暂占用用户剪贴板，
        // 让用户能看到"这次走的是哪条路"。
        ShowPopupAt(popup =>
            popup.ShowForAsync(hit.Text, _service, hit.ScreenX, hit.ScreenY, hit.Method));
    }

    /// <summary>划词内容过长：只提示，不翻译。</summary>
    private void OnSelectionTooLong(string message, int screenX, int screenY)
    {
        ShowPopupAt(popup =>
        {
            popup.ShowTooLong(message, screenX, screenY);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 全局左键抬起：浮窗开着且点在它外面就关掉。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不用失焦事件</b>：浮窗的「失焦自动关闭」依赖
    /// <see cref="Window.Deactivated"/>，而该事件只在窗口<b>真正取得过焦点</b>后才会触发。
    /// 划词时前台是别的应用，Windows 的前台锁定不允许后台程序抢焦点
    /// （实测：浮窗弹出后前台窗口仍是记事本），于是 Deactivated 永不触发，
    /// 浮窗就一直留在屏幕上——这就是"有时不自动消失"的原因，
    /// 取决于 Activate() 是否碰巧成功。</para>
    ///
    /// <para>改用全局鼠标钩子判定"点在外面"，不依赖焦点，因此必然生效。
    /// 失焦关闭仍然保留，作为浮窗确实拿到焦点时的补充路径。</para>
    /// </remarks>
    private void OnGlobalLeftClick(MouseUpEvent e)
    {
        var popup = _popup;
        if (popup is null)
        {
            return;
        }

        // 点在浮窗内（拖动、点按钮、选中文字）不关
        if (popup.ContainsScreenPoint(e.ScreenX, e.ScreenY))
        {
            return;
        }

        popup.CloseIfNotPinned();
    }

    /// <summary>
    /// 弹出一个浮窗。同一时间只保留一个——弹新的先关旧的，
    /// 否则连续划词会在屏幕上堆出一串窗口。
    /// </summary>
    /// <param name="show">负责显示内容；坐标由调用方在闭包里传给浮窗自身。</param>
    private void ShowPopupAt(Func<TranslatePopup, Task> show)
    {
        var previous = _popup;
        _popup = null;

        if (previous is not null)
        {
            try
            {
                previous.Close();
            }
            catch (Exception ex)
            {
                AppLog.Error("关闭旧浮窗失败", ex);
            }
        }

        var popup = new TranslatePopup();
        _popup = popup;

        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_popup, popup))
            {
                _popup = null;
            }
        };

        // 不 await：翻译在后台流式进行，界面不应被阻塞。
        //
        // 但必须自己接住异常，两种失败形态都要覆盖：
        //  - 同步 lambda（ShowTooLong 路径）抛出的异常会直接冒泡到 UI 线程，
        //    成为 Dispatcher 未处理异常；
        //  - async lambda 在首个 await 之前的异常（ResolveLanguages / Show）
        //    会被封装进这个被丢弃的 Task，成为"无人观察的异常"而静默丢失。
        try
        {
            _ = show(popup);
        }
        catch (Exception ex)
        {
            AppLog.Error("显示划词浮窗失败", ex);
        }
    }

    // ---------------------------------------------------------------- 语言选择

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _busy)
        {
            return;
        }

        if (SourceLangBox.SelectedItem is AppLanguage source)
        {
            _config.Current.SourceLanguage = source.Code;
        }

        if (TargetLangBox.SelectedItem is AppLanguage target)
        {
            _config.Current.TargetLanguage = target.Code;
        }

        _config.Save();
        RefreshLanguageHint();
    }

    /// <summary>互换输入与目标语言。输入语言为"自动检测"时无法互换。</summary>
    private void OnSwapLanguagesClick(object sender, RoutedEventArgs e)
    {
        if (_busy || SourceLangBox.SelectedItem is not AppLanguage source)
        {
            return;
        }

        if (source.Code == AutoDetect.Code)
        {
            ShowLanguageHint("输入语言为自动检测，无法互换");
            return;
        }

        if (TargetLangBox.SelectedItem is not AppLanguage target)
        {
            return;
        }

        _initializing = true;
        try
        {
            SourceLangBox.SelectedItem = Languages.All.FirstOrDefault(l => l.Code == target.Code);
            TargetLangBox.SelectedItem = Languages.All.FirstOrDefault(l => l.Code == source.Code);

            _config.Current.SourceLanguage = target.Code;
            _config.Current.TargetLanguage = source.Code;
            _config.Save();
        }
        finally
        {
            _initializing = false;
        }

        RefreshLanguageHint();
    }

    /// <summary>
    /// 刷新语言提示：当输入与目标语言相同时给出可见的警示。
    /// 此时服务层会自动换到另一种语言，必须让用户知道，不能静默处理。
    /// </summary>
    private void RefreshLanguageHint()
    {
        if (SourceLangBox.SelectedItem is AppLanguage source
            && TargetLangBox.SelectedItem is AppLanguage target
            && source.Code != AutoDetect.Code
            && source.Code == target.Code)
        {
            var fallback = target.Code switch
            {
                "zh" => "英语",
                "en" => "中文",
                _ => null,
            };

            ShowLanguageHint(fallback is null
                ? "输入与目标语言相同"
                : $"输入与目标相同，将自动译为{fallback}");
        }
        else
        {
            HideLanguageHint();
        }
    }

    private void ShowLanguageHint(string text)
    {
        LangHint.Text = text;
        LangHint.Visibility = Visibility.Visible;
    }

    private void HideLanguageHint() => LangHint.Visibility = Visibility.Collapsed;

    // ---------------------------------------------------------------- 热键

    private void RegisterHotkeys()
    {
        var config = _config.Current;

        var windowHotkey = ParseHotkey(config.HotkeyMainWindow);
        if (windowHotkey is { } wh)
        {
            _windowHotkeyId = _hotkeys.Register(wh.Modifiers, wh.Key, config.HotkeyMainWindow, out var error);
            if (_windowHotkeyId < 0)
            {
                System.Diagnostics.Debug.WriteLine(error);
            }
        }

        var ocrHotkey = ParseHotkey(config.HotkeyOcr);
        if (ocrHotkey is { } oh)
        {
            _ocrHotkeyId = _hotkeys.Register(oh.Modifiers, oh.Key, config.HotkeyOcr, out var error);
            if (_ocrHotkeyId < 0)
            {
                System.Diagnostics.Debug.WriteLine(error);
            }
        }
    }

    private void OnHotkeyPressed(int id)
    {
        if (id == _windowHotkeyId)
        {
            Dispatcher.Invoke(() =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }

                Activate();
                InputBox.Focus();
            });
        }
        else if (id == _ocrHotkeyId)
        {
            _ = Dispatcher.InvokeAsync(CaptureAndTranslateAsync);
        }
    }

    /// <summary>
    /// 把 "Ctrl+Alt+O" 解析成修饰键 + 虚拟键码；无法解析返回 null。
    /// </summary>
    /// <remarks>
    /// 校验规则（都是必要的，否则会注册出危险或无效的组合）：
    /// <list type="bullet">
    /// <item><b>必须至少有一个修饰键</b>——Win32 会成功注册「无修饰键」的字母键，
    /// 结果是该键在全系统范围内被吞掉，用户从此无法正常输入字母 A</item>
    /// <item><b>只允许一个主键</b>——"Ctrl+A+B" 若取最后一个键，用户以为绑的是 A+B，
    /// 实际只绑了 B，且设置界面的校验会放行</item>
    /// </list>
    /// </remarks>
    public static (HotkeyModifiers Modifiers, uint Key)? ParseHotkey(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var modifiers = HotkeyModifiers.None;
        uint key = 0;
        var keyCount = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "WIN":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
            }

            keyCount++;
            if (keyCount > 1)
            {
                // 出现第二个主键：拒绝，而不是悄悄取最后一个
                return null;
            }

            if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
            {
                key = char.ToUpperInvariant(part[0]);
            }
            else if (part.Length is 2 or 3
                     && (part[0] is 'F' or 'f')
                     && int.TryParse(part[1..], out var fn)
                     && fn is >= 1 and <= 24)
            {
                key = (uint)(0x70 + fn - 1);   // VK_F1 = 0x70
            }
            else
            {
                return null;
            }
        }

        if (key == 0 || modifiers == HotkeyModifiers.None)
        {
            return null;
        }

        return (modifiers, key);
    }

    // ---------------------------------------------------------------- 设置 / 状态

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_config) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        // 设置里可能改了快捷键：先全部注销再按新配置注册
        _hotkeys.UnregisterAll();
        _windowHotkeyId = -1;
        _ocrHotkeyId = -1;
        RegisterHotkeys();

        // 也可能改了语言：同步主界面的下拉选中项与语言提示。
        // 配置读的是实时值，不同步的话会出现「界面显示旧语言、实际按新语言翻译」。
        // 用 _initializing 包住，避免触发 OnLanguageChanged 再写一次配置。
        _initializing = true;
        try
        {
            // 重建下拉：设置里可能增删了「可选语种」，
            // 只改选中项的话列表内容还是旧的（新勾的语种不会出现、取消的仍在）
            InitializeLanguageSelectors();

            // 设置里也可能换了模型。直接重建（而不是等异步刷新）：
            // 若此刻 Ollama 恰好连不上，刷新会保持现状，界面就会停在旧模型上，
            // 与刚保存的配置不一致
            InitializeModelSelector();

            // 诊断日志开关可能也被改了，必须立即生效——否则要重启才起作用，
            // 与「划词没反应时打开它去排查」的用途不符，用户会以为功能失效。
            AppLog.Verbose = _config.Current.DiagnosticLogging;
        }
        finally
        {
            _initializing = false;
        }

        UpdateTargetHint(new LanguagePair(
            _service.ConfiguredSource, _service.ConfiguredTarget, Swapped: false));
        RefreshLanguageHint();

        // 服务地址/模型可能改了，重新探测 Ollama 状态，并补齐模型清单
        _ = CheckOllamaAsync();
        _ = RefreshModelListAsync();
    }

    private async Task CheckOllamaAsync()
    {
        StatusOllama.Text = "正在连接 Ollama…";

        try
        {
            var client = _service.GetClient();
            var models = await client.ListModelsAsync();
            if (models is null)
            {
                StatusDot.Fill = StatusBrush("SystemFillColorCriticalBrush", 0xE5, 0x39, 0x35);
                StatusOllama.Text = "Ollama 未连接";
            }
            else if (!models.Any(m => m.Equals(_config.Current.Model, StringComparison.OrdinalIgnoreCase)))
            {
                StatusDot.Fill = StatusBrush("SystemFillColorCautionBrush", 0xFF, 0xB3, 0x00);
                StatusOllama.Text = $"缺少模型 {_config.Current.Model}";
            }
            else
            {
                StatusDot.Fill = StatusBrush("SystemFillColorSuccessBrush", 0x4C, 0xAF, 0x50);
                StatusOllama.Text = $"Ollama 已连接 · {_config.Current.Model}";
            }
        }
        catch (Exception ex)
        {
            StatusDot.Fill = StatusBrush("SystemFillColorCriticalBrush", 0xE5, 0x39, 0x35);
            StatusOllama.Text = $"Ollama 连接失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 取主题里的语义色笔刷；取不到时回落到给定颜色。
    /// </summary>
    /// <remarks>
    /// 状态指示灯用主题的语义色（成功 / 警告 / 错误）而不是写死 RGB：
    /// 这两种主题下语义色的取值不同，写死会在切换主题后显得突兀。
    /// 保留回落值是为了在主题字典尚未加载时仍有颜色，不至于变成透明。
    /// </remarks>
    private static Brush StatusBrush(string resourceKey, byte r, byte g, byte b) =>
        Application.Current?.TryFindResource(resourceKey) as Brush
        ?? new SolidColorBrush(Color.FromRgb(r, g, b));

    protected override void OnClosed(EventArgs e)
    {
        _selectionWatcher.SelectionDetected -= OnSelectionDetected;
        _selectionWatcher.ContentTooLong -= OnSelectionTooLong;
        _selectionWatcher.GlobalLeftClick -= OnGlobalLeftClick;
        _selectionWatcher.Dispose();

        try
        {
            _popup?.Close();
        }
        catch
        {
            // 关闭浮窗失败不影响退出
        }

        try
        {
            _resultWindow?.Close();
        }
        catch
        {
            // 关闭结果窗口失败不影响退出
        }

        _hotkeys.Dispose();
        _service.Dispose();
        _store.Dispose();
        base.OnClosed(e);
    }
}
