namespace ChatTranslate.Services;

/// <summary>
/// 一个待翻译分段。
/// </summary>
/// <param name="Text">分段正文（已去掉首尾空白）。</param>
/// <param name="Separator">
/// 原文里紧跟在它后面的分隔符（<c>"\n\n"</c> / <c>"\n"</c> / <c>" "</c> / 空串）。
/// 拼接译文时要按它还回去，否则段落结构会丢、英文还会把词粘在一起。
/// </param>
public sealed record TextSegment(string Text, string Separator);

/// <summary>
/// 把长文本切成能一次送进模型的分段。
/// </summary>
/// <remarks>
/// <para><b>为什么必须切</b>：单次请求的容量是硬的——prompt 与生成共用 <c>num_ctx</c>，
/// 输出另有 <c>num_predict</c> 上限。超出部分是<b>静默损坏</b>：prompt 超了由 Ollama
/// 丢弃最前面（最老）的内容，输出超了直接截断。用户拿到的是一段
/// 「看起来完整、其实少了一截」的译文，只会以为模型翻得不好。</para>
///
/// <para><b>切点按语义层级逐级退让</b>：空行 → 换行 → 句末标点 → 次级标点 → 空格 → 硬切。
/// 硬切是最后手段（整段没有标点的长串，如 base64），会切断词句，但总好过不发或发出去被截断。</para>
/// </remarks>
public static class TextChunker
{
    /// <summary>句末标点：切在这些字符之后，句子仍完整。</summary>
    private const string SentenceEnders = "。！？!?…；;";

    /// <summary>次级标点：切在分句处，语义损失可接受。</summary>
    private const string ClausePunctuation = "，,、：:）)】」》〉”’";

    /// <summary>尾段小于这个比例时并入上一段，避免为几个字符多发一次请求。</summary>
    private const double MergeTailRatio = 0.2;

    /// <summary>并入尾段后允许的最大超出比例。切分预算是带安全余量的，略微超出无害。</summary>
    private const double MergeStretchRatio = 1.2;

    /// <summary>
    /// 按 <paramref name="maxChars"/> 切分文本。
    /// </summary>
    /// <param name="text">原文。</param>
    /// <param name="maxChars">单段最大字符数。</param>
    /// <returns>分段列表；文本为空时返回空列表。</returns>
    public static IReadOnlyList<TextSegment> Split(string text, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);

        var segments = new List<TextSegment>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        if (text.Length <= maxChars)
        {
            segments.Add(new TextSegment(text.Trim(), string.Empty));
            return segments;
        }

        var start = 0;
        while (start < text.Length)
        {
            var limit = Math.Min(start + maxChars, text.Length);

            if (limit >= text.Length)
            {
                var tail = text[start..].Trim();
                if (tail.Length > 0)
                {
                    segments.Add(new TextSegment(tail, string.Empty));
                }

                break;
            }

            var cut = FindCut(text, start, limit);

            // 切点之后的分隔符归入 Separator，并从下一段的起点跳过它
            var separator = DetectSeparator(text, cut);
            var next = cut + separator.Length;

            var piece = text[start..cut].Trim();
            if (piece.Length > 0)
            {
                segments.Add(new TextSegment(piece, separator));
            }
            else
            {
                // 切点没产出内容（例如窗口内只有分隔符）：不要把分隔符吞掉
                next = cut;
            }

            // 必须严格前进，否则会死循环
            start = next > start ? next : start + 1;
        }

        MergeTinyTail(segments, maxChars);
        return segments;
    }

    /// <summary>
    /// 把各段译文按原文的分隔符拼回去。
    /// </summary>
    /// <param name="segments">切分结果，与 <paramref name="translations"/> 一一对应。</param>
    /// <param name="translations">各段译文。</param>
    /// <remarks>
    /// <b>分隔符原样还回去，不额外补空格</b>：切点是由原文决定的，
    /// 该有空格的地方原文本来就有（切在空格之前）。凭空补空格会污染输出，
    /// 也会让"分段结果拼回去等于原文"这条不变量不成立。
    /// </remarks>
    public static string Join(IReadOnlyList<TextSegment> segments, IReadOnlyList<string> translations)
    {
        if (segments.Count != translations.Count)
        {
            throw new ArgumentException("分段数与译文数不一致", nameof(translations));
        }

        var sb = new System.Text.StringBuilder();

        for (var i = 0; i < segments.Count; i++)
        {
            sb.Append(translations[i].Trim());

            if (i + 1 < segments.Count)
            {
                sb.Append(segments[i].Separator);
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// 在 <c>[start, limit)</c> 窗口内找一个尽量靠后的语义切点。
    /// </summary>
    /// <returns>切点位置（不含）。切在标点之后时含该标点，切在换行之前时不含换行。</returns>
    private static int FindCut(string text, int start, int limit)
    {
        var span = limit - start;

        // 1) 换行（含空行）。把整个空白串留给 Separator —— 切点取"空白串的第一个字符"，
        //    这样 "\n\n"、"\r\n\r\n"、"   \n\n " 都能原样进入 Separator，
        //    段落结构在拼接时不会塌成单个换行。
        var index = text.LastIndexOf('\n', limit - 1, span);
        if (index <= start)
        {
            index = text.LastIndexOf('\r', limit - 1, span);
        }

        if (index > start)
        {
            var runStart = index;
            while (runStart > start && char.IsWhiteSpace(text[runStart - 1]))
            {
                runStart--;
            }

            return runStart;
        }

        // 2) 句末标点：含标点本身。其后若有空白，由 DetectSeparator 收进 Separator。
        index = LastIndexOfAny(text, SentenceEnders, start, limit);
        if (index > start)
        {
            return index + 1;
        }

        // 3) 分句标点
        index = LastIndexOfAny(text, ClausePunctuation, start, limit);
        if (index > start)
        {
            return index + 1;
        }

        // 4) 空格（英文长段落）。切在空格<b>之前</b>：切在之后的话，
        //    这个空格会落进本段末尾，随后被 Trim 掉 —— 拼接时就丢了一个空格。
        index = text.LastIndexOf(' ', limit - 1, span);
        if (index > start)
        {
            return index;
        }

        // 5) 硬切。不能劈开代理对（emoji 等由两个 char 组成），
        //    否则两段各拿到半个字符，都会变成替换符。
        var cut = limit;
        if (cut > start + 1 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return cut;
    }

    /// <summary>取窗口内 <paramref name="candidates"/> 中任一字符的最后一次出现位置。</summary>
    private static int LastIndexOfAny(string text, string candidates, int start, int limit)
    {
        var best = -1;

        foreach (var ch in candidates)
        {
            var index = text.LastIndexOf(ch, limit - 1, limit - start);
            if (index > best)
            {
                best = index;
            }
        }

        return best;
    }

    /// <summary>
    /// 取出切点之后的空白串，<b>原样</b>作为分隔符。
    /// </summary>
    /// <remarks>
    /// 不做归一化（曾经把 <c>"\r\n\r\n"</c> 收敛成 <c>"\n\n"</c>、把多空格收敛成单空格）：
    /// 归一化会让"分段拼回去等于原文"这条不变量不成立，也就无法用测试证明没丢字。
    /// 分隔符只出现在段与段之间，原样保留没有任何副作用。
    /// </remarks>
    private static string DetectSeparator(string text, int cut)
    {
        var end = cut;
        while (end < text.Length && char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return end == cut ? string.Empty : text[cut..end];
    }

    /// <summary>
    /// 尾段过小时并入上一段。
    /// </summary>
    /// <remarks>
    /// 否则「刚超出上限一点点」的文本会为几个字符多发一次完整请求——
    /// 延迟翻倍，收益为零。
    /// </remarks>
    private static void MergeTinyTail(List<TextSegment> segments, int maxChars)
    {
        if (segments.Count < 2)
        {
            return;
        }

        var last = segments[^1];
        if (last.Text.Length >= maxChars * MergeTailRatio)
        {
            return;
        }

        var previous = segments[^2];
        if (previous.Text.Length + last.Text.Length > maxChars * MergeStretchRatio)
        {
            return;
        }

        segments[^2] = new TextSegment(
            previous.Text + previous.Separator + last.Text,
            last.Separator);

        segments.RemoveAt(segments.Count - 1);
    }
}
