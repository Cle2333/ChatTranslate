using System.Collections.ObjectModel;
using System.Windows;
using ChatTranslate.Core;
using ChatTranslate.Data;
using Wpf.Ui.Controls;

namespace ChatTranslate.Views;

/// <summary>
/// 「已归档的对话」窗口：查看归档的会话，就地恢复或删除。
/// </summary>
/// <remarks>
/// 复用主窗口那份 <see cref="ChatStore"/>（同一个连接、同一把锁），不另开连接：
/// 同一个进程操作同一份数据，多开连接只会让写入互相阻塞。
/// </remarks>
public partial class ArchiveWindow : FluentWindow
{
    private readonly ChatStore _store;
    private readonly ObservableCollection<ThreadViewModel> _items = [];

    /// <summary>
    /// 窗口内是否发生过恢复 / 删除。主窗口据此判断关闭后要不要刷新侧边栏。
    /// </summary>
    /// <remarks>
    /// 归档窗口的列表里**不可能出现主窗口当前正在看的会话**（归档时就已从它那里切走了），
    /// 所以这里不存在「删掉自己正在用的对话」这种情况，不必回传当前会话的有效性。
    /// </remarks>
    public bool Changed { get; private set; }

    public ArchiveWindow(ChatStore store)
    {
        _store = store;
        InitializeComponent();

        ArchivedList.ItemsSource = _items;

        // 用 Loaded 而不是构造函数：构造函数里还拿不到窗口尺寸，
        // 而这里要读库、算提示文案，放在窗口真正显示时更直观（也为将来加"打开时定位"留位置）。
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        _items.Clear();
        foreach (var thread in _store.ListArchivedThreads())
        {
            _items.Add(ThreadViewModel.From(thread));
        }

        var count = _items.Count;
        CountHint.Text = count > 0 ? $"共 {count} 个已归档对话" : string.Empty;
        EmptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (Resolve(sender) is not { } target)
        {
            return;
        }

        // 返回 false 说明会话已经不在了（理论上不会：列表刚刷新过），
        // 此时仍然刷新一次列表，把界面拉回与库一致的状态。
        if (_store.SetArchived(target.Id, archived: false))
        {
            Changed = true;
            AppLog.Info($"恢复已归档对话：id={target.Id}「{target.Title}」");
        }

        Reload();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Resolve(sender) is not { } target || !ThreadDialogs.ConfirmDelete(this, target))
        {
            return;
        }

        _store.DeleteThread(target.Id);
        Changed = true;
        AppLog.Info($"删除已归档对话：id={target.Id}「{target.Title}」");
        Reload();
    }

    /// <summary>按钮的 DataContext 就是该行的会话（按钮在列表项的可视树内，继承正常）。</summary>
    private static ThreadViewModel? Resolve(object sender) =>
        (sender as FrameworkElement)?.DataContext as ThreadViewModel;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
