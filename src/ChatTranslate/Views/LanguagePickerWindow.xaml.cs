using System.Windows;
using Wpf.Ui.Controls;

namespace ChatTranslate.Views;

/// <summary>
/// 「下拉里显示哪些语种」的选择窗口：把语种平铺出来勾选。
/// </summary>
/// <remarks>
/// 单独开窗口的原因：语种有 37 个，直接平铺在设置页里会占掉大半个页面，
/// 把真正的设置项（服务、快捷键、划词）全挤到下面。
///
/// 只负责挑选，不读写配置 —— 调用方拿到 <see cref="SelectedCodes"/> 后，
/// 仍由设置窗口统一在「保存」时落盘，这样用户在设置窗口点「取消」时
/// 对语种的选择也一并作废，不会出现"语种改了但别的设置没保存"的半吊子状态。
/// </remarks>
public partial class LanguagePickerWindow : FluentWindow
{
    private readonly List<LanguageChip> _chips;

    public LanguagePickerWindow(IEnumerable<LanguageChip> source)
    {
        InitializeComponent();

        // 深拷贝：点「取消」不能改到设置窗口里的原始状态
        _chips = source.Select(chip => chip.Clone()).ToList();
        foreach (var chip in _chips)
        {
            chip.PropertyChanged += (_, _) => UpdateCount();
        }

        LanguageTileList.ItemsSource = _chips;
        UpdateCount();
    }

    /// <summary>点「确定」后的结果；其它情况（取消 / 关窗口）为空。</summary>
    public IReadOnlyList<string> SelectedCodes { get; private set; } = [];

    /// <summary>已选数量。置灰项也算选中，否则计数会与最终保存结果对不上。</summary>
    private void UpdateCount()
    {
        var count = _chips.Count(c => c.IsSelected || !c.CanToggle);
        CountHint.Text = $"已选 {count} 种";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        SelectedCodes = _chips
            .Where(c => c.IsSelected || !c.CanToggle)
            .Select(c => c.Code)
            .ToList();

        DialogResult = true;
        Close();
    }
}
