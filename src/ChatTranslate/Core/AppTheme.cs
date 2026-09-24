using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace ChatTranslate.Core;

/// <summary>
/// 应用的主题与强调色。
/// </summary>
/// <remarks>
/// <para><b>强调色固定为品牌蓝，不跟随 Windows 系统强调色。</b>
/// Wpf.Ui 默认取系统强调色，于是同一份界面在不同机器上配色不同：
/// 本机系统强调色是品红，用户气泡与侧边栏选中项就成了紫粉色，
/// 与项目其余部分的色卡（<c>#0A84F4</c>）不一致。固定之后界面外观与机器设置无关。</para>
///
/// <para>只改强调色、不改主题：底色仍走 Wpf.Ui 的 Fluent 令牌，因此这一处调整
/// 不涉及任何控件的可读性——强调色上的文字颜色由
/// <c>TextOnAccentFillColorPrimaryBrush</c> 自动取反。</para>
/// </remarks>
public static class AppTheme
{
    /// <summary>品牌蓝。</summary>
    public static readonly Color Accent = Color.FromRgb(0x0A, 0x84, 0xF4);

    /// <summary>
    /// 主题的声明值。必须与 <c>App.xaml</c> 里 <c>&lt;ui:ThemesDictionary Theme="..." /&gt;</c> 一致。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="ResolveTheme"/> 读取时以运行时实际主题为准，这里只是取不到时的兜底。
    /// </remarks>
    public const ApplicationTheme Theme = ApplicationTheme.Dark;

    /// <summary>
    /// 把强调色写进应用资源。
    /// </summary>
    /// <remarks>
    /// 必须在 <c>App.xaml</c> 的资源字典加载之后调用（即 <c>OnStartup</c> 里）：
    /// 字典随后加载会覆盖这一步的结果，顺序反了就白做。
    /// </remarks>
    public static void Apply()
    {
        var theme = ResolveTheme();

        ApplicationAccentColorManager.Apply(
            Accent,
            theme,
            systemGlassColor: false,    // 不走玻璃色提亮，保持色卡原值
            systemAccentColor: false);  // 这不是系统强调色——正是要覆盖它

        AppLog.Info($"强调色已应用：{Accent}（主题 {theme}）");
    }

    /// <summary>
    /// 取实际生效的主题。
    /// </summary>
    /// <remarks>
    /// 主题本来只写在 <c>App.xaml</c> 的 <c>ThemesDictionary Theme</c> 里，
    /// <see cref="Theme"/> 是第二份拷贝——两处不一致时，
    /// <c>ApplicationAccentColorManager.Apply</c> 会按<b>错误的主题</b>计算强调色的明暗档与
    /// 「强调色上的文字色」，界面静默走样（例如深字压在品牌蓝上），从报错里完全看不出。
    /// 因此以运行时实际主题为准，不一致时留一条 WARN。
    /// </remarks>
    private static ApplicationTheme ResolveTheme()
    {
        ApplicationTheme actual;

        try
        {
            actual = ApplicationThemeManager.GetAppTheme();
        }
        catch (Exception ex)
        {
            // 取不到就用声明值，不因为一个观感参数打断启动
            AppLog.Trace($"读取当前主题失败，改用声明值 {Theme}：{ex.Message}");
            return Theme;
        }

        if (actual is ApplicationTheme.Unknown)
        {
            return Theme;
        }

        if (actual != Theme)
        {
            AppLog.Warn($"AppTheme.Theme={Theme} 与运行时实际主题 {actual} 不一致，"
                        + "已按实际主题应用强调色；请把 App.xaml 的 ThemesDictionary 与本文件的 Theme 改成一致");
        }

        return actual;
    }
}
