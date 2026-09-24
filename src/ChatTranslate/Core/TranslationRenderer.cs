using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChatTranslate.Core;

/// <summary>待渲染的一行：原文行（含包围盒）+ 对应译文。</summary>
public sealed record TranslationItem(OcrLine Line, string Translation);

/// <summary>替换渲染的样式。</summary>
public sealed record TextRenderStyle
{
    /// <summary>是否用取样的背景色盖住原文。</summary>
    public bool MaskBackground { get; init; } = true;

    /// <summary>描边，保证复杂背景下仍可辨认。</summary>
    public bool Outline { get; init; } = true;

    /// <summary>
    /// 可读字号下限。缩小到此值仍放不下就不再缩，改为向下扩框。
    /// </summary>
    /// <remarks>
    /// 定这个下限的原因：中英互译长度常差 1.5–2 倍。若一味缩小以求"塞进原框"，
    /// 短框里的译文会被压成蚂蚁字——那样的"替换成功"没有意义。
    /// 宁可把图向下扩一点，也要保证看得清。
    /// </remarks>
    public double ReadableFontSize { get; init; } = 11;

    /// <summary>字号上限，避免原文框异常大时画出巨字。</summary>
    public double MaxFontSize { get; init; } = 48;

    /// <summary>放不下时是否允许向下扩框（必要时连画布一起加高）。</summary>
    public bool AllowExpand { get; init; } = true;

    /// <summary>画布最多向下扩展为原图的多少倍，防止异常输入撑出巨图。</summary>
    public double MaxCanvasGrowth { get; init; } = 3.0;

    /// <summary>字体。中文用雅黑，兼顾可读性与系统可用性。</summary>
    public string FontFamily { get; init; } = "Microsoft YaHei UI, Microsoft YaHei";
}

/// <summary>
/// 把译文渲染回原截图，覆盖掉原文区域。
/// </summary>
/// <remarks>
/// <para><b>难点在于原文与译文的排版差异</b>：Windows OCR 只给词级包围盒，
/// 没有字体、字号、行高信息。中英互译长度又常差 1.5–2 倍，
/// 所以只能靠包围盒反推字号；装不下就换行、缩字号，再装不下就向下扩框。</para>
///
/// <para><b>遮罩颜色从原图取样</b>，而不是写死黑或白：
/// 截图背景可能是任何颜色，写死的遮罩会像打了一块补丁。</para>
///
/// <para><b>文字颜色按遮罩亮度自动取反</b>：遮罩亮则用深色字，遮罩暗则用浅色字，
/// 这样无论原文是白底黑字还是黑底白字，替换后都保持原来的视觉风格且必然可读。</para>
///
/// <para><b>两阶段处理</b>：先对全部行做排版测量，得到最终画布高度，再统一绘制。
/// 单阶段做不到——只有先知道所有行扩到多高，才能确定画布要加高多少。</para>
/// </remarks>
public static class TranslationRenderer
{
    private const int MaskPadding = 2;
    private const double LineGap = 2;

    /// <summary>
    /// 画布至少向下扩展这么多像素（在倍数上限之外另给的保底量）。
    /// 避免矮选区被倍数上限卡住而裁掉译文。
    /// </summary>
    private const double MinimumCanvasGrowth = 300;

    /// <summary>一行的最终排版结果。</summary>
    /// <remarks>
    /// <see cref="FontSize"/> 必须留存：描边粗细要按字号算，
    /// 而 <c>FormattedText.Height</c> 是整块文本高度（随换行数变化），不能用。
    /// </remarks>
    private sealed record LinePlan(
        Rect Box,
        FormattedText Text,
        double FontSize,
        Color Background,
        Color TextColor,
        Color OutlineColor);

    /// <summary>
    /// 渲染替换结果。
    /// </summary>
    /// <param name="source">原始截图（裁剪后的小图）。</param>
    /// <param name="items">按原文位置排列的译文项。</param>
    public static BitmapSource Render(
        BitmapSource source,
        IReadOnlyList<TranslationItem> items,
        TextRenderStyle? style = null)
    {
        var options = style ?? new TextRenderStyle();
        var width = source.PixelWidth;
        var imageHeight = source.PixelHeight;

        // 统一转成 Bgra32：后续要按固定步长取像素做背景取样
        var bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        bgra.Freeze();
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);

        // 自上而下处理，这样每行的可用扩展空间只受它下面那一行限制
        var ordered = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Translation))
            .OrderBy(i => i.Line.BoundingBox.Top)
            .ToList();

        var typeface = new Typeface(
            new FontFamily(options.FontFamily),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);

        // ===== 阶段一：排版测量 =====
        // 先算出每行"应该用多高的框"。同一段文字里，各行字形不同
        // （有 / 无下伸部的字母会让 OCR 框高差几像素），若各按自己的框高定字号，
        // 同一段译文会出现明显的大小不一 —— 而原文本来是同一字号。
        var uniformHeights = ComputeUniformHeights(ordered);

        var plans = new List<LinePlan>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var prevBottom = i > 0 ? ordered[i - 1].Line.BoundingBox.Bottom : 0;
            var nextTop = i + 1 < ordered.Count
                ? ordered[i + 1].Line.BoundingBox.Top
                : double.MaxValue;

            var plan = PlanLine(ordered[i], uniformHeights[i], pixels, bgra.PixelWidth, imageHeight,
                prevBottom, nextTop, typeface, options);

            if (plan is not null)
            {
                plans.Add(plan);
            }
        }

        // ===== 决定画布高度 =====
        var needed = plans.Count == 0
            ? imageHeight
            : (int)Math.Ceiling(plans.Max(p => p.Box.Bottom));

        // 上限取「按倍数」与「至少多给一段固定高度」的较大者。
        // 只用倍数的话，矮选区会吃亏：图高 20px 时 3 倍上限只有 60px，
        // 而一行长译文换行后可能要 100px —— 底部会被静默裁掉。
        var maxHeight = (int)Math.Ceiling(Math.Max(
            imageHeight * Math.Max(1.0, options.MaxCanvasGrowth),
            imageHeight + MinimumCanvasGrowth));

        var canvasHeight = Math.Clamp(needed, imageHeight, maxHeight);

        // 真被截断时必须留痕：否则用户只看到译文莫名少了一块，无从判断原因
        if (needed > canvasHeight)
        {
            AppLog.Warn($"替换图高度被上限截断：需要 {needed}px，画布 {canvasHeight}px，"
                        + $"底部约 {needed - canvasHeight}px 的内容会被裁掉");
        }

        // ===== 阶段二：绘制 =====
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, width, imageHeight));

            // 画布加高后露出的区域：用底边像素的平均色填满，
            // 视觉上像"画面自然延续"，而不是凭空多出一条透明或黑色
            if (canvasHeight > imageHeight)
            {
                var extension = new SolidColorBrush(AverageBottomRow(pixels, width, imageHeight));
                extension.Freeze();
                dc.DrawRectangle(extension, null,
                    new Rect(0, imageHeight, width, canvasHeight - imageHeight));
            }

            foreach (var plan in plans)
            {
                if (options.MaskBackground)
                {
                    var maskBrush = new SolidColorBrush(plan.Background);
                    maskBrush.Freeze();
                    dc.DrawRectangle(maskBrush, null, plan.Box);
                }

                var origin = new Point(
                    plan.Box.Left,
                    plan.Box.Top + Math.Max(0, (plan.Box.Height - plan.Text.Height) / 2));

                var geometry = plan.Text.BuildGeometry(origin);
                if (geometry is null)
                {
                    continue;
                }

                if (options.Outline)
                {
                    // 按<b>字号</b>算描边粗细，不能用 FormattedText.Height ——
                    // 后者是整块文本的高度（首行顶到末行底）。译文被约束在原文框宽度内
                    // 几乎必然换行，于是换行越多描边越粗：字号 15px 时单行约 1.5px 尚可，
                    // 3 行后 Height≈50 → 描边 2.8px，而 CJK 笔画本身只有 1~2px，
                    // 结果文字被黑边糊成一团，且同一张图里各行描边权重还会随换行数浮动。
                    var thickness = Math.Max(1.5, plan.FontSize * 0.07);
                    var pen = new Pen(new SolidColorBrush(plan.OutlineColor), thickness);
                    pen.Freeze();
                    dc.DrawGeometry(null, pen, geometry);
                }

                var fill = new SolidColorBrush(plan.TextColor);
                fill.Freeze();
                dc.DrawGeometry(fill, null, geometry);
            }
        }

        var target = new RenderTargetBitmap(width, canvasHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>
    /// 计算每行统一后的目标框高。
    /// </summary>
    /// <remarks>
    /// <para>要解决两个噪声来源：</para>
    /// <list type="number">
    /// <item>OCR 的框高等于"该行实际出现的字形"高度。含 g/p/y 的行更高，
    /// 不含的行更矮 —— 同一段文字里能差 20–30%（实测 18 vs 23）。</item>
    /// <item>OCR 偶发误读（如把 <c>Artificial</c> 读成 <c>Ärtificial</c>）会凭空
    /// 抬高某一行的框高（实测同一行在两次运行中分别是 23 和 28）。</item>
    /// </list>
    ///
    /// <para>因此以<b>全局中位数</b>为锚（对单个异常值稳健），把落在
    /// ±35% 范围内的行视为同一字号，并取这些行的<b>中位数</b>作为统一框高 ——
    /// 不用最大值，否则一个误读就能把所有行都撑大。
    /// 范围之外的行（如真正的标题）保留自己的框高，避免标题被压成正文大小。</para>
    /// </remarks>
    private static double[] ComputeUniformHeights(IReadOnlyList<TranslationItem> ordered)
    {
        var result = new double[ordered.Count];
        if (ordered.Count == 0)
        {
            return result;
        }

        var heights = ordered.Select(i => i.Line.BoundingBox.Height).ToArray();
        var positive = heights.Where(h => h > 0).OrderBy(h => h).ToArray();

        if (positive.Length == 0)
        {
            return result;
        }

        var median = Median(positive);
        if (median <= 0)
        {
            // 实际不可达（positive 只含 > 0 的值）。
            // 返回 result 而不是 heights：后者是未排序、可能含 0/负值的原数组，
            // 与其它分支"返回统一后的目标框高"的语义不一致，会误导后续维护者。
            return result;
        }

        const double Tolerance = 1.35;
        var low = median / Tolerance;
        var high = median * Tolerance;

        var body = positive.Where(h => h >= low && h <= high).ToArray();
        var bodySize = body.Length > 0 ? Median(body) : median;

        for (var k = 0; k < heights.Length; k++)
        {
            result[k] = heights[k] >= low && heights[k] <= high ? bodySize : heights[k];
        }

        return result;
    }

    private static double Median(double[] sorted)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>对一行做排版测量，返回它最终占用的矩形与文字尺寸。</summary>
    private static LinePlan? PlanLine(
        TranslationItem item,
        double uniformHeight,
        byte[] pixels,
        int imageWidth,
        int imageHeight,
        double prevBottom,
        double nextTop,
        Typeface typeface,
        TextRenderStyle style)
    {
        var box = item.Line.BoundingBox;
        if (box.Width <= 0 || box.Height <= 0)
        {
            return null;
        }

        // ── 遮罩：至少要盖住原文，所以取"统一框高"与"本行实际框高"的较大者 ──
        // 本行框高可能因字形/误读而偏大（实测 28 vs 23），遮罩必须覆盖它，
        // 否则原文会露出边角。
        var cover = Math.Max(uniformHeight, box.Height);
        var center = box.Top + box.Height / 2;
        var top = center - cover / 2;
        var bottom = center + cover / 2;

        // 夹进相邻行之间：绝不能吃掉上下行的原文。
        //
        // 但只在"邻行确实在本行外侧"时才夹取。多列排版（网页/报纸式两栏，OCR 会按列
        // 给出多行）或 OCR 把同一排文字切成两行时，下一行的 Top 会落在本行框<b>之内</b>，
        // 此时 bottom 会被压到 top 附近，遮罩退化成 1~5px 的细缝 ——
        // 原文没被盖住、译文直接叠在原文上，两行都成乱码。
        if (prevBottom > 0 && prevBottom + LineGap < bottom)
        {
            top = Math.Max(top, prevBottom + LineGap);
        }

        if (nextTop != double.MaxValue && nextTop - LineGap > top)
        {
            bottom = Math.Min(bottom, nextTop - LineGap);
        }

        top = Math.Max(0, top);
        bottom = Math.Min(imageHeight, Math.Max(bottom, top + 1));

        var maskLeft = Math.Max(0, box.Left - MaskPadding);
        var mask = new Rect(
            maskLeft,
            Math.Max(0, top - MaskPadding),
            Math.Min(imageWidth - maskLeft, box.Width + MaskPadding * 2),
            Math.Max(1, (bottom - top) + MaskPadding * 2));

        if (mask.Width <= 1 || mask.Height <= 1)
        {
            return null;
        }

        // ── 字号：依据是"统一框高"，不是遮罩高度 ──
        // 用遮罩高度定字号会让框高大的行自动用大字（正是字号不齐的原因）。
        var fitHeight = uniformHeight + MaskPadding * 2;

        var background = style.MaskBackground
            ? SampleBackground(pixels, imageWidth, imageHeight, mask)
            : Colors.Transparent;

        // 文字颜色按遮罩亮度取反，保证对比度
        var darkMask = style.MaskBackground && Luminance(background) < 0.5;
        var textColor = darkMask ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x1A, 0x1A, 0x1A);
        var outlineColor = darkMask ? Color.FromArgb(0xCC, 0, 0, 0) : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);

        var brush = new SolidColorBrush(textColor);
        brush.Freeze();

        // 允许向下扩展的边界：不越过下一行顶边（避免盖住下一行）
        var allowedBottom = nextTop == double.MaxValue
            ? double.MaxValue
            : Math.Min(imageHeight, nextTop - LineGap);

        // 下限不能超过统一框高：原文本身就是小字时（如 9px 的界面文字），
        // 不该为了"可读"把它撑成大字，那反而不像原图了
        var floor = Math.Min(style.ReadableFontSize, uniformHeight);
        var size = Math.Clamp(uniformHeight, floor, style.MaxFontSize);

        while (size >= floor)
        {
            var candidate = Measure(item.Translation, typeface, size, mask.Width, brush);

            // 用统一的 fitHeight 判定，而不是 mask.Height ——
            // 后者各行不同（因字形/误读而异的框高），正是字号不齐的根源
            if (candidate.Height <= fitHeight + 0.5)
            {
                return new LinePlan(mask, candidate, size, background, textColor, outlineColor);
            }

            // 已经到下限，再缩就是蚂蚁字了
            if (size <= floor + 0.01)
            {
                break;
            }

            size = Math.Max(floor, size * 0.92);
        }

        if (!style.AllowExpand)
        {
            var squeezed = Measure(item.Translation, typeface, floor, mask.Width, brush);
            return new LinePlan(mask, squeezed, floor, background, textColor, outlineColor);
        }

        // 保持可读字号，向下扩框；必要时连画布一起加高
        var final = Measure(item.Translation, typeface, floor, mask.Width, brush);
        var targetHeight = Math.Max(mask.Height, final.Height + MaskPadding * 2);

        // 下一行压在下面时不能扩过去（会盖掉它的原文），只能限制在原框内
        if (allowedBottom != double.MaxValue)
        {
            var available = allowedBottom - mask.Top;
            if (available > mask.Height)
            {
                targetHeight = Math.Min(targetHeight, available);
            }
            else
            {
                targetHeight = mask.Height;
            }
        }

        var grown = new Rect(mask.Left, mask.Top, mask.Width, targetHeight);

        // 按新高度重挑一次字号，可能能放回更大的字。
        // 从统一框高起步（而不是从 grown.Height），保持与他行一致的字号基准
        var size2 = Math.Clamp(Math.Max(uniformHeight, floor), floor, style.MaxFontSize);
        while (size2 >= floor)
        {
            var candidate = Measure(item.Translation, typeface, size2, grown.Width, brush);
            if (candidate.Height <= grown.Height + 0.5)
            {
                return new LinePlan(grown, candidate, size2, background, textColor, outlineColor);
            }

            if (size2 <= floor + 0.01)
            {
                break;
            }

            size2 = Math.Max(floor, size2 * 0.92);
        }

        return new LinePlan(grown, final, floor, background, textColor, outlineColor);
    }

    private static FormattedText Measure(
        string text, Typeface typeface, double size, double maxWidth, Brush brush) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            1.0)
        {
            // 只限制宽度以触发换行；不设 MaxLineCount，
            // 否则高度会被截断，算不出真实需要多少空间
            MaxTextWidth = Math.Max(1, maxWidth),
            Trimming = TextTrimming.None,
        };

    /// <summary>
    /// 从原图取样背景色：取框内出现频率最高的颜色。
    /// </summary>
    /// <remarks>
    /// 用"出现次数最多"而不是"平均值"：平均值会被文字像素拉向中间色（白底黑字会算出灰），
    /// 得到一块明显的补丁色。最高频颜色在文字占比不高的截图里基本就是真实背景。
    /// 颜色先量化到 5 位/通道再统计，避免渐变背景把计数打散。
    /// </remarks>
    private static Color SampleBackground(byte[] pixels, int width, int height, Rect box)
    {
        var left = Math.Max(0, (int)box.Left);
        var top = Math.Max(0, (int)box.Top);
        var right = Math.Min(width, (int)Math.Ceiling(box.Right));
        var bottom = Math.Min(height, (int)Math.Ceiling(box.Bottom));

        if (right <= left || bottom <= top)
        {
            return Colors.Black;
        }

        var histogram = new Dictionary<int, (int Count, long R, long G, long B)>();

        for (var y = top; y < bottom; y++)
        {
            var rowOffset = y * width * 4;
            for (var x = left; x < right; x++)
            {
                var o = rowOffset + x * 4;
                var b = pixels[o];
                var g = pixels[o + 1];
                var r = pixels[o + 2];

                var key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);

                if (histogram.TryGetValue(key, out var entry))
                {
                    histogram[key] = (entry.Count + 1, entry.R + r, entry.G + g, entry.B + b);
                }
                else
                {
                    histogram[key] = (1, r, g, b);
                }
            }
        }

        if (histogram.Count == 0)
        {
            return Colors.Black;
        }

        var best = histogram.MaxBy(kv => kv.Value.Count).Value;
        return Color.FromRgb(
            (byte)(best.R / best.Count),
            (byte)(best.G / best.Count),
            (byte)(best.B / best.Count));
    }

    /// <summary>底边像素的平均色，用于填充画布加高后露出的区域。</summary>
    private static Color AverageBottomRow(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return Colors.Black;
        }

        long r = 0, g = 0, b = 0;
        var rowOffset = (height - 1) * width * 4;

        for (var x = 0; x < width; x++)
        {
            var o = rowOffset + x * 4;
            b += pixels[o];
            g += pixels[o + 1];
            r += pixels[o + 2];
        }

        return Color.FromRgb((byte)(r / width), (byte)(g / width), (byte)(b / width));
    }

    private static double Luminance(Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
