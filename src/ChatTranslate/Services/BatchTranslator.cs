using System.Text;
using System.Text.RegularExpressions;
using ChatTranslate.Core;

namespace ChatTranslate.Services;

/// <summary>
/// 多行批量翻译：<b>一次请求翻译多行</b>，并保持行号映射。
/// </summary>
/// <remarks>
/// <para><b>为什么要批量</b>：截图 OCR 常给出十几行文本。逐行发请求意味着十几次模型往返，
/// 既慢又浪费——而译文必须能对应回原来的每一行位置，否则替换渲染无法排版。</para>
///
/// <para><b>用编号协议保证映射</b>：把每行写成 <c>序号|原文</c>，要求模型按同样格式返回。
/// 这样即使模型调整了措辞，行与行的对应关系也不会丢。</para>
///
/// <para><b>解析失败必须能退化</b>：模型不一定守格式。协议解析不出来时退回逐行翻译，
/// 宁可慢，也不能把错位的译文画到图上（错位比不翻译更糟）。</para>
/// </remarks>
public static partial class BatchTranslator
{
    /// <summary>一次请求里最多包含多少行。行数过多时模型容易漏行或合并行。</summary>
    private const int MaxLinesPerRequest = 20;

    /// <summary>一次请求里最多包含多少字符，防止超出上下文窗口。</summary>
    private const int MaxCharsPerRequest = 1200;

    /// <summary>
    /// 按行翻译，返回与输入等长的译文数组（顺序一一对应）。
    /// </summary>
    public static async Task<IReadOnlyList<string>> TranslateLinesAsync(
        OllamaClient client,
        IReadOnlyList<string> lines,
        Language target,
        Language? source = null,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var results = new string[lines.Count];

        // 空行不翻译，原样保留（保留空行是为了让行号映射不被打乱）
        var pending = new List<int>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                results[i] = lines[i];
            }
            else
            {
                pending.Add(i);
            }
        }

        if (pending.Count == 0)
        {
            return results;
        }

        var done = 0;

        foreach (var chunk in Chunk(pending, lines))
        {
            ct.ThrowIfCancellationRequested();

            if (chunk.Count == 1)
            {
                // 单行没必要用编号协议，直接翻（编号有时会被模型当成正文）
                var index = chunk[0];
                results[index] = await TranslateSingleAsync(
                    client, lines[index], target, source, ct).ConfigureAwait(false);
            }
            else
            {
                var translated = await TranslateChunkAsync(
                    client, chunk, lines, target, source, ct).ConfigureAwait(false);

                for (var k = 0; k < chunk.Count; k++)
                {
                    results[chunk[k]] = translated[k];
                }
            }

            done += chunk.Count;
            progress?.Report((done, pending.Count));
        }

        return results;
    }

    /// <summary>按行数与字符数把待翻译行切成若干批。</summary>
    private static IEnumerable<List<int>> Chunk(IReadOnlyList<int> pending, IReadOnlyList<string> lines)
    {
        var current = new List<int>();
        var chars = 0;

        foreach (var index in pending)
        {
            var length = lines[index].Length;

            var wouldOverflow = current.Count >= MaxLinesPerRequest
                                || (current.Count > 0 && chars + length > MaxCharsPerRequest);

            if (wouldOverflow)
            {
                yield return current;
                current = [];
                chars = 0;
            }

            current.Add(index);
            chars += length;
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }

    /// <summary>整批翻译一次；编号协议解析失败时退回逐行翻译。</summary>
    private static async Task<List<string>> TranslateChunkAsync(
        OllamaClient client,
        List<int> chunk,
        IReadOnlyList<string> lines,
        Language target,
        Language? source,
        CancellationToken ct)
    {
        var prompt = BuildBatchPrompt(chunk, lines, target, source);
        var batch = await client.CompleteAsync([ChatMessage.User(prompt)], null, null, ct)
            .ConfigureAwait(false);

        var parse = ParseIndexedReply(batch.Text);

        // 成功条件必须比"序号都在"更严，否则两类模型不合规都会被误判成功、
        // 不触发退化，而类注释承诺的是"宁可退化也不出错位/残缺"：
        //
        //  1) 重复序号 —— 后写覆盖先写，静默丢一条译文；
        //  2) 某行译文被折成多个物理行 —— 续行不匹配编号格式而被丢弃，
        //     译文被静默截断。
        //
        // 因此要求：无重复序号、无未解析的行、且取到的条数与期望一致。
        var ok = !parse.HasDuplicate
                 && parse.UnparsedLines == 0
                 && parse.Map.Count == chunk.Count
                 && chunk.All(index => parse.Map.ContainsKey(index + 1));

        if (ok)
        {
            return chunk.Select(index => parse.Map[index + 1]).ToList();
        }

        AppLog.Warn($"批量翻译的编号协议未严格通过（期望 {chunk.Count} 行，取到 {parse.Map.Count} 行，"
                    + $"重复序号={parse.HasDuplicate}，未解析行={parse.UnparsedLines}），退回逐行翻译");

        var fallback = new List<string>(chunk.Count);
        foreach (var index in chunk)
        {
            ct.ThrowIfCancellationRequested();
            fallback.Add(await TranslateSingleAsync(client, lines[index], target, source, ct)
                .ConfigureAwait(false));
        }

        return fallback;
    }

    private static string BuildBatchPrompt(
        List<int> chunk, IReadOnlyList<string> lines, Language target, Language? source)
    {
        var sb = new StringBuilder();
        sb.Append("下面是若干行文本，每行以「序号|」开头。请把每行「|」之后的内容分别翻译为")
          .Append(target.ChineseName);

        if (source is not null)
        {
            sb.Append("（原文为").Append(source.ChineseName).Append('）');
        }

        sb.Append("。\n要求：保持行数与顺序不变，输出格式同样是「序号|译文」，"
                + "不要合并或拆分任何一行，不要输出任何解释。\n\n");

        foreach (var index in chunk)
        {
            sb.Append(index + 1).Append('|').Append(lines[index]).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>编号协议的解析结果。</summary>
    /// <param name="Map">序号 → 译文。</param>
    /// <param name="HasDuplicate">是否出现重复序号（后写覆盖先写 = 静默丢译文）。</param>
    /// <param name="UnparsedLines">未能按编号格式解析的非空行数（通常是译文折行的续行）。</param>
    private readonly record struct ParsedBatch(
        Dictionary<int, string> Map, bool HasDuplicate, int UnparsedLines);

    /// <summary>
    /// 解析「序号|译文」格式。识别中英文竖线与冒号，容忍前导空白。
    /// </summary>
    /// <remarks>
    /// 除了取出映射，还要上报两类"不合规"信号：重复序号与未解析行。
    /// 只看"序号在不在"会把它们当成成功，从而把错位/残缺的译文画到图上。
    /// </remarks>
    private static ParsedBatch ParseIndexedReply(string reply)
    {
        var map = new Dictionary<int, string>();
        var hasDuplicate = false;
        var unparsed = 0;

        foreach (var raw in reply.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var m = IndexedLine().Match(line);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var index))
            {
                // 解析不出来的非空行：可能是模型加的解释，也可能是上一行译文的续行
                unparsed++;
                continue;
            }

            var text = m.Groups[2].Value.Trim();
            if (text.Length == 0)
            {
                unparsed++;
                continue;
            }

            if (!map.TryAdd(index, text))
            {
                hasDuplicate = true;
            }
        }

        return new ParsedBatch(map, hasDuplicate, unparsed);
    }

    /// <summary>
    /// 单行翻译（单行分片与退化路径都走这里）。
    /// </summary>
    /// <remarks>
    /// 空译文<b>不抛异常</b>，而是记警告后返回空串——与 <c>TranslationService</c> 的
    /// "抛异常"策略不同，理由：本方法用在多行批量的退化路径上，一行抛异常会让整批
    /// （乃至整次 OCR 翻译）失败，把其余已成功的行一起丢掉，那是比"少一行"更糟的结果。
    /// 这里保证的是<b>不静默</b>：日志留痕，且上层在"全部行都为空"时会明确报错。
    /// </remarks>
    private static async Task<string> TranslateSingleAsync(
        OllamaClient client, string text, Language target, Language? source, CancellationToken ct)
    {
        var prompt = OllamaClient.BuildTranslatePrompt(text, target, source);
        var reply = await client.CompleteAsync([ChatMessage.User(prompt)], null, null, ct)
            .ConfigureAwait(false);

        var translation = reply.Text;

        if (translation.Length == 0)
        {
            AppLog.Warn($"单行翻译返回空译文（原文 {text.Length} 字符），该行将留空");
        }

        return translation;
    }

    // 形如 "12|译文"、"12｜译文"、"12: 译文"、"12. 译文"
    [GeneratedRegex(@"^(\d+)\s*[|｜:：.、]\s*(.*)$")]
    private static partial Regex IndexedLine();
}
