using System.Windows;

namespace ChatTranslate.Views;

/// <summary>
/// 会话的归档 / 删除操作共用的对话框。
/// </summary>
/// <remarks>
/// 主窗口与「已归档的对话」窗口都要用同一套文案：各写一份字面量迟早会漂移，
/// 而这里的措辞直接关系到用户是否意识到「删除不可撤销」。
/// </remarks>
internal static class ThreadDialogs
{
    /// <summary>
    /// 删除会话前的二次确认。
    /// </summary>
    /// <returns>用户是否确认删除。</returns>
    public static bool ConfirmDelete(Window owner, ThreadViewModel thread)
    {
        var detail = thread.MessageCount > 0
            ? $"其中 {thread.MessageCount} 条消息与截图文件会一并清理。"
            : "这是一个还没有消息的空对话。";

        return ConfirmDialog.Confirm(
            owner,
            $"删除对话「{thread.Title}」？",
            $"{detail}\n\n删除后无法撤销。",
            "删除",
            danger: true);
    }

    /// <summary>
    /// 提示翻译正在进行，此刻不能改动会话。
    /// </summary>
    /// <remarks>
    /// 翻译过程会往会话里写消息（先落用户消息，流式结束后落译文）。此时删掉会话的话，
    /// 第二次写入会撞上外键约束直接抛异常；归档则会让这次翻译的结果写进一个
    /// 用户已经收起来的对话里，表现为「消息凭空消失」。
    /// </remarks>
    public static void ShowBusy(Window owner) =>
        ConfirmDialog.Inform(
            owner,
            "翻译进行中",
            "这次翻译结束之后才能归档或删除它 —— 译文还要写进这个对话里。");
}
