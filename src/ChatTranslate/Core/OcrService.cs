using System.Windows;
using Windows.Graphics.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Storage;

namespace ChatTranslate.Core;

/// <summary>识别出的单词（含包围盒，供译文替换排版使用）。</summary>
public sealed record OcrWord(string Text, Rect BoundingBox);

/// <summary>识别出的一行文本。</summary>
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words, Rect BoundingBox);

/// <summary>OCR 结果（纯 CLR 类型，避免把 WinRT 对象带出到其他线程）。</summary>
public sealed record OcrOutput(string Text, IReadOnlyList<OcrLine> Lines);

/// <summary>
/// 基于 Windows.Media.Ocr 的离线文字识别。
/// 与 pot-desktop 的做法一致（System OCR），但是同进程调用，无子进程开销。
/// </summary>
public static class OcrService
{
    /// <summary>系统已安装的 OCR 语言包标签，例如 zh-Hans-CN。</summary>
    public static IReadOnlyList<string> AvailableLanguages =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    /// <summary>单张图片的最大边长限制（超过需先缩放，否则 OcrEngine 会拒绝）。</summary>
    public static uint MaxImageDimension => OcrEngine.MaxImageDimension;

    /// <summary>
    /// 识别指定图片文件。
    /// </summary>
    /// <param name="filePath">图片绝对路径（PNG / JPG 等）。</param>
    /// <param name="languageTag">语言标签；传 null 或空则使用系统当前语言。</param>
    public static async Task<OcrOutput> RecognizeFileAsync(string filePath, string? languageTag = null)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        return await RecognizeStreamAsync(stream, languageTag);
    }

    private static async Task<OcrOutput> RecognizeStreamAsync(
        Windows.Storage.Streams.IRandomAccessStream stream,
        string? languageTag)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync();

        var engine = CreateEngine(languageTag);
        var result = await engine.RecognizeAsync(bitmap);

        var lines = new List<OcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var words = new List<OcrWord>(line.Words.Count);
            foreach (var w in line.Words)
            {
                words.Add(new OcrWord(
                    w.Text,
                    new Rect(w.BoundingRect.X, w.BoundingRect.Y,
                             w.BoundingRect.Width, w.BoundingRect.Height)));
            }

            var box = UnionOf(words);
            lines.Add(new OcrLine(line.Text, words, box));
        }

        return new OcrOutput(result.Text, lines);
    }

    private static OcrEngine CreateEngine(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? throw new InvalidOperationException(
                    "系统没有可用的 OCR 语言包，请在 Windows 设置中安装语言包。");
        }

        var engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
        if (engine is not null)
        {
            return engine;
        }

        // 语言标签可能写作 zh-CN / en-US，而系统注册的是 zh-Hans-CN / en-US，做一次模糊匹配
        var normalized = languageTag.Replace('_', '-');
        var match = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(
            l => l.LanguageTag.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
              || normalized.StartsWith(l.LanguageTag, StringComparison.OrdinalIgnoreCase));

        if (match is not null && OcrEngine.TryCreateFromLanguage(match) is { } fallback)
        {
            return fallback;
        }

        throw new InvalidOperationException(
            $"系统未安装语言包：{languageTag}。已安装：{string.Join(", ", AvailableLanguages)}");
    }

    private static Rect UnionOf(IReadOnlyList<OcrWord> words)
    {
        if (words.Count == 0)
        {
            return Rect.Empty;
        }

        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var w in words)
        {
            l = Math.Min(l, w.BoundingBox.Left);
            t = Math.Min(t, w.BoundingBox.Top);
            r = Math.Max(r, w.BoundingBox.Right);
            b = Math.Max(b, w.BoundingBox.Bottom);
        }

        return new Rect(l, t, r - l, b - t);
    }
}
