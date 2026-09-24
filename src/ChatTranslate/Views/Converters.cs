using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ChatTranslate.Views;

/// <summary>
/// 把图片文件路径转成可绑定的 BitmapImage。
///
/// <para>必须用 <see cref="BitmapCacheOption.OnLoad"/> 并在加载后立即释放流：
/// 否则 WPF 会一直持有文件句柄，后续删除或覆盖该文件都会失败。</para>
/// </summary>
public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // 加载完即释放文件句柄
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();                                  // 跨线程安全 + 提升性能
            return bitmap;
        }
        catch
        {
            // 图片损坏 / 被占用时不应让整个界面崩掉
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>取反的布尔→可见性转换（true→Collapsed）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>布尔→可见性：true→Visible。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 根据气泡是否带图，选用纯文本模板或图片模板。
/// </summary>
public sealed class BubbleTemplateSelector : System.Windows.Controls.DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }

    public DataTemplate? ImageTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is BubbleViewModel bubble && bubble.HasImage)
        {
            return ImageTemplate ?? TextTemplate;
        }

        return TextTemplate;
    }
}
