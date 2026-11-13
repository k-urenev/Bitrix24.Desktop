using System.Text.Json;
using Microsoft.Data.Sqlite;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Auth;
using Bitrix24.Desktop.Core.Storage;

namespace Bitrix24.Desktop.Core.Tests;

public class RestAndStoreTests
{
    [Fact]
    public void Rest_error_and_string_id_are_read()
    {
        using var document = JsonDocument.Parse("""
            {"error":"insufficient_scope","result":{"ID":"15","DOWNLOAD_URL":"/disk/download"}}
            """);

        Assert.Equal("insufficient_scope", JsonRead.Str(document.RootElement, "error"));
        Assert.Equal(15, JsonRead.Int(JsonRead.Prop(document.RootElement, "result"), "ID", "id"));
        Assert.Equal("/disk/download", JsonRead.Str(JsonRead.Prop(document.RootElement, "result"), "DOWNLOAD_URL", "downloadUrl"));
    }

    [Fact]
    public void Refresh_url_asks_the_portal_for_a_new_token()
    {
        var url = TokenEndpoints.Build(
            TokenEndpoints.From("corp.example.ru", null),
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = "local.app",
                ["client_secret"] = "secret",
                ["refresh_token"] = "refresh"
            });

        Assert.StartsWith("https://corp.example.ru/oauth/token/?", url);
        Assert.Contains("grant_type=refresh_token", url);
        Assert.Contains("refresh_token=refresh", url);
    }

    [Fact]
    public void Edited_flag_is_read_from_message_params()
    {
        using var document = JsonDocument.Parse("""
            {"params":{"IS_EDITED":"Y","FILE_ID":[]}}
            """);

        Assert.True(MessageFlags.IsEdited(document.RootElement));
    }

    [Fact]
    public void Edited_message_keeps_the_flag_in_the_cache()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "b24-edited-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(folder.FullName, "local.db");
        try
        {
            var database = new LocalDatabase(path);
            database.Initialize();
            database.SaveMessages("chat", new[]
            {
                new ChatMessage(7, "chat", 1, "Автор", "было", null, false, Array.Empty<ChatFile>())
            });

            database.UpdateMessageText("chat", 7, "стало");

            var shown = database.GetMessages("chat");
            Assert.Equal("стало", shown[0].Text);
            Assert.True(shown[0].IsEdited);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder.FullName, true);
        }
    }

    [Fact]
    public void Saved_messages_keep_only_the_newest_rows()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "b24-messages-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(folder.FullName, "local.db");
        try
        {
            var database = new LocalDatabase(path);
            database.Initialize();
            var messages = Enumerable.Range(1, LocalDatabase.KeptMessagesPerDialog + 50)
                .Select(id => new ChatMessage(id, "chat", 1, "Автор", "текст", null, false, Array.Empty<ChatFile>()))
                .ToArray();

            database.SaveMessages("chat", messages);

            Assert.Equal(LocalDatabase.KeptMessagesPerDialog, database.CountMessages("chat"));
            var shown = database.GetMessages("chat");
            Assert.Equal(200, shown.Count);
            Assert.Equal(LocalDatabase.KeptMessagesPerDialog + 50 - 199, shown[0].Id);
            Assert.Equal("текст", shown[0].Text);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder.FullName, true);
        }
    }

    [Fact]
    public void Dialog_keeps_the_opponents_read_cursor_when_the_chat_list_refreshes()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "b24-reads-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(folder.FullName, "local.db");
        try
        {
            var database = new LocalDatabase(path);
            database.Initialize();
            database.SaveDialogs(new[] { Dialog("632", 2995268, delivered: false) });
            database.SetOthersRead("632", 2995268);
            database.SaveDialogs(new[] { Dialog("632", 2995652, delivered: false) });

            var dialog = Assert.Single(database.GetDialogs());
            Assert.Equal(2995268, dialog.OthersReadMessageId);
            Assert.False(dialog.LastMessageDelivered);
            Assert.Equal(2995652, dialog.LastMessageId);

            database.SaveDialogs(new[] { Dialog("632", 2995652, delivered: true) });
            dialog = Assert.Single(database.GetDialogs());
            Assert.True(dialog.LastMessageDelivered);
            Assert.Equal(2995268, dialog.OthersReadMessageId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder.FullName, true);
        }
    }

    [Fact]
    public void Task_notices_stay_with_their_user_and_keep_the_newest()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "b24-notices-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(folder.FullName, "local.db");
        try
        {
            var database = new LocalDatabase(path);
            database.Initialize();
            var at = DateTimeOffset.Parse("2026-11-06T10:18:00+03:00");
            database.AddTaskNotices(7, new[] { Notice(1, "Первая") }, at);
            database.AddTaskNotices(7, new[] { Notice(2, "Вторая") }, at.AddMinutes(1));
            database.AddTaskNotices(8, new[] { Notice(3, "Чужая") }, at);

            var mine = database.GetTaskNotices(7);
            Assert.Equal(2, mine.Count);
            Assert.Equal("Вторая", mine[0].Title);
            Assert.Equal("Первая", mine[1].Title);
            Assert.Equal("Чужая", Assert.Single(database.GetTaskNotices(8)).Title);
            Assert.Equal(2, database.CountUnreadTaskNotices(7));
            Assert.Equal(1, database.CountUnreadTaskNotices(8));
            database.MarkTaskNoticesRead(7);
            Assert.Equal(0, database.CountUnreadTaskNotices(7));
            Assert.Equal(1, database.CountUnreadTaskNotices(8));

            var bulk = Enumerable.Range(1, LocalDatabase.KeptTaskNotices + 5)
                .Select(id => Notice(id, "Задача " + id))
                .ToArray();
            database.AddTaskNotices(7, bulk, at);
            var kept = database.GetTaskNotices(7);
            Assert.Equal(LocalDatabase.KeptTaskNotices, kept.Count);
            Assert.Equal("Задача " + (LocalDatabase.KeptTaskNotices + 5), kept[0].Title);
            Assert.Equal(LocalDatabase.KeptTaskNotices, database.CountUnreadTaskNotices(7));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder.FullName, true);
        }
    }

    [Fact]
    public void Task_notices_saved_before_the_unread_flag_stay_read()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "b24-notices-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(folder.FullName, "local.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE task_notices (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        user_id INTEGER NOT NULL,
                        task_id INTEGER NOT NULL,
                        title TEXT NOT NULL,
                        body TEXT NOT NULL,
                        url TEXT,
                        created_at TEXT NOT NULL
                    );
                    INSERT INTO task_notices (user_id, task_id, title, body, url, created_at)
                    VALUES (7, 1, 'Старая', 'текст', NULL, '2026-11-01T10:00:00+03:00');
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
            var database = new LocalDatabase(path);
            database.Initialize();
            Assert.Equal(0, database.CountUnreadTaskNotices(7));
            database.AddTaskNotices(7, new[] { Notice(2, "Новая") }, DateTimeOffset.Parse("2026-11-06T10:18:00+03:00"));
            Assert.Equal(1, database.CountUnreadTaskNotices(7));
            Assert.Equal("Новая", database.GetTaskNotices(7)[0].Title);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder.FullName, true);
        }
    }

    [Fact]
    public void A_comment_notice_rewrites_the_existing_task_row()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "b24-notices-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(folder.FullName, "local.db");
        try
        {
            var database = new LocalDatabase(path);
            database.Initialize();
            var at = DateTimeOffset.Parse("2026-11-06T10:18:00+03:00");
            database.AddTaskNotices(7, new[] { Notice(1, "Задача", "Статус: Ждёт выполнения → Выполняется") }, at);
            database.MarkTaskNoticesRead(7);
            var later = at.AddHours(2);
            database.AddTaskNotices(7, new[] { Notice(1, "Задача", "Оставлен комментарий", update: true) }, later);

            var mine = database.GetTaskNotices(7);
            var notice = Assert.Single(mine);
            Assert.Equal("Оставлен комментарий", notice.Body);
            Assert.Equal(later, notice.At);
            Assert.Equal(1, database.CountUnreadTaskNotices(7));
            Assert.Empty(database.GetTaskNotices(8));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder.FullName, true);
        }
    }

    [Fact]
    public void A_link_preview_is_kept_with_the_message()
    {
        var folder = Directory.CreateTempSubdirectory("b24-link-");
        var path = Path.Combine(folder.FullName, "app.db");
        try
        {
            var database = new LocalDatabase(path);
            database.Initialize();
            database.SaveLinkPreview("chat", 4, new LinkPreview("https://example.com/a", "Заголовок", "Описание", ""));

            var stored = database.GetLinkPreviews("chat");
            Assert.Equal("Заголовок", stored[4].Title);
            Assert.Equal("https://example.com/a", stored[4].Url);

            database.SaveLinkPreview("chat", 4, new LinkPreview("https://example.com/b", "Новый", "", "pic.img"));
            stored = database.GetLinkPreviews("chat");
            Assert.Equal("Новый", stored[4].Title);
            Assert.Equal("pic.img", stored[4].ImagePath);

            database.DeleteLinkPreview("chat", 4);
            Assert.Empty(database.GetLinkPreviews("chat"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder.FullName, true);
        }
    }

    private static AppNotification Notice(int id, string title, string body = "текст", bool update = false) =>
        new(NotificationKind.Task, title, body, null, id, "https://portal/tasks/task/view/" + id + "/", update);

    private static DialogSummary Dialog(string id, int messageId, bool delivered) =>
        new(id, "Андрей", null, "#00ACE3", "тест", messageId, 520, DateTimeOffset.Parse("2026-11-02T15:29:00+03:00"), 0, "user", delivered);
}
