using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ChatTranslate.Core;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace ChatTranslate.Views;

/// <summary>对照模式的一行：序号 + 原文 + 译文。</summary>
public sealed record CompareRow(string Index, string Source, string Target);

/// <summary>
/// OCR 翻译结果窗口，三种查看模式：
/// <list type="bullet">
/// <item><b>替换</b>——译文覆盖原文后的成品图，可直接保存分享</item>
/// <item><b>对照</b>——原图 + 逐行原文↔译文，便于核对识别是否准确</item>
/// <item><b>复制</b>——纯译文文本</item>
/// </list>
/// </summary>
public partial class ResultWindow : FluentWindow
{
    private readonly BitmapSource _original;
    private readonly BitmapSource? _rendered;
    private readonly string _plainText;

    public ResultWindow(
        BitmapSource original,
        BitmapSource? rendered,
        IReadOnlyList<TranslationItem> items,
        string plainText)
    {
        InitializeComponent();

        _original = original;
        _rendered = rendered;
        _plainText = plainText;

        ReplaceImage.Source = rendered;
        CompareImage.Source = original;
        CopyBox.Text = plainText;

        var rows = new List<CompareRow>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            rows.Add(new CompareRow(
                (i + 1).ToString(),
                items[i].Line.Text,
                items[i].Translation));
        }

        CompareList.ItemsSource = rows;

        // 没有渲染结果时直接落到"对照"，避免停在一个空白页签上
        SetMode(rendered is null ? ResultMode.Compare : ResultMode.Replace);

        if (rendered is null)
        {
            StatusText.Text = "未能生成替换图，已切换到对照模式";
        }
    }

    private enum ResultMode
    {
        Replace,
        Compare,
        Copy,
    }

    private void OnModeReplace(object sender, RoutedEventArgs e) => SetMode(ResultMode.Replace);

    private void OnModeCompare(object sender, RoutedEventArgs e) => SetMode(ResultMode.Compare);

    private void OnModeCopy(object sender, RoutedEventArgs e) => SetMode(ResultMode.Copy);

    private void SetMode(ResultMode mode)
    {
        // 替换图缺失时不放开该模式，否则用户会看到一个空面板
        if (mode == ResultMode.Replace && _rendered is null)
        {
            mode = ResultMode.Compare;
        }

        ReplacePanel.Visibility = mode == ResultMode.Replace ? Visibility.Visible : Visibility.Collapsed;
        ComparePanel.Visibility = mode == ResultMode.Compare ? Visibility.Visible : Visibility.Collapsed;
        CopyPanel.Visibility = mode == ResultMode.Copy ? Visibility.Visible : Visibility.Collapsed;

        ReplaceModeButton.Appearance = mode == ResultMode.Replace
            ? ControlAppearance.Primary : ControlAppearance.Secondary;
        CompareModeButton.Appearance = mode == ResultMode.Compare
            ? ControlAppearance.Primary : ControlAppearance.Secondary;
        CopyModeButton.Appearance = mode == ResultMode.Copy
            ? ControlAppearance.Primary : ControlAppearance.Secondary;

        if (mode == ResultMode.Replace)
        {
            StatusText.Text = $"{_rendered!.PixelWidth} × {_rendered.PixelHeight} 像素";
        }
    }

    private void OnCopyText(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_plainText.Length == 0)
            {
                StatusText.Text = "没有可复制的内容";
                return;
            }

            Clipboard.SetText(_plainText);
            StatusText.Text = "译文已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private void OnSaveImage(object sender, RoutedEventArgs e)
    {
        if (_rendered is null)
        {
            StatusText.Text = "没有可保存的替换图";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存替换图",
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = $"translated-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            DefaultExt = ".png",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_rendered));

            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);

            StatusText.Text = $"已保存：{Path.GetFileName(dialog.FileName)}";
            AppLog.Info($"替换图已保存：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失败：{ex.Message}";
            AppLog.Error("保存替换图失败", ex);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
