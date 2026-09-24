using System.IO;
using Microsoft.Data.Sqlite;

namespace ChatTranslate.Data;

/// <summary>
/// 会话存储（SQLite）。
///
/// <para>一个连接贯穿进程生命周期：桌面单用户场景下没有并发压力，
/// 而 SQLite 的连接建立本身有开销。所有公开方法都在锁内访问连接。</para>
/// </summary>
public sealed class ChatStore : IDisposable
{
    /// <summary>库结构版本。数据库里会记录，用于将来做迁移判断。</summary>
    private const int SchemaVersion = 1;

    /// <summary>新会话的默认标题。建表默认值、模型初始值、标题更新条件共用此常量。</summary>
    public const string DefaultTitle = "新对话";

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public ChatStore(string? databasePath = null)
    {
        var path = databasePath ?? AppPaths.DatabaseFile;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 不启用 Shared cache：它与 WAL 存在兼容限制，可能让 journal_mode=WAL 静默失效。
        // 单用户单连接场景下 shared cache 也没有意义。
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        try
        {
            _connection.Open();
            Initialize();
        }
        catch
        {
            // 建表/PRAGMA 失败时构造函数整体抛出，若不在此释放，
            // 连接池里的底层文件句柄会一直保留到进程退出。
            _connection.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        // 校验 WAL 是否真的生效：这个 PRAGMA 会返回实际生效的日志模式，
        // 用 ExecuteNonQuery 会把返回值丢掉、失败也无从察觉，进而影响崩溃恢复行为。
        var mode = ExecuteScalar("PRAGMA journal_mode=WAL;") as string;
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            System.Diagnostics.Debug.WriteLine($"警告：WAL 未生效，当前日志模式为 {mode ?? "(未知)"}");
        }

        Execute("PRAGMA foreign_keys=ON;");

        Execute("""
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL
            );
            """);

        Execute($"""
            CREATE TABLE IF NOT EXISTS threads (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                title       TEXT    NOT NULL DEFAULT '{DefaultTitle}',
                created_at  TEXT    NOT NULL,
                updated_at  TEXT    NOT NULL
            );
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS messages (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                thread_id   INTEGER NOT NULL,
                is_user     INTEGER NOT NULL,
                text        TEXT    NOT NULL DEFAULT '',
                image_path  TEXT    NULL,
                created_at  TEXT    NOT NULL,
                FOREIGN KEY (thread_id) REFERENCES threads(id) ON DELETE CASCADE
            );
            """);

        Execute("CREATE INDEX IF NOT EXISTS idx_messages_thread ON messages(thread_id, id);");
        Execute("CREATE INDEX IF NOT EXISTS idx_threads_updated ON threads(updated_at DESC);");

        var existing = Convert.ToInt64(ExecuteScalar("SELECT COALESCE(MAX(version), 0) FROM schema_info;") ?? 0L);

        if (existing == 0)
        {
            using var insert = _connection.CreateCommand();
            insert.CommandText = "INSERT INTO schema_info(version) VALUES ($v);";
            insert.Parameters.AddWithValue("$v", SchemaVersion);
            insert.ExecuteNonQuery();
        }
        else if (existing != SchemaVersion)
        {
            // 目前只有 v1，没有迁移路径。显式报出来，避免将来加了新版本却以为已自动处理。
            System.Diagnostics.Debug.WriteLine(
                $"数据库结构版本为 {existing}，程序期望 {SchemaVersion}：需要迁移但尚未实现。");
        }
    }

    // ---------------------------------------------------------------- 会话

    /// <summary>列出全部会话，按最近更新排序。</summary>
    public List<ChatThread> ListThreads()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT t.id, t.title, t.created_at, t.updated_at,
                       (SELECT COUNT(*) FROM messages m WHERE m.thread_id = t.id)
                FROM threads t
                ORDER BY t.updated_at DESC;
                """;

            var result = new List<ChatThread>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ChatThread
                {
                    Id = reader.GetInt64(0),
                    Title = reader.GetString(1),
                    CreatedAt = ParseTime(reader.GetString(2)),
                    UpdatedAt = ParseTime(reader.GetString(3)),
                    MessageCount = (int)reader.GetInt64(4),
                });
            }

            return result;
        }
    }

    /// <summary>新建会话，返回其 Id。</summary>
    public long CreateThread(string title = DefaultTitle)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            var now = DateTime.UtcNow.ToString("O");
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO threads(title, created_at, updated_at) VALUES ($t, $c, $u);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$t", title);
            command.Parameters.AddWithValue("$c", now);
            command.Parameters.AddWithValue("$u", now);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>
    /// 删除会话及其全部消息，并清理该会话的截图文件。
    /// </summary>
    /// <remarks>
    /// 外键级联只清数据库行；截图 PNG 存在文件系统上，不清理的话
    /// 反复删除带截图的会话会让 images 目录无限增长。
    /// </remarks>
    public void DeleteThread(long threadId)
    {
        List<string> imagePaths;

        lock (_gate)
        {
            ThrowIfDisposed();

            // 先取出图片路径，删除行之后就查不到了
            using (var query = _connection.CreateCommand())
            {
                query.CommandText =
                    "SELECT image_path FROM messages WHERE thread_id = $id AND image_path IS NOT NULL;";
                query.Parameters.AddWithValue("$id", threadId);

                imagePaths = [];
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    var path = reader.IsDBNull(0) ? null : reader.GetString(0);
                    if (!string.IsNullOrEmpty(path))
                    {
                        imagePaths.Add(path);
                    }
                }
            }

            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM threads WHERE id = $id;";
            command.Parameters.AddWithValue("$id", threadId);
            command.ExecuteNonQuery();
        }

        // 文件删除放在事务外：删不掉不影响数据一致性，不该因此回滚
        foreach (var path in imagePaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"删除截图失败（已忽略）：{path} — {ex.Message}");
            }
        }
    }

    /// <summary>若标题仍是默认值，用首条消息更新它。</summary>
    public void UpdateTitleIfDefault(long threadId, string candidate)
    {
        var title = BuildTitle(candidate);
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        lock (_gate)
        {
            ThrowIfDisposed();

            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE threads SET title = $t WHERE id = $id AND title = $d;";
            command.Parameters.AddWithValue("$t", title);
            command.Parameters.AddWithValue("$id", threadId);
            command.Parameters.AddWithValue("$d", DefaultTitle);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 取非空文本的前 24 个字符作为标题。
    /// </summary>
    /// <remarks>
    /// 必须按文本元素截断，不能按 UTF-16 码元：emoji 等增补平面字符占两个码元，
    /// 从中间切开会产生半个字符的乱码，而这个乱码会被直接写进数据库标题。
    /// </remarks>
    public static string BuildTitle(string text)
    {
        const int maxLength = 24;

        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (flat.Length <= maxLength)
        {
            return flat;
        }

        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(flat);
        var count = 0;
        var cut = 0;

        while (enumerator.MoveNext())
        {
            count++;
            cut += ((string)enumerator.Current).Length;

            if (count == maxLength)
            {
                break;
            }
        }

        return flat[..cut] + "…";
    }

    // ---------------------------------------------------------------- 消息

    /// <summary>读取某个会话的全部消息（按时间顺序）。</summary>
    public List<ChatMessageEntity> ListMessages(long threadId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, thread_id, is_user, text, image_path, created_at
                FROM messages WHERE thread_id = $id ORDER BY id;
                """;
            command.Parameters.AddWithValue("$id", threadId);

            var result = new List<ChatMessageEntity>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ChatMessageEntity
                {
                    Id = reader.GetInt64(0),
                    ThreadId = reader.GetInt64(1),
                    IsUser = reader.GetInt64(2) != 0,
                    Text = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    ImagePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt = ParseTime(reader.GetString(5)),
                });
            }

            return result;
        }
    }

    /// <summary>追加一条消息，并把所属会话的更新时间推到现在。</summary>
    public long AddMessage(
        long threadId,
        bool isUser,
        string text,
        string? imagePath = null)
    {
        var now = DateTime.UtcNow.ToString("O");

        lock (_gate)
        {
            ThrowIfDisposed();

            using var transaction = _connection.BeginTransaction();

            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO messages(thread_id, is_user, text, image_path, created_at)
                VALUES ($tid, $u, $t, $img, $c);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$tid", threadId);
            insert.Parameters.AddWithValue("$u", isUser ? 1 : 0);
            insert.Parameters.AddWithValue("$t", text);
            insert.Parameters.AddWithValue("$img", (object?)imagePath ?? DBNull.Value);
            insert.Parameters.AddWithValue("$c", now);
            var id = Convert.ToInt64(insert.ExecuteScalar() ?? 0L);

            using var touch = _connection.CreateCommand();
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE threads SET updated_at = $u WHERE id = $id;";
            touch.Parameters.AddWithValue("$u", now);
            touch.Parameters.AddWithValue("$id", threadId);
            touch.ExecuteNonQuery();

            transaction.Commit();
            return id;
        }
    }

    /// <summary>找到最近一个会话；没有则新建一个。</summary>
    public long GetOrCreateLatestThread()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id FROM threads ORDER BY updated_at DESC LIMIT 1;";
            var existing = command.ExecuteScalar();
            if (existing is not null && existing != DBNull.Value)
            {
                return Convert.ToInt64(existing);
            }
        }

        return CreateThread();
    }

    // ---------------------------------------------------------------- 工具

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? ExecuteScalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static DateTime ParseTime(string raw) =>
        DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : DateTime.UtcNow;

    public void Dispose()
    {
        // 与其它方法共用同一把锁和同一个 _disposed 标志：
        // 否则后台翻译线程正在执行命令时，UI 线程退出释放连接会抛 ObjectDisposedException。
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.Dispose();
        }
    }
}
