using System.IO;
using System.Windows;
using ChatTranslate.Data;
using ChatTranslate.Services;
using Wpf.Ui.Controls;

namespace ChatTranslate.Views;

/// <summary>设置窗口：服务、快捷键、划词、数据位置。</summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly ConfigStore _config;

    public SettingsWindow(ConfigStore config)
    {
        InitializeComponent();
        _config = config;

        var current = _config.Current;
        HostBox.Text = current.OllamaHost;
        ModelBox.Text = current.Model;
        NumCtxBox.Text = current.NumCtx.ToString();
        HotkeyOcrBox.Text = current.HotkeyOcr;
        HotkeyWindowBox.Text = current.HotkeyMainWindow;
        DelayBox.Text = current.SelectionDelayMs.ToString();
        DiagnosticToggle.IsChecked = current.DiagnosticLogging;

        // null 防御：配置文件被外部编辑成 null 时不能让设置窗口崩掉
        BlacklistBox.Text = string.Join(
            Environment.NewLine, current.SelectionBlacklist ?? []);
        DataPathBox.Text = AppPaths.DataRoot;

        LanguageBox.ItemsSource = Services.Languages.All;
        LanguageBox.DisplayMemberPath = nameof(Services.Language.ChineseName);
        LanguageBox.SelectedItem =
            Services.Languages.ByCode(_config.Current.TargetLanguage) ?? Services.Languages.Default;

        // 输入语言：自动检测 + 全部语种
        var sources = new List<Services.Language> { AutoDetectOption };
        sources.AddRange(Services.Languages.All);
        SourceLanguageBox.ItemsSource = sources;
        SourceLanguageBox.DisplayMemberPath = nameof(Services.Language.ChineseName);
        SourceLanguageBox.SelectedItem =
            sources.FirstOrDefault(l => l.Code == current.SourceLanguage) ?? AutoDetectOption;
    }

    /// <summary>输入语言下拉里的"自动检测"哨兵项。</summary>
    internal static readonly Services.Language AutoDetectOption =
        new("auto", "自动检测", "Auto Detect");

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

        if (LanguageBox.SelectedItem is Services.Language language)
        {
            target.TargetLanguage = language.Code;
        }

        if (SourceLanguageBox.SelectedItem is Services.Language source)
        {
            target.SourceLanguage = source.Code;
        }

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
