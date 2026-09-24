namespace ChatTranslate.Services;

/// <summary>Hy-MT2 支持的语言。官方要求用**全称**，中文 prompt 配中文名。</summary>
public sealed record Language(string Code, string ChineseName, string EnglishName)
{
    public override string ToString() => ChineseName;
}

/// <summary>Hy-MT2 语种表（来自官方 README）。</summary>
public static class Languages
{
    public static readonly IReadOnlyList<Language> All =
    [
        new("zh", "中文", "Chinese"),
        new("en", "英语", "English"),
        new("ja", "日语", "Japanese"),
        new("ko", "韩语", "Korean"),
        new("fr", "法语", "French"),
        new("de", "德语", "German"),
        new("es", "西班牙语", "Spanish"),
        new("pt", "葡萄牙语", "Portuguese"),
        new("ru", "俄语", "Russian"),
        new("it", "意大利语", "Italian"),
        new("ar", "阿拉伯语", "Arabic"),
        new("th", "泰语", "Thai"),
        new("vi", "越南语", "Vietnamese"),
        new("ms", "马来语", "Malay"),
        new("id", "印尼语", "Indonesian"),
        new("tl", "菲律宾语", "Filipino"),
        new("hi", "印地语", "Hindi"),
        new("zh-Hant", "繁体中文", "Traditional Chinese"),
        new("pl", "波兰语", "Polish"),
        new("cs", "捷克语", "Czech"),
        new("nl", "荷兰语", "Dutch"),
        new("km", "高棉语", "Khmer"),
        new("my", "缅甸语", "Burmese"),
        new("fa", "波斯语", "Persian"),
        new("gu", "古吉拉特语", "Gujarati"),
        new("ur", "乌尔都语", "Urdu"),
        new("te", "泰卢固语", "Telugu"),
        new("mr", "马拉地语", "Marathi"),
        new("he", "希伯来语", "Hebrew"),
        new("bn", "孟加拉语", "Bengali"),
        new("ta", "泰米尔语", "Tamil"),
        new("uk", "乌克兰语", "Ukrainian"),
        new("bo", "藏语", "Tibetan"),
        new("kk", "哈萨克语", "Kazakh"),
        new("mn", "蒙古语", "Mongolian"),
        new("ug", "维吾尔语", "Uyghur"),
        new("yue", "粤语", "Cantonese"),
    ];

    /// <summary>按代码查语言；找不到返回 null。</summary>
    public static Language? ByCode(string code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>按中文名查语言；找不到返回 null。</summary>
    public static Language? ByChineseName(string name) =>
        All.FirstOrDefault(l => string.Equals(l.ChineseName, name, StringComparison.Ordinal));

    /// <summary>默认目标语言：中文。按代码显式取，不依赖语种表的排列顺序。</summary>
    public static Language Default => ByCode("zh")!;

    /// <summary>
    /// 粗判文本语言，只区分中文与英文，无法判断时返回 null。
    ///
    /// <para>用途是「原文与目标语言相同时自动切换到另一种」。
    /// 只做中英二元判断：绝大多数场景（中文用户翻译中英内容）够用，
    /// 且不需要引入语言检测库。</para>
    /// </summary>
    /// <remarks>
    /// <b>分母只统计「有效字符」</b>——汉字与拉丁字母，以及无法归类但确实是文字的字符会稀释比例。
    /// 若把 emoji、符号也算进分母，像 <c>好的👍</c> 这种文本（emoji 占两个 UTF-16 码元）
    /// 会让汉字比例跌破一半、被判为 null，于是本该触发的换向不触发，
    /// 目标语言为中文时就把中文原样再"翻"一遍——恰好是本方法要避免的空转。
    /// </remarks>
    public static string? Detect(string text)
    {
        var cjk = 0;
        var latin = 0;
        var otherLetters = 0;

        foreach (var ch in text)
        {
            // 空白、标点、数字、符号一律不参与判断（emoji 也落在这里）
            if (!IsLetterLike(ch))
            {
                continue;
            }

            if (ch is >= '\u4E00' and <= '\u9FFF')
            {
                cjk++;
            }
            else if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
            {
                latin++;
            }
            else
            {
                // 带重音的拉丁字母、假名、谚文、西里尔文等：算作"其他文字"，
                // 中性计入总分，但不捧高任何一方的比例
                otherLetters++;
            }
        }

        var total = cjk + latin + otherLetters;
        if (total == 0)
        {
            return null;
        }

        // 过半才算，避免个别汉字/单词影响判断
        if (cjk * 2 > total)
        {
            return "zh";
        }

        return latin * 2 > total ? "en" : null;
    }

    /// <summary>是否为"文字类"字符（用于语言判断，排除标点、符号、emoji、数字）。</summary>
    private static bool IsLetterLike(char ch) =>
        char.IsLetter(ch);
}
