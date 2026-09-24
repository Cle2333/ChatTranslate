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
        AppLog.Info($"===== 启动：版本 {typeof(MainWindow).Assembly.GetName().Version} =====");
        await CheckOllamaAsync();

        InputBox.Focus();
    }

    private void RefreshThreads()
    {
        _threads.Clear();
        foreach (var thread in _store.ListThreads())
        {
            _threads.Add(ThreadViewModel.From(thread));
        }
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

    // ---------------------------------------------------------------- 输入翻译

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = SendAsync();
        }
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

            bubble.Text = $"正在翻译 {lineTexts.Count} 行…";

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

        // 服务地址/模型可能改了，重新探测 Ollama 状态
        _ = CheckOllamaAsync();
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
