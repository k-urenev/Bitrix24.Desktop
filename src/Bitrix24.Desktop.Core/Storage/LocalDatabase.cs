using Microsoft.Data.Sqlite;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Storage;

public sealed class LocalDatabase
{
    public const int KeptMessagesPerDialog = 400;
    public const int KeptTaskNotices = 200;

    private readonly string _connectionString;
    private readonly object _gate = new();

    public LocalDatabase(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    public void Initialize()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS dialogs (
                    dialog_id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    color TEXT NOT NULL,
                    preview TEXT NOT NULL,
                    last_message_id INTEGER NOT NULL,
                    last_author_id INTEGER NOT NULL,
                    last_message_at TEXT,
                    unread_counter INTEGER NOT NULL,
                    dialog_type TEXT NOT NULL,
                    position INTEGER NOT NULL,
                    last_delivered INTEGER NOT NULL DEFAULT 0,
                    others_read_message_id INTEGER NOT NULL DEFAULT 0,
                    chat_id INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS reaction_marks (
                    dialog_id TEXT NOT NULL DEFAULT '',
                    chat_id INTEGER NOT NULL DEFAULT 0,
                    message_id INTEGER NOT NULL,
                    PRIMARY KEY (dialog_id, chat_id, message_id)
                );
                CREATE TABLE IF NOT EXISTS messages (
                    dialog_id TEXT NOT NULL,
                    message_id INTEGER NOT NULL,
                    author_id INTEGER NOT NULL,
                    author_name TEXT NOT NULL,
                    text TEXT NOT NULL,
                    sent_at TEXT,
                    is_system INTEGER NOT NULL,
                    PRIMARY KEY (dialog_id, message_id)
                );
                CREATE TABLE IF NOT EXISTS dialog_cursors (
                    dialog_id TEXT PRIMARY KEY,
                    last_notified_message_id INTEGER NOT NULL,
                    last_read_message_id INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS task_snapshots (
                    task_id INTEGER PRIMARY KEY,
                    title TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    deadline TEXT,
                    responsible_id INTEGER NOT NULL,
                    comment_count INTEGER NOT NULL,
                    new_comment_count INTEGER NOT NULL,
                    created_at TEXT,
                    changed_at TEXT
                );
                CREATE TABLE IF NOT EXISTS app_flags (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS task_notices (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id INTEGER NOT NULL,
                    task_id INTEGER NOT NULL,
                    title TEXT NOT NULL,
                    body TEXT NOT NULL,
                    url TEXT,
                    created_at TEXT NOT NULL,
                    unread INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS link_previews (
                    dialog_id TEXT NOT NULL,
                    message_id INTEGER NOT NULL,
                    url TEXT NOT NULL,
                    title TEXT NOT NULL,
                    description TEXT NOT NULL,
                    image_path TEXT NOT NULL,
                    PRIMARY KEY (dialog_id, message_id)
                );
                """;
            command.ExecuteNonQuery();
            EnsureColumn(connection, "messages", "files_json", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "messages", "reactions_json", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(connection, "messages", "is_edited", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "dialogs", "last_delivered", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "dialogs", "others_read_message_id", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "dialogs", "chat_id", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "task_notices", "unread", "INTEGER NOT NULL DEFAULT 0");
        }
    }

    public void SaveDialogs(IReadOnlyList<DialogSummary> dialogs)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var keptReads = ReadOthers(connection, transaction);
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM dialogs";
                delete.ExecuteNonQuery();
            }

            for (var index = 0; index < dialogs.Count; index++)
            {
                var dialog = dialogs[index];
                keptReads.TryGetValue(dialog.DialogId, out var keptRead);
                var othersRead = Math.Max(keptRead, dialog.OthersReadMessageId);
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO dialogs (dialog_id, title, color, preview, last_message_id, last_author_id, last_message_at, unread_counter, dialog_type, position, last_delivered, others_read_message_id, chat_id)
                    VALUES ($id, $title, $color, $preview, $lastId, $author, $at, $unread, $type, $position, $delivered, $others, $chat)
                    """;
                insert.Parameters.AddWithValue("$id", dialog.DialogId);
                insert.Parameters.AddWithValue("$title", dialog.Title);
                insert.Parameters.AddWithValue("$color", dialog.Color);
                insert.Parameters.AddWithValue("$preview", dialog.LastMessageText);
                insert.Parameters.AddWithValue("$lastId", dialog.LastMessageId);
                insert.Parameters.AddWithValue("$author", dialog.LastMessageAuthorId);
                insert.Parameters.AddWithValue("$at", dialog.LastMessageAt?.ToString("o") ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("$unread", dialog.UnreadCounter);
                insert.Parameters.AddWithValue("$type", dialog.Type);
                insert.Parameters.AddWithValue("$position", index);
                insert.Parameters.AddWithValue("$delivered", dialog.LastMessageDelivered ? 1 : 0);
                insert.Parameters.AddWithValue("$others", othersRead);
                insert.Parameters.AddWithValue("$chat", dialog.ChatId);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<DialogSummary> GetDialogs()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT dialog_id, title, color, preview, last_message_id, last_author_id, last_message_at, unread_counter, dialog_type, last_delivered, others_read_message_id, chat_id
                FROM dialogs
                ORDER BY position
                """;
            using var reader = command.ExecuteReader();
            var list = new List<DialogSummary>();
            while (reader.Read())
            {
                list.Add(new DialogSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    null,
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    ReadDate(reader, 6),
                    reader.GetInt32(7),
                    reader.GetString(8),
                    reader.GetInt32(9) == 1,
                    reader.GetInt32(10),
                    ChatId: reader.GetInt32(11)));
            }

            return list;
        }
    }

    public int NewestMessageId(string dialogId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(message_id) FROM messages WHERE dialog_id = $dialog";
            command.Parameters.AddWithValue("$dialog", dialogId);
            var value = command.ExecuteScalar();
            return value is long id ? (int)id : value is int number ? number : 0;
        }
    }

    public IReadOnlyList<FeedGap> ListGaps()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT d.dialog_id, d.title, d.last_message_id,
                       COALESCE((SELECT MAX(m.message_id) FROM messages m WHERE m.dialog_id = d.dialog_id), 0)
                FROM dialogs d
                WHERE d.last_message_id >
                      COALESCE((SELECT MAX(m.message_id) FROM messages m WHERE m.dialog_id = d.dialog_id), 0)
                ORDER BY d.position
                """;
            using var reader = command.ExecuteReader();
            var list = new List<FeedGap>();
            while (reader.Read())
            {
                list.Add(new FeedGap(
                    reader.GetString(0),
                    reader.GetString(1),
                    Convert.ToInt32(reader.GetValue(2)),
                    Convert.ToInt32(reader.GetValue(3))));
            }

            return list;
        }
    }

    public void BackupTo(string path)
    {
        lock (_gate)
        {
            if (File.Exists(path))
                File.Delete(path);
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }
    }

    public void RememberReaction(string dialogId, int chatId, int messageId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO reaction_marks (dialog_id, chat_id, message_id)
                VALUES ($dialog, $chat, $message)
                ON CONFLICT(dialog_id, chat_id, message_id) DO UPDATE SET
                    dialog_id = excluded.dialog_id,
                    chat_id = excluded.chat_id
                """;
            command.Parameters.AddWithValue("$dialog", dialogId);
            command.Parameters.AddWithValue("$chat", chatId);
            command.Parameters.AddWithValue("$message", messageId);
            command.ExecuteNonQuery();
        }
    }

    public void ForgetReaction(string dialogId, int chatId, int messageId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM reaction_marks
                WHERE message_id = $message
                  AND (
                    ($dialog <> '' AND dialog_id = $dialog)
                    OR ($chat > 0 AND chat_id = $chat)
                  )
                """;
            command.Parameters.AddWithValue("$dialog", dialogId);
            command.Parameters.AddWithValue("$chat", chatId);
            command.Parameters.AddWithValue("$message", messageId);
            command.ExecuteNonQuery();
        }
    }

    public void ForgetChatReactions(string dialogId, int chatId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM reaction_marks
                WHERE ($dialog <> '' AND dialog_id = $dialog)
                   OR ($chat > 0 AND chat_id = $chat)
                """;
            command.Parameters.AddWithValue("$dialog", dialogId);
            command.Parameters.AddWithValue("$chat", chatId);
            command.ExecuteNonQuery();
        }
    }

    public bool HasUnreadReaction(string dialogId, int chatId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT 1
                FROM reaction_marks
                WHERE ($dialog <> '' AND dialog_id = $dialog)
                   OR ($chat > 0 AND chat_id = $chat)
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$dialog", dialogId);
            command.Parameters.AddWithValue("$chat", chatId);
            return command.ExecuteScalar() is not null;
        }
    }

    public string FindDialogId(int chatId)
    {
        if (chatId <= 0)
            return "";

        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT dialog_id FROM dialogs WHERE chat_id = $chat LIMIT 1";
            command.Parameters.AddWithValue("$chat", chatId);
            return command.ExecuteScalar() as string ?? "";
        }
    }

    public int FindChatId(string dialogId)
    {
        if (dialogId.Length == 0)
            return 0;

        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT chat_id FROM dialogs WHERE dialog_id = $dialog";
            command.Parameters.AddWithValue("$dialog", dialogId);
            return command.ExecuteScalar() is long chat ? (int)chat : 0;
        }
    }

    public void SaveMessages(string dialogId, IReadOnlyList<ChatMessage> messages)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var message in messages)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO messages (dialog_id, message_id, author_id, author_name, text, sent_at, is_system, files_json, reactions_json, is_edited)
                    VALUES ($dialog, $id, $author, $name, $text, $at, $system, $files, $reactions, $edited)
                    ON CONFLICT(dialog_id, message_id) DO UPDATE SET
                        author_id = excluded.author_id,
                        author_name = excluded.author_name,
                        text = excluded.text,
                        sent_at = excluded.sent_at,
                        is_system = excluded.is_system,
                        files_json = excluded.files_json,
                        reactions_json = CASE WHEN $keep = 1 THEN messages.reactions_json ELSE excluded.reactions_json END,
                        is_edited = excluded.is_edited
                    """;
                command.Parameters.AddWithValue("$dialog", dialogId);
                command.Parameters.AddWithValue("$id", message.Id);
                command.Parameters.AddWithValue("$author", message.AuthorId);
                command.Parameters.AddWithValue("$name", message.AuthorName);
                command.Parameters.AddWithValue("$text", message.Text);
                command.Parameters.AddWithValue("$at", message.SentAt?.ToString("o") ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$system", message.IsSystem ? 1 : 0);
                command.Parameters.AddWithValue("$files", FileListJson.Write(message.Files));
                command.Parameters.AddWithValue("$reactions", message.Reactions is null ? "" : ReactionJson.Write(message.Reactions));
                command.Parameters.AddWithValue("$edited", message.IsEdited ? 1 : 0);
                command.Parameters.AddWithValue("$keep", message.Reactions is null ? 1 : 0);
                command.ExecuteNonQuery();
            }

            using (var prune = connection.CreateCommand())
            {
                prune.Transaction = transaction;
                prune.CommandText = """
                    DELETE FROM messages
                    WHERE dialog_id = $dialog
                      AND message_id NOT IN (
                          SELECT message_id FROM messages
                          WHERE dialog_id = $dialog
                          ORDER BY message_id DESC
                          LIMIT $keep)
                    """;
                prune.Parameters.AddWithValue("$dialog", dialogId);
                prune.Parameters.AddWithValue("$keep", KeptMessagesPerDialog);
                prune.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void SaveReactions(string dialogId, int messageId, IReadOnlyList<ChatReaction> reactions)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE messages
                SET reactions_json = $reactions
                WHERE dialog_id = $dialog AND message_id = $id
                """;
            command.Parameters.AddWithValue("$reactions", ReactionJson.Write(reactions));
            command.Parameters.AddWithValue("$dialog", dialogId);
            command.Parameters.AddWithValue("$id", messageId);
            command.ExecuteNonQuery();
        }
    }

    public int CountMessages(string dialogId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM messages WHERE dialog_id = $dialog";
            command.Parameters.AddWithValue("$dialog", dialogId);
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    public IReadOnlyList<ChatMessage> GetMessages(string dialogId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT message_id, author_id, author_name, text, sent_at, is_system, files_json, reactions_json, is_edited
                FROM (
                    SELECT message_id, author_id, author_name, text, sent_at, is_system, files_json, reactions_json, is_edited
                    FROM messages
                    WHERE dialog_id = $dialog
                    ORDER BY message_id DESC
                    LIMIT 200
                )
                ORDER BY message_id
                """;
            command.Parameters.AddWithValue("$dialog", dialogId);
            using var reader = command.ExecuteReader();
            var list = new List<ChatMessage>();
            while (reader.Read())
            {
                list.Add(new ChatMessage(
                    reader.GetInt32(0),
                    dialogId,
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    ReadDate(reader, 4),
                    reader.GetInt32(5) == 1,
                    FileListJson.Read(reader.IsDBNull(6) ? "" : reader.GetString(6)),
                    Reactions: ReactionJson.Read(reader.IsDBNull(7) ? "" : reader.GetString(7)),
                    IsEdited: reader.GetInt32(8) == 1));
            }

            return list;
        }
    }

    public void UpdateMessageText(string dialogId, int messageId, string text)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE messages
                SET text = $text, is_edited = 1
                WHERE dialog_id = $dialog AND message_id = $id
                """;
            command.Parameters.AddWithValue("$dialog", dialogId);
            command.Parameters.AddWithValue("$id", messageId);
            command.Parameters.AddWithValue("$text", text);
            command.ExecuteNonQuery();
        }
    }

    public void SaveLinkPreview(string dialogId, int messageId, LinkPreview preview)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO link_previews (dialog_id, message_id, url, title, description, image_path)
                VALUES ($dialog, $id, $url, $title, $description, $image)
                ON CONFLICT(dialog_id, message_id) DO UPDATE SET
                    url = excluded.url,
                    title = excluded.title,
                    description = excluded.description,
                    image_path = excluded.image_path
                """;
            command.Parameters.AddWithValue("$dialog", dialogId);
            command.Parameters.AddWithValue("$id", messageId);
            command.Parameters.AddWithValue("$url", preview.Url);
            command.Parameters.AddWithValue("$title", preview.Title);
            command.Parameters.AddWithValue("$description", preview.Description);
            command.Parameters.AddWithValue("$image", preview.ImagePath);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteLinkPreview(string dialogId, int messageId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM link_previews WHERE dialog_id = $dialog AND message_id = $id";
            command.Parameters.AddWithValue("$dialog", dialogId);
            command.Parameters.AddWithValue("$id", messageId);
            command.ExecuteNonQuery();
        }
    }

    public Dictionary<int, LinkPreview> GetLinkPreviews(string dialogId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT message_id, url, title, description, image_path
                FROM link_previews
                WHERE dialog_id = $dialog
                """;
            command.Parameters.AddWithValue("$dialog", dialogId);
            using var reader = command.ExecuteReader();
            var map = new Dictionary<int, LinkPreview>();
            while (reader.Read())
            {
                map[reader.GetInt32(0)] = new LinkPreview(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4));
            }

            return map;
        }
    }

    public IReadOnlyDictionary<string, int> GetCursors()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT dialog_id, last_notified_message_id FROM dialog_cursors";
            using var reader = command.ExecuteReader();
            var cursors = new Dictionary<string, int>(StringComparer.Ordinal);
            while (reader.Read())
                cursors[reader.GetString(0)] = reader.GetInt32(1);
            return cursors;
        }
    }

    public void SetLastNotified(string dialogId, int messageId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO dialog_cursors (dialog_id, last_notified_message_id, last_read_message_id)
                VALUES ($id, $message, 0)
                ON CONFLICT(dialog_id) DO UPDATE SET
                    last_notified_message_id = MAX(dialog_cursors.last_notified_message_id, excluded.last_notified_message_id)
                """;
            command.Parameters.AddWithValue("$id", dialogId);
            command.Parameters.AddWithValue("$message", messageId);
            command.ExecuteNonQuery();
        }
    }

    public void SetOthersRead(string dialogId, int messageId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE dialogs
                SET others_read_message_id = $read
                WHERE dialog_id = $id
                """;
            command.Parameters.AddWithValue("$read", messageId);
            command.Parameters.AddWithValue("$id", dialogId);
            command.ExecuteNonQuery();
        }
    }

    public void SetLastRead(string dialogId, int messageId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO dialog_cursors (dialog_id, last_notified_message_id, last_read_message_id)
                VALUES ($id, $message, $message)
                ON CONFLICT(dialog_id) DO UPDATE SET
                    last_read_message_id = MAX(dialog_cursors.last_read_message_id, excluded.last_read_message_id),
                    last_notified_message_id = MAX(dialog_cursors.last_notified_message_id, excluded.last_notified_message_id)
                """;
            command.Parameters.AddWithValue("$id", dialogId);
            command.Parameters.AddWithValue("$message", messageId);
            command.ExecuteNonQuery();
        }
    }

    public bool HasFlag(string key) => GetFlag(key) is not null;

    public string? GetFlag(string key)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_flags WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetFlag(string key, string value)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_flags (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyDictionary<int, TaskSnapshot> GetTaskSnapshots()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT task_id, title, status, deadline, responsible_id, comment_count, new_comment_count, created_at, changed_at
                FROM task_snapshots
                """;
            using var reader = command.ExecuteReader();
            var snapshots = new Dictionary<int, TaskSnapshot>();
            while (reader.Read())
            {
                var snapshot = ReadTask(reader);
                snapshots[snapshot.Id] = snapshot;
            }

            return snapshots;
        }
    }

    public void ReplaceTaskSnapshots(IReadOnlyList<TaskSnapshot> tasks)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM task_snapshots";
                delete.ExecuteNonQuery();
            }

            foreach (var task in tasks)
                InsertTask(connection, transaction, task);
            transaction.Commit();
        }
    }

    public void UpsertTaskSnapshots(IReadOnlyList<TaskSnapshot> tasks)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var task in tasks)
                InsertTask(connection, transaction, task);
            transaction.Commit();
        }
    }

    private static void InsertTask(SqliteConnection connection, SqliteTransaction transaction, TaskSnapshot task)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO task_snapshots (task_id, title, status, deadline, responsible_id, comment_count, new_comment_count, created_at, changed_at)
            VALUES ($id, $title, $status, $deadline, $responsible, $comments, $fresh, $created, $changed)
            ON CONFLICT(task_id) DO UPDATE SET
                title = excluded.title,
                status = excluded.status,
                deadline = excluded.deadline,
                responsible_id = excluded.responsible_id,
                comment_count = excluded.comment_count,
                new_comment_count = excluded.new_comment_count,
                created_at = excluded.created_at,
                changed_at = excluded.changed_at
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$status", task.Status);
        command.Parameters.AddWithValue("$deadline", (object?)task.Deadline ?? DBNull.Value);
        command.Parameters.AddWithValue("$responsible", task.ResponsibleId);
        command.Parameters.AddWithValue("$comments", task.CommentCount);
        command.Parameters.AddWithValue("$fresh", task.NewCommentCount);
        command.Parameters.AddWithValue("$created", task.CreatedAt?.ToString("o") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$changed", task.ChangedAt?.ToString("o") ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void AddTaskNotices(int userId, IReadOnlyList<AppNotification> cards, DateTimeOffset at)
    {
        var tasks = cards.Where(card => card.Kind == NotificationKind.Task).ToArray();
        if (tasks.Length == 0)
            return;

        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var card in tasks)
            {
                var taskId = card.TaskId ?? 0;
                if (card.UpdateExisting && UpdateTaskNotice(connection, transaction, userId, taskId, card, at))
                    continue;

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO task_notices (user_id, task_id, title, body, url, created_at, unread)
                    VALUES ($user, $task, $title, $body, $url, $at, 1)
                    """;
                command.Parameters.AddWithValue("$user", userId);
                command.Parameters.AddWithValue("$task", taskId);
                command.Parameters.AddWithValue("$title", card.Title);
                command.Parameters.AddWithValue("$body", card.Body);
                command.Parameters.AddWithValue("$url", (object?)card.ExternalUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$at", at.ToString("o"));
                command.ExecuteNonQuery();
            }

            using var trim = connection.CreateCommand();
            trim.Transaction = transaction;
            trim.CommandText = """
                DELETE FROM task_notices
                WHERE user_id = $user
                AND id NOT IN (
                    SELECT id FROM task_notices WHERE user_id = $user ORDER BY id DESC LIMIT $keep
                )
                """;
            trim.Parameters.AddWithValue("$user", userId);
            trim.Parameters.AddWithValue("$keep", KeptTaskNotices);
            trim.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private static bool UpdateTaskNotice(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int userId,
        int taskId,
        AppNotification card,
        DateTimeOffset at)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE task_notices
            SET title = $title,
                body = $body,
                url = $url,
                created_at = $at,
                unread = 1
            WHERE id = (
                SELECT id FROM task_notices
                WHERE user_id = $user AND task_id = $task
                ORDER BY id DESC
                LIMIT 1
            )
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$title", card.Title);
        command.Parameters.AddWithValue("$body", card.Body);
        command.Parameters.AddWithValue("$url", (object?)card.ExternalUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", at.ToString("o"));
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<TaskNoticeRecord> GetTaskNotices(int userId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, task_id, title, body, url, created_at
                FROM task_notices
                WHERE user_id = $user
                ORDER BY id DESC
                """;
            command.Parameters.AddWithValue("$user", userId);
            using var reader = command.ExecuteReader();
            var list = new List<TaskNoticeRecord>();
            while (reader.Read())
            {
                var at = DateTimeOffset.TryParse(reader.GetString(5), out var parsed)
                    ? parsed
                    : DateTimeOffset.Now;
                list.Add(new TaskNoticeRecord(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    at));
            }

            return list;
        }
    }

    public int CountUnreadTaskNotices(int userId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM task_notices WHERE user_id = $user AND unread = 1";
            command.Parameters.AddWithValue("$user", userId);
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    public void MarkTaskNoticesRead(int userId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE task_notices SET unread = 0 WHERE user_id = $user AND unread = 1";
            command.Parameters.AddWithValue("$user", userId);
            command.ExecuteNonQuery();
        }
    }

    private static TaskSnapshot ReadTask(SqliteDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetInt32(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        ReadDate(reader, 7),
        ReadDate(reader, 8));

    private static Dictionary<string, int> ReadOthers(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT dialog_id, others_read_message_id FROM dialogs";
        using var reader = command.ExecuteReader();
        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
            reads[reader.GetString(0)] = reader.GetInt32(1);
        return reads;
    }

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        var text = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table})";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
