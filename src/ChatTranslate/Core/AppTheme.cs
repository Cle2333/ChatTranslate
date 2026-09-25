using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ChatTranslate.Core;

/// <summary>
/// 应用的主题与强调色。
/// </summary>
/// <remarks>
/// <para>主题有三档：<b>跟随系统 / 亮色 / 深色</b>，取值存在配置里（<c>AppConfig.Theme</c>），
/// 本类是它的<b>唯一真源</b>。此前主题写死在 <c>App.xaml</c> 的
/// <c>&lt;ui:ThemesDictionary Theme="Dark" /&gt;</c>，同时本类还有一份 <c>Theme</c> 常量拷贝；
/// 两份不一致时强调色会按<b>错误的主题</b>计算明暗档与「强调色上的文字色」，
/// 界面静默走样（例如深字压在品牌蓝上），从报错里完全看不出。
/// 改成可切换之后那份拷贝已删除，类里只剩一个来源。</para>
///
/// <para><b>强调色固定为品牌蓝，不跟随 Windows 系统强调色。</b>
/// Wpf.Ui 默认取系统强调色，于是同一份界面在不同机器上配色不同：
/// 本机系统强调色是品红，用户气泡与侧边栏选中项就成了紫粉色，
/// 与项目其余部分的色卡（<c>#0A84F4</c>）不一致。固定之后界面外观与机器设置无关。</para>
///
/// <para>强调色分「亮色档 / 深色档」，取值随主题而变（连"压在强调色上的文字色"也是）。
/// 所以<b>每次换主题都必须重算一次强调色</b>，否则亮色模式下会残留深色档。</para>
/// </remarks>
public static class AppTheme
{
    /// <summary>品牌蓝。</summary>
    public static readonly Color Accent = Color.FromRgb(0x0A, 0x84, 0xF4);

    /// <summary>跟随系统的深浅色设置。</summary>
    public const string ModeSystem = "System";

    /// <summary>强制亮色。</summary>
    public const string ModeLight = "Light";

    /// <summary>强制深色。</summary>
    public const string ModeDark = "Dark";

    private static readonly string[] KnownModes = [ModeSystem, ModeLight, ModeDark];

    private static string _mode = ModeDark;
    private static Window? _watchedWindow;

    /// <summary>强调色当前是按哪个主题算出来的；用来避免重复计算。</summary>
    private static ApplicationTheme? _accentTheme;

    /// <summary>重入保护：应用强调色本身可能再次触发主题变化事件。</summary>
    private static bool _applyingAccent;

    static AppTheme()
    {
        // 主题一旦变化就重算强调色。
        //
        // 为什么不只在 Apply() 里算：跟随系统模式下，系统主题变化由 Wpf.Ui 自己改主题，
        // 不一定经过本类；漏掉这一步，切到系统亮色后强调色的档位还是深色那份。
        ApplicationThemeManager.Changed += (theme, _) =>
        {
            if (_accentTheme != theme)
            {
                ApplyAccent(theme);
            }
        };
    }

    /// <summary>当前主题模式：<see cref="ModeSystem"/> / <see cref="ModeLight"/> / <see cref="ModeDark"/>。</summary>
    public static string CurrentMode => _mode;

    /// <summary>
    /// 把任意字符串收敛成合法模式。
    /// </summary>
    /// <remarks>配置是明文 JSON，被手改成未知值时回落到默认深色，不抛异常。</remarks>
    public static string Normalize(string? mode) =>
        KnownModes.FirstOrDefault(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase))
        ?? ModeDark;

    /// <summary>
    /// 应用主题模式，并记住它。
    /// </summary>
    /// <remarks>
    /// 启动时必须在主窗口创建之前调用（见 <c>App.OnStartup</c>）：那之后才建窗口，
    /// 首帧就是目标配色，不会先出现深色再闪一下变亮色。
    /// </remarks>
    public static void Apply(string mode)
    {
        _mode = Normalize(mode);
        var theme = ResolveTheme(_mode);

        try
        {
            // updateAccent: false —— 强调色由本类统一管（品牌蓝），
            // 不让 Wpf.Ui 顺手改成系统强调色。
            // backdrop 传 Mica：换主题要连背景材质一并重算，否则半透明层的配方还是旧主题的。
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, false);
        }
        catch (Exception ex)
        {
            // 纯观感调整，失败就沿用当前配色，不能因此影响功能
            AppLog.Warn($"应用主题失败（沿用当前配色）：{ex.Message}");
        }

        ApplyAccent(theme);
        SyncSystemWatcher();
        AppLog.Info($"主题已应用：{_mode}（实际 {theme}）");
    }

    /// <summary>
    /// 登记要跟随系统主题的窗口。
    /// </summary>
    /// <remarks>
    /// 主窗口由 <c>App.xaml</c> 的 StartupUri 创建，走到 <c>App.OnStartup</c> 时它还不存在，
    /// 所以系统主题监听只能等窗口出来后再挂（见 <c>MainWindow.OnLoaded</c>）。
    /// </remarks>
    public static void AttachWindow(Window window)
    {
        _watchedWindow = window;
        SyncSystemWatcher();
    }

    /// <summary>按当前模式决定是否监听系统主题变化。</summary>
    private static void SyncSystemWatcher()
    {
        if (_watchedWindow is null)
        {
            return;
        }

        try
        {
            if (_mode == ModeSystem)
            {
                SystemThemeWatcher.Watch(_watchedWindow, WindowBackdropType.Mica, false);
            }
            else
            {
                // 必须显式解绑：留着的话系统主题一变，Wpf.Ui 会把用户明确选定的
                // 亮色/深色覆盖掉，看起来像"设置不生效"。
                SystemThemeWatcher.UnWatch(_watchedWindow);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"切换系统主题监听失败（界面仍按当前主题显示）：{ex.Message}");
        }
    }

    /// <summary>把模式解析成实际要应用的 Wpf.Ui 主题。</summary>
    private static ApplicationTheme ResolveTheme(string mode) => mode switch
    {
        ModeLight => ApplicationTheme.Light,
        ModeDark => ApplicationTheme.Dark,
        _ => SystemThemeOrDark(),
    };

    /// <summary>
    /// 读系统的深浅色设置。
    /// </summary>
    /// <remarks>
    /// 注意返回类型是 <c>SystemTheme</c>（取值有 Light/Dark/HC/Custom 等），
    /// 与 <c>GetAppTheme()</c> 返回的 <c>ApplicationTheme</c> 是两个不同的枚举，不能混用。
    /// 只有明确的 <c>Light</c> 才当亮色；<c>Dark</c>、高对比度、读不到都按深色处理。
    /// </remarks>
    private static ApplicationTheme SystemThemeOrDark()
    {
        try
        {
            return ApplicationThemeManager.GetSystemTheme() is SystemTheme.Light
                ? ApplicationTheme.Light
                : ApplicationTheme.Dark;
        }
        catch (Exception ex)
        {
            AppLog.Trace($"读取系统主题失败，按深色处理：{ex.Message}");
            return ApplicationTheme.Dark;
        }
    }

    /// <summary>
    /// 把品牌蓝按给定主题写进应用资源。
    /// </summary>
    /// <remarks>
    /// 必须在 <c>App.xaml</c> 的资源字典加载之后调用：字典随后加载会覆盖这一步的结果。
    /// </remarks>
    private static void ApplyAccent(ApplicationTheme theme)
    {
        if (_applyingAccent)
        {
            return;
        }

        _applyingAccent = true;
        try
        {
            ApplicationAccentColorManager.Apply(
                Accent,
                theme,
                systemGlassColor: false,    // 不走玻璃色提亮，保持色卡原值
                systemAccentColor: false);  // 这不是系统强调色——正是要覆盖它

            _accentTheme = theme;
            AppLog.Info($"强调色已应用：{Accent}（主题 {theme}）");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"应用强调色失败（沿用 Wpf.Ui 默认强调色）：{ex.Message}");
        }
        finally
        {
            _applyingAccent = false;
        }
    }
}
