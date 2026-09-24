using System.Diagnostics;
using System.IO;
using System.Windows;
using ChatTranslate.Core;
using ChatTranslate.Services;

namespace ChatTranslate;

/// <summary>
/// P0 可行性自检面板。
/// P1 起本窗口会替换为正式界面（侧边栏 + 对话区 + 状态栏），此处仅用于验证底层能力。
/// </summary>
public partial class MainWindow : Window
{
    private const string OllamaHost = "http://127.0.0.1:11434";
    private const string DefaultModel = "hy-mt2:7b-q4km";

    private HotkeyManager? _hotkeys;
    private int _testHotkeyId = -1;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Append("就绪。建议先点「检测环境」。\n");
    }

    // ---------------------------------------------------------------- 环境

    private async void OnCheckEnvironment(object sender, RoutedEventArgs e)
    {
        await GuardAsync(async () =>
        {
            Append("═══ 环境检测 ═══");

            var languages = OcrService.AvailableLanguages;
            Append($"系统 OCR 语言包：{(languages.Count == 0 ? "（无！）" : string.Join(", ", languages))}");
            Append($"OCR 单图最大边长：{OcrService.MaxImageDimension} px");

            var bounds = ScreenCapture.GetVirtualScreenBounds();
            Append($"虚拟屏幕：{bounds.Width}×{bounds.Height} @ ({bounds.X},{bounds.Y})");

            using var client = new OllamaClient(OllamaHost, DefaultModel);
            var models = await client.ListModelsAsync();
            if (models is null)
            {
                Append($"Ollama：❌ 无法连接 {OllamaHost}（请确认服务已启动）");
            }
            else
            {
                Append($"Ollama：✅ 已连接，共 {models.Count} 个模型");
                foreach (var m in models)
                {
                    Append($"    · {m}{(m.Contains("hy-mt2", StringComparison.OrdinalIgnoreCase) ? "   ← 翻译模型" : string.Empty)}");
                }
            }

            EnvText.Text =
                $"OCR 语言包：{string.Join(" / ", languages)}\n" +
                $"虚拟屏幕：{bounds.Width}×{bounds.Height}（多显示器已合并）\n" +
                $"Ollama：{(models is null ? "未连接" : $"已连接（{models.Count} 个模型）")}\n" +
                $"目标模型：{DefaultModel}";

            Append(string.Empty);
        });
    }

    // ---------------------------------------------------------------- 取词

    private async void OnGrabSelection(object sender, RoutedEventArgs e)
    {
        await GuardAsync(async () =>
        {
            Append("═══ 取词测试 ═══");
            Append("请在 3 秒内切换到其他窗口（记事本 / 浏览器 / Word），选中一段文字…");

            for (var i = 3; i > 0; i--)
            {
                Append($"    {i}…");
                await Task.Delay(1000);
            }

            var sw = Stopwatch.StartNew();
            var result = await Task.Run(SelectionGrabber.Grab);
            sw.Stop();

            if (result.Text is null)
            {
                Append($"❌ 未取到文本（{sw.ElapsedMilliseconds} ms）：{result.Note}");
            }
            else
            {
                var method = result.Method == SelectionMethod.Uia ? "UIA（未碰剪贴板）" : "Ctrl+C 回退（剪贴板已还原）";
                Append($"✅ 取到 {result.Text.Length} 字符，通道：{method}，耗时 {sw.ElapsedMilliseconds} ms");
                Append($"    原文：{Preview(result.Text)}");
            }

            Append(string.Empty);
        });
    }

    // ---------------------------------------------------------------- OCR

    private async void OnOcrTest(object sender, RoutedEventArgs e)
    {
        await GuardAsync(async () =>
        {
            Append("═══ OCR 测试 ═══");

            var sw = Stopwatch.StartNew();
            var screenshot = ScreenCapture.CaptureVirtualScreen();
            Append($"已抓取全屏：{screenshot.PixelWidth}×{screenshot.PixelHeight} px（{sw.ElapsedMilliseconds} ms）");

            var imagePath = Path.Combine(Path.GetTempPath(), "ChatTranslate", "p0_ocr.png");
            ScreenCapture.SavePng(screenshot, imagePath);
            Append($"已存临时文件：{imagePath}");

            sw.Restart();
            var output = await OcrService.RecognizeFileAsync(imagePath);
            sw.Stop();

            var wordCount = output.Lines.Sum(l => l.Words.Count);
            Append($"✅ 识别完成：{output.Lines.Count} 行 / {wordCount} 词，耗时 {sw.ElapsedMilliseconds} ms");
            Append($"    全文预览：{Preview(output.Text)}");

            if (output.Lines.Count > 0)
            {
                var first = output.Lines[0];
                var box = first.BoundingBox;
                Append($"    首行包围盒：({box.X:F0},{box.Y:F0}) {box.Width:F0}×{box.Height:F0}");
                if (first.Words.Count > 0)
                {
                    var w0 = first.Words[0];
                    Append($"    首词「{w0.Text}」包围盒：({w0.BoundingBox.X:F0},{w0.BoundingBox.Y:F0}) " +
                           $"{w0.BoundingBox.Width:F0}×{w0.BoundingBox.Height:F0}  ← 词级坐标可用于译文替换");
                }
            }

            Append(string.Empty);
        });
    }

    // ---------------------------------------------------------------- 热键

    private void OnHotkeyTest(object sender, RoutedEventArgs e)
    {
        try
        {
            _hotkeys ??= new HotkeyManager();
            _hotkeys.HotkeyPressed += OnHotkeyPressed;

            if (_testHotkeyId >= 0)
            {
                _hotkeys.Unregister(_testHotkeyId);
                _testHotkeyId = -1;
                Append("已注销测试热键。");
                return;
            }

            var id = _hotkeys.Register(
                HotkeyModifiers.Control | HotkeyModifiers.Alt,
                0x54,   // VK_T
                "Ctrl+Alt+T",
                out var error);

            if (id < 0)
            {
                Append($"❌ {error}");
                return;
            }

            _testHotkeyId = id;
            Append("═══ 热键测试 ═══");
            Append("✅ 已注册 Ctrl+Alt+T —— 请切到其他窗口按下该组合键，回到本窗口应看到触发记录。");
            Append("    （再次点击本按钮可注销）");
            Append(string.Empty);
        }
        catch (Exception ex)
        {
            Append($"❌ 热键管理器初始化失败：{ex.Message}");
        }
    }

    private void OnHotkeyPressed(int id)
    {
        if (id == _testHotkeyId)
        {
            Append($"✅ 热键触发于 {DateTime.Now:HH:mm:ss.fff}");
        }
    }

    // ---------------------------------------------------------------- 翻译

    private async void OnTranslateTest(object sender, RoutedEventArgs e)
    {
        await GuardAsync(async () =>
        {
            Append("═══ 翻译测试 ═══");

            const string source = "人工智能正在深刻改变我们的生活方式。";
            var prompt = OllamaClient.BuildTranslatePrompt(source, Languages.ByCode("en")!);
            Append($"原文：{source}");

            using var client = new OllamaClient(OllamaHost, DefaultModel);

            var sw = Stopwatch.StartNew();
            var buffer = new System.Text.StringBuilder();
            await foreach (var piece in client.StreamAsync([ChatMessage.User(prompt)]))
            {
                buffer.Append(piece);
            }
            sw.Stop();

            var text = OllamaClient.CleanOutput(buffer.ToString());
            Append($"译文：{text}");
            Append($"✅ 流式完成，耗时 {sw.Elapsed.TotalSeconds:F2} s");

            var reply = await client.ChatAsync([ChatMessage.User(prompt)]);
            var m = reply.Metrics;
            Append($"    指标：prompt={m.PromptEvalCount} tok，输出={m.EvalCount} tok，" +
                   $"速度={m.TokensPerSecond:F1} tok/s，加载={(m.LoadDurationNs / 1e9):F2} s");
            Append(string.Empty);
        });
    }

    // ---------------------------------------------------------------- 剪贴板保护

    private async void OnClipboardTest(object sender, RoutedEventArgs e)
    {
        await GuardAsync(async () =>
        {
            Append("═══ 剪贴板保护测试 ═══");
            Append("步骤：先复制一段文字（或一张图片），点住本按钮后…");
            Append("现在开始：3 秒后执行「备份 → 模拟 Ctrl+C → 还原」全流程。");

            for (var i = 3; i > 0; i--)
            {
                Append($"    {i}…");
                await Task.Delay(1000);
            }

            // 注意：后台线程不能直接更新 UI，日志先在后台收集再统一输出
            var lines = await Task.Run(() =>
            {
                var log = new List<string>();

                var before = ClipboardBackup.Capture();
                log.Add($"    备份完成：{before.Count} 项已知格式（剪贴板序号 {ClipboardBackup.SequenceNumber()}）");

                // 故意破坏剪贴板，模拟取词过程中剪贴板被我们占用的情形
                ClipboardBackup.Restore([]);
                log.Add($"    已清空剪贴板，序号变为 {ClipboardBackup.SequenceNumber()}");

                ClipboardBackup.Restore(before);
                log.Add($"    还原完成，序号 {ClipboardBackup.SequenceNumber()}");

                var text = ClipboardBackup.GetText();
                log.Add(string.IsNullOrEmpty(text)
                    ? "    ℹ 剪贴板原有内容不是文本（可能是图片 / 文件），已按已知格式还原"
                    : $"    还原后的文本：{Preview(text)}");

                foreach (var entry in before)
                {
                    entry.Dispose();
                }

                return log;
            });

            foreach (var line in lines)
            {
                Append(line);
            }

            Append("✅ 剪贴板保护流程执行完毕。请手动粘贴验证内容是否完好。");
            Append(string.Empty);
        });
    }

    // ---------------------------------------------------------------- 辅助

    private void OnClearLog(object sender, RoutedEventArgs e) => LogBox.Clear();

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Append($"❌ 异常：{ex.GetType().Name}：{ex.Message}");
        }
    }

    private void Append(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private static string Preview(string text)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotkeys?.Dispose();
        base.OnClosed(e);
    }
}
