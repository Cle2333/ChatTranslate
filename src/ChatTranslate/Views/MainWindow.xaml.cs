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

    private readonly ObservableCollection<ThreadViewModel> _threads = [];
    private readonly ObservableCollection<BubbleViewModel> _messages = [];

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

        ThreadList.ItemsSource = _threads;
        MessageList.ItemsSource = _messages;

        _initializing = true;
        SelectionToggle.IsChecked = _config.Current.SelectionEnabled;
        InitializeLanguageSelectors();
        _initializing = false;

        // 事件订阅只做一次。
        // 放进 RegisterHotkeys() 会导致每次保存设置（都会重注册热键）都多挂一份订阅，
        // 于是按一下热键触发 N 次回调、订阅链无限增长。
        _hotkeys.HotkeyPressed += OnHotkeyPressed;

        Loaded += OnLoaded;
    }

    /// <summary>填充输入/目标语言下拉，并按配置选中当前值。</summary>
    private void InitializeLanguageSelectors()
    {
        // 输入语言：自动检测 + 全部语种
        var sources = new List<AppLanguage> { AutoDetect };
        sources.AddRange(Languages.All);
        SourceLangBox.ItemsSource = sources;
        SourceLangBox.DisplayMemberPath = nameof(AppLanguage.ChineseName);
        SourceLangBox.SelectedItem =
            sources.FirstOrDefault(l => l.Code == _config.Current.SourceLanguage) ?? AutoDetect;

        // 目标语言：全部语种（不含自动检测——目标必须明确）
        TargetLangBox.ItemsSource = Languages.All;
        TargetLangBox.DisplayMemberPath = nameof(AppLanguage.ChineseName);
        TargetLangBox.SelectedItem =
            Languages.ByCode(_config.Current.TargetLanguage) ?? Languages.Default;
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

        ScrollToBottom();
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

        _currentThreadId = _store.CreateThread();
        RefreshThreads();
        SelectThreadInList(_currentThreadId);
        _messages.Clear();
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
                _messages.Add(new BubbleViewModel
                {
                    Text = "（截图中没有识别到文字）",
                    IsUser = false,
                    TimeText = DateTime.Now.ToString("HH:mm"),
                });
                ScrollToBottom();
                return;
            }

            // 用户气泡显示截图本身
            _messages.Add(new BubbleViewModel
            {
                Text = recognized,
                IsUser = true,
                TimeText = DateTime.Now.ToString("HH:mm"),
                ImagePath = imagePath,
            });
            ScrollToBottom();

            await RunTranslationAsync(recognized, imagePath);
        }
        catch (Exception ex)
        {
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
            if (SourceLangBox.ItemsSource is IEnumerable<AppLanguage> sources)
            {
                SourceLangBox.SelectedItem =
                    sources.FirstOrDefault(l => l.Code == _config.Current.SourceLanguage);
            }

            TargetLangBox.SelectedItem =
                Languages.ByCode(_config.Current.TargetLanguage);
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
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
                StatusOllama.Text = "Ollama 未连接";
            }
            else if (!models.Any(m => m.Equals(_config.Current.Model, StringComparison.OrdinalIgnoreCase)))
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00));
                StatusOllama.Text = $"缺少模型 {_config.Current.Model}";
            }
            else
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                StatusOllama.Text = $"Ollama 已连接 · {_config.Current.Model}";
            }
        }
        catch (Exception ex)
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            StatusOllama.Text = $"Ollama 连接失败：{ex.Message}";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotkeys.Dispose();
        _service.Dispose();
        _store.Dispose();
        base.OnClosed(e);
    }
}
