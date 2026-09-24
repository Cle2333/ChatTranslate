using System.ComponentModel;
using System.Runtime.CompilerServices;
using ChatTranslate.Data;

namespace ChatTranslate.Views;

/// <summary>侧边栏的历史条目。</summary>
public sealed class ThreadViewModel
{
    public required long Id { get; init; }
    public required string Title { get; init; }
    public required string TimeText { get; init; }
    public required int MessageCount { get; init; }

    public static ThreadViewModel From(ChatThread thread) => new()
    {
        Id = thread.Id,
        Title = thread.Title,
        TimeText = thread.TimeText,
        MessageCount = thread.MessageCount,
    };
}

/// <summary>
/// 对话气泡。
///
/// <para>同一条消息要么是纯文本，要么是带图（截图 OCR 的用户侧消息展示截图本身）。</para>
/// <para>实现 <see cref="INotifyPropertyChanged"/> 是为了支撑流式输出：
/// 边生成边刷新同一条气泡的文本。</para>
/// </summary>
public sealed class BubbleViewModel : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private bool _isStreaming;
    private string _streamingLabel = StreamingIdleLabel;

    /// <summary>流式指示的默认文案。</summary>
    public const string StreamingIdleLabel = "生成中";

    public required bool IsUser { get; init; }

    public required string TimeText { get; init; }

    public string? ImagePath { get; init; }

    public bool HasImage => !string.IsNullOrEmpty(ImagePath);

    /// <summary>气泡文本。流式输出期间会被反复改写。</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }

            _text = value;
            OnPropertyChanged();
        }
    }

    /// <summary>是否正在生成中。</summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (_isStreaming == value)
            {
                return;
            }

            _isStreaming = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 流式指示的文案。
    /// </summary>
    /// <remarks>
    /// 模型不在显存里时，本次翻译要先等 Ollama 把它载入（实测 7.05 s），
    /// 这段时间气泡里一个字都不会出现。只显示「生成中」的话用户会以为程序卡死，
    /// 所以要能在等待期间换成「模型加载中…」，等首块内容到达再切回来。
    /// </remarks>
    public string StreamingLabel
    {
        get => _streamingLabel;
        set
        {
            if (_streamingLabel == value)
            {
                return;
            }

            _streamingLabel = value;
            OnPropertyChanged();
        }
    }

    public static BubbleViewModel From(ChatMessageEntity message) => new()
    {
        Text = message.Text,
        IsUser = message.IsUser,
        TimeText = message.TimeText,
        ImagePath = message.ImagePath,
    };

    /// <summary>新建一条待填充的助手气泡（流式输出用）。</summary>
    public static BubbleViewModel Streaming() => new()
    {
        Text = string.Empty,
        IsUser = false,
        TimeText = DateTime.Now.ToString("HH:mm"),
        IsStreaming = true,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
