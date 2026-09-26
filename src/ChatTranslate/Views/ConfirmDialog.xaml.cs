using System.Windows;
using ChatTranslate.Core;
using Wpf.Ui.Controls;

namespace ChatTranslate.Views;

/// <summary>
/// 应用风格的确认 / 提示对话框（替代系统 MessageBox）。
/// </summary>
/// <remarks>
/// 系统 MessageBox 是浅色的原生模板，在深色界面里会弹出一块白底，与整体风格脱节。
/// 这里用 FluentWindow + 主题资源做同等功能的一版。
/// </remarks>
public partial class ConfirmDialog : FluentWindow
{
    private ConfirmDialog()
    {
        InitializeComponent();

        // 高度由内容决定，但**不能用 SizeToContent="Height"**，见 TightenHeight 的说明。
        Loaded += (_, _) => TightenHeight();
    }

    /// <summary>
    /// 按内容的实际高度收紧窗口，去掉底部多出来的那一截。
    /// </summary>
    /// <remarks>
    /// <c>SizeToContent="Height"</c> 与 FluentWindow 的窗口 chrome 叠加时会多算高度：
    /// 实测窗口 480 物理像素（320 DIP），而内容只有约 210 DIP —— 底部空出 110 DIP。
    /// 而且这一截是"窗口比内容高"，在内容里怎么调边距都消不掉。
    ///
    /// 做法：布局完成后取内容自身想要的高度，关掉 SizeToContent 再显式设高。
    /// 用 <c>DesiredSize</c> 而不是 <c>ActualHeight</c> —— 后者在窗口拉伸下等于可用高度，
    /// 减出来的差就恒为 0，什么都不会发生。
    /// </remarks>
    private void TightenHeight()
    {
        var content = RootGrid.DesiredSize.Height;

        // 内容还没量出来就别动：宁可留一点空隙，也不能把内容裁掉
        if (content <= 0)
        {
            return;
        }

        SizeToContent = SizeToContent.Manual;
        Height = content;

        // 诊断级：正常时不需要，但窗口尺寸再出怪事时，这一行能直接指出差在哪
        AppLog.Trace($"确认框按内容收紧：窗口 {ActualHeight:F1} → {content:F1} DIP");
    }

    /// <summary>
    /// 弹出一个确认框。
    /// </summary>
    /// <param name="owner">属主窗口。</param>
    /// <param name="headline">标题行（一句话说清要做什么）。</param>
    /// <param name="body">正文（后果说明）。</param>
    /// <param name="confirmText">确认按钮文字。</param>
    /// <param name="danger">
    /// 确认按钮是否用危险配色。用于删除这类不可撤销的操作 ——
    /// 与「确定」区分开，避免和普通操作共用同一套视觉。
    /// </param>
    /// <returns>用户是否点了确认按钮。直接关窗口 / Esc / 点取消都返回 false。</returns>
    public static bool Confirm(
        Window owner,
        string headline,
        string body,
        string confirmText,
        bool danger = false)
    {
        var dialog = new ConfirmDialog { Owner = owner };

        dialog.DialogTitleBar.Title = headline;
        dialog.Title = headline;
        dialog.HeadlineText.Text = headline;
        dialog.BodyText.Text = body;
        dialog.ConfirmButton.Content = confirmText;

        if (danger)
        {
            dialog.ConfirmButton.Appearance = ControlAppearance.Danger;
            dialog.DialogIcon.Symbol = SymbolRegular.Delete24;
            dialog.DialogIcon.Foreground =
                (System.Windows.Media.Brush)Application.Current.FindResource("SystemFillColorCriticalBrush");
        }

        return dialog.ShowDialog() == true;
    }

    /// <summary>弹一个只有「知道了」的提示框（用于「翻译进行中」这类告知）。</summary>
    public static void Inform(Window owner, string headline, string body)
    {
        var dialog = new ConfirmDialog { Owner = owner };

        dialog.DialogTitleBar.Title = headline;
        dialog.Title = headline;
        dialog.HeadlineText.Text = headline;
        dialog.BodyText.Text = body;
        dialog.ConfirmButton.Visibility = Visibility.Collapsed;
        dialog.CancelButton.Content = "知道了";
        dialog.DialogIcon.Symbol = SymbolRegular.Info24;

        dialog.ShowDialog();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
