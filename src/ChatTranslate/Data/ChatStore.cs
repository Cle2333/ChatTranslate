using System.IO;
using Microsoft.Data.Sqlite;

namespace ChatTranslate.Data;

/// <summary>
/// 会话存储（SQLite）。
///
/// <para>一个连接贯穿进程生命周期：桌面单用户场景下没有并发压力，
/// 而 SQLite 的连接建立本身有开销。所有公开方法都是同步的，
/// 调用方如需在后台线程使用请自行包裹。</para>
/// </summary>
public sealed class ChatStore : IDisposable
{
    private const int SchemaVersion = 1;

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

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());

        _connection.Open();
        Initialize();
    }

    private void Initialize()
    {
        lock (_gate)
        {
            Execute("PRAGMA journal_mode=WAL;");
            Execute("PRAGMA foreign_keys=ON;");

            Execute("""
                CREATE TABLE IF NOT EXISTS schema_info (
                    version INTEGER NOT NULL
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS threads (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    title       TEXT    NOT NULL DEFAULT '新对话',
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

            using var check = _connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM schema_info;";
            var has = Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0;

            if (!has)
            {
                using var insert = _connection.CreateCommand();
                insert.CommandText = "INSERT INTO schema_info(version) VALUES ($v);";
                insert.Parameters.AddWithValue("$v", SchemaVersion);
                insert.ExecuteNonQuery();
            }
        }
    }

    // ---------------------------------------------------------------- 会话

    /// <summary>列出全部会话，按最近更新排序。</summary>
    public List<ChatThread> ListThreads()
    {
        lock (_gate)
        {
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
    public long CreateThread(string title = "新对话")
    {
        lock (_gate)
        {
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

    /// <summary>删除会话及其全部消息。</summary>
    public void DeleteThread(long threadId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM threads WHERE id = $id;";
            command.Parameters.AddWithValue("$id", threadId);
            command.ExecuteNonQuery();
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
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE threads SET title = $t
                WHERE id = $id AND (title = '新对话' OR title = '');
                """;
            command.Parameters.AddWithValue("$t", title);
            command.Parameters.AddWithValue("$id", threadId);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>取非空文本的前 24 个字符作为标题。</summary>
    public static string BuildTitle(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 24 ? flat : flat[..24] + "…";
    }

    // ---------------------------------------------------------------- 消息

    /// <summary>读取某个会话的全部消息（按时间顺序）。</summary>
    public List<ChatMessageEntity> ListMessages(long threadId)
    {
        lock (_gate)
        {
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

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static DateTime ParseTime(string raw) =>
        DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : DateTime.UtcNow;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }
}
