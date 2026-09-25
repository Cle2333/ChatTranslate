using System.ComponentModel;
using ChatTranslate.Services;

namespace ChatTranslate.Views;

/// <summary>「可选语种」里的一个勾选项。</summary>
public sealed class LanguageChip : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _canToggle;

    public LanguageChip(Language language, bool isSelected, bool canToggle)
    {
        Language = language;
        _isSelected = isSelected;
        _canToggle = canToggle;
    }

    public Language Language { get; }

    public string Code => Language.Code;

    public string Name => Language.ChineseName;

    /// <summary>
    /// 是否允许取消勾选。
    /// </summary>
    /// <remarks>
    /// 必须可写：用户在窗口开着的时候可能改了输入/目标语言，
    /// 对应的语种就应立即变成"必须保留"，而不是停留在打开窗口那一刻的快照。
    /// 不变的话，用户取消勾选后保存，配置校验又会把它静默加回来——
    /// 用户会以为设置没生效。
    /// </remarks>
    public bool CanToggle
    {
        get => _canToggle;
        set
        {
            if (_canToggle == value)
            {
                return;
            }

            _canToggle = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggle)));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>
    /// 复制一份。
    /// </summary>
    /// <remarks>
    /// 语种选择器是模态窗口，点「取消」必须完全不影响调用方的状态，
    /// 所以传进去的是一份拷贝，只有点「确定」才把结果回写给调用方。
    /// </remarks>
    public LanguageChip Clone() => new(Language, _isSelected, _canToggle);

    public event PropertyChangedEventHandler? PropertyChanged;
}
