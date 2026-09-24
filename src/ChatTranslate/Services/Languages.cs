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

    /// <summary>默认目标语言：中文。</summary>
    public static Language Default => All[0];
}
