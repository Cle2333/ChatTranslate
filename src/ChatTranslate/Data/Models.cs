namespace ChatTranslate.Data;

/// <summary>一条会话（一个"对话串"）。</summary>
public sealed class ChatThread
{
    public long Id { get; init; }

    /// <summary>标题，取自首条用户消息的前若干字符。</summary>
    public string Title { get; set; } = "新对话";

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>消息条数（仅列表展示用，由查询带出）。</summary>
    public int MessageCount { get; set; }

    /// <summary>侧边栏展示的相对时间。</summary>
    public string TimeText => FormatRelative(UpdatedAt);

    internal static string FormatRelative(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var delta = DateTime.Now - local;

        if (delta.TotalSeconds < 60)
        {
            return "刚刚";
        }

        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        if (local.Date == DateTime.Today)
        {
            return $"今天 {local:HH:mm}";
        }

        if (local.Date == DateTime.Today.AddDays(-1))
        {
            return $"昨天 {local:HH:mm}";
        }

        return local.Year == DateTime.Now.Year
            ? local.ToString("MM-dd HH:mm")
            : local.ToString("yyyy-MM-dd");
    }
}

/// <summary>一条对话消息。</summary>
public sealed class ChatMessageEntity
{
    public long Id { get; init; }

    public long ThreadId { get; init; }

    /// <summary>true = 用户侧，false = 翻译结果侧。</summary>
    public bool IsUser { get; init; }

    /// <summary>
    /// 文本内容。
    /// 用户侧 = 原文；结果侧 = 译文。
    /// 图片消息（截图 OCR）时，这里存**识别出的原文**，用于构造对话上下文。
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// 截图图片的绝对路径（仅截图 OCR 的用户消息有值）。
    /// 界面据此直接展示图片，而不是展示识别出的文字。
    /// </summary>
    public string? ImagePath { get; init; }

    public DateTime CreatedAt { get; init; }

    /// <summary>是否带图。</summary>
    public bool HasImage => !string.IsNullOrEmpty(ImagePath);

    /// <summary>气泡上展示的时间。</summary>
    public string TimeText => CreatedAt.ToLocalTime().ToString("HH:mm");

    /// <summary>构造对话上下文时用的角色。</summary>
    public string Role => IsUser ? "user" : "assistant";
}
