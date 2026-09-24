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

    /// <summary>当前主题。<c>App.xaml</c> 里 <c>ThemesDictionary Theme</c> 的值必须与它一致。</summary>
    public const ApplicationTheme Theme = ApplicationTheme.Dark;

    /// <summary>
    /// 把强调色写进应用资源。
    /// </summary>
    /// <remarks>
    /// 必须在 <c>App.xaml</c> 的资源字典加载之后调用（即 <c>OnStartup</c> 里）：
    /// 字典随后加载会覆盖这一步的结果，顺序反了就白做。
    /// </remarks>
    public static void Apply() =>
        ApplicationAccentColorManager.Apply(
            Accent,
            Theme,
            systemGlassColor: false,    // 不走玻璃色提亮，保持色卡原值
            systemAccentColor: false);  // 这不是系统强调色——正是要覆盖它
}
