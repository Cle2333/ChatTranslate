using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace WpfUiSpike;

/// <summary>侧边栏历史条目。</summary>
public sealed class HistoryItem
{
    public string Title { get; init; } = string.Empty;
    public string Time { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
}

/// <summary>对话气泡。</summary>
public sealed class Bubble
{
    public string Text { get; init; } = string.Empty;
    public bool IsUser { get; init; }
    public string Time { get; init; } = string.Empty;
}

/// <summary>
/// 界面 spike：验证 WPF + WPF-UI 能否做出 codex 式布局与观感。
/// 仅为视觉验证，不含业务逻辑。
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        LoadSampleData();
    }

    private void LoadSampleData()
    {
        HistoryList.ItemsSource = new List<HistoryItem>
        {
            new() { Title = "人工智能正在深刻改变我们的生活方式", Time = "刚刚", Source = "输入" },
            new() { Title = "The quick brown fox jumps over…", Time = "12 分钟前", Source = "划词" },
            new() { Title = "这款新显卡的性能比其前代产品高出 40%", Time = "1 小时前", Source = "截图" },
            new() { Title = "Please submit your report before…", Time = "今天 14:20", Source = "划词" },
            new() { Title = "会议因为技术故障推迟了半个小时", Time = "昨天", Source = "输入" },
            new() { Title = "Machine translation has improved…", Time = "昨天", Source = "划词" },
        };

        MessageList.ItemsSource = new List<Bubble>
        {
            new()
            {
                Text = "你好，我是 ChatTranslate。\n可以输入文字翻译，也可以开启左上角的划词开关，选中任意位置的字句直接翻译。\n全程本地运行，内容不会离开这台电脑。",
                IsUser = false,
                Time = "20:41",
            },
            new()
            {
                Text = "人工智能正在深刻改变我们的生活方式。",
                IsUser = true,
                Time = "20:41",
            },
            new()
            {
                Text = "Artificial intelligence is profoundly changing the way we live.",
                IsUser = false,
                Time = "20:41",
            },
            new()
            {
                Text = "这家餐厅的红烧肉做得非常地道，值得一试。",
                IsUser = true,
                Time = "20:42",
            },
            new()
            {
                Text = "The braised pork at this restaurant is remarkably authentic — well worth trying.",
                IsUser = false,
                Time = "20:42",
            },
        };
    }

    private void OnSelectionToggled(object sender, RoutedEventArgs e)
    {
        SelectionHint.Text = SelectionToggle.IsChecked == true ? "选中即翻译" : "已关闭";
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) { }

    private void OnNewChatClick(object sender, RoutedEventArgs e) { }

    private void OnSendClick(object sender, RoutedEventArgs e) { }

    private void OnInputKeyDown(object sender, KeyEventArgs e) { }
}
