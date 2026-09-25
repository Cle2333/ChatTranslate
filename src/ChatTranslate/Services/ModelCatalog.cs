namespace ChatTranslate.Services;

/// <summary>
/// 已安装模型清单。
/// </summary>
/// <remarks>
/// <para>主界面与设置页都要填这个下拉，两处的规则必须一致，因此抽到一处：</para>
///
/// <list type="bullet">
/// <item><b>失败不抛、也不清空</b>：Ollama 没启动、超时、响应格式异常时返回的清单只含
/// 当前配置的那个模型。调用方据此保留界面上的当前值——
/// 一次探测失败就把用户配好的模型从下拉里抹掉，是最难解释的那种"设置不见了"。</item>
///
/// <item><b>配置里的模型必然在清单首位</b>：它可能刚被 <c>ollama rm</c> 掉、
/// 或是手工 <c>ollama create</c> 后才有的名字。不在清单里的话，
/// <c>ComboBox</c> 绑定不上会被清空，界面显示空白、看起来像设置坏了。</item>
/// </list>
///
/// <para>每次调用自建一个短命客户端：这个操作是用户点开下拉 / 点刷新时才触发的低频动作，
/// 不值得为它维护客户端缓存；而复用 <see cref="TranslationService"/> 里按配置指纹缓存的客户端
/// 反而会出问题——在设置页里改了地址再点刷新，拿到的仍是旧地址的清单。</para>
/// </remarks>
public static class ModelCatalog
{
    /// <summary>
    /// 取已安装模型名清单（失败时至少包含 <paramref name="configuredModel"/>）。
    /// </summary>
    /// <param name="host">Ollama 地址。</param>
    /// <param name="configuredModel">当前配置的模型名；会保证出现在结果里。</param>
    /// <param name="numCtx">构造客户端需要，与探测无关。</param>
    /// <param name="keepAlive">构造客户端需要，与探测无关。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<IReadOnlyList<string>> ListAsync(
        string host,
        string configuredModel,
        int numCtx,
        string keepAlive,
        CancellationToken ct = default)
    {
        var models = new List<string>();

        try
        {
            using var client = new OllamaClient(host, configuredModel, numCtx, keepAlive);
            var installed = await client.ListModelsAsync(ct);

            if (installed is not null)
            {
                // 去重（用不区分大小写的比较，避免 "Hy-MT2:7B" 与 "hy-mt2:7b" 同时出现）
                foreach (var name in installed)
                {
                    if (!string.IsNullOrWhiteSpace(name)
                        && !models.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        models.Add(name);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 调用方取消（例如窗口已关闭）：不给结果，也不当成失败处理
            throw;
        }
        catch
        {
            // 其余失败一律静默：清单为空由下面的兜底补上配置值
        }

        if (!string.IsNullOrWhiteSpace(configuredModel)
            && !models.Contains(configuredModel, StringComparer.OrdinalIgnoreCase))
        {
            models.Insert(0, configuredModel);
        }

        return models;
    }
}
