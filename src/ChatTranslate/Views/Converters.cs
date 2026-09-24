using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ChatTranslate.Views;

/// <summary>
/// 把图片文件路径转成可绑定的 BitmapImage。
///
/// <para>只做「路径 → 位图」这一件事。原先同文件里还有一个「布尔取反→可见性」转换器，
/// 全工程无引用，已删除——留着容易被误以为已经生效。</para>
/// </summary>
public sealed class PathToImageConverter : IValueConverter
{
    /// <summary>
    /// 解码宽度上限。
    /// 气泡里图片最大显示到约 480 DIP 宽、360 DIP 高，按 200% 缩放预留 960 像素足够；
    /// 不设上限的话，大块截图会以原始像素整张解码并常驻内存。
    /// </summary>
    private const int DecodeWidth = 960;

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

            // 加载完即释放文件句柄，否则 WPF 会一直持有该文件，后续删除/覆盖都会失败
            bitmap.CacheOption = BitmapCacheOption.OnLoad;

            // 在解码阶段就降采样，避免整张原始位图常驻内存
            bitmap.DecodePixelWidth = DecodeWidth;

            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();   // 跨线程安全 + 提升性能
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
