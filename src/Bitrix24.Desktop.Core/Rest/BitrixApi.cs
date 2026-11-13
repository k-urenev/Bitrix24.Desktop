using System.Text.Json;
using System.Text.Json.Nodes;
using Bitrix24.Desktop.Core;
using Bitrix24.Desktop.Core.Auth;
using Bitrix24.Desktop.Core.Polling;

namespace Bitrix24.Desktop.Core.Rest;

public sealed class BitrixApi
{
    private readonly BitrixRestClient _rest;
    private readonly SessionHolder _sessions;

    public BitrixApi(BitrixRestClient rest, SessionHolder sessions)
    {
        _rest = rest;
        _sessions = sessions;
    }

    public async Task<UserProfile> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        using var document = await _rest.CallAsync("user.current", new JsonObject(), cancellationToken);
        var result = Result(document);
        var name = string.Join(" ", new[]
        {
            JsonRead.Str(result, "NAME", "name"),
            JsonRead.Str(result, "LAST_NAME", "lastName", "last_name")
        }.Where(part => part.Length > 0));
        if (string.IsNullOrWhiteSpace(name))
            name = "Сотрудник";
        return new UserProfile(
            JsonRead.Int(result, "ID", "id"),
            name,
            UserPhoto(result, _sessions.Current.PortalDomain));
    }

    public async Task<PullSession?> GetPullSessionAsync(CancellationToken cancellationToken)
    {
        using var document = await _rest.CallAsync("pull.application.config.get", new JsonObject(), cancellationToken);
        return PullSession.TryParse(Result(document));
    }

    public async Task<IReadOnlyList<string>> GetScopesAsync(CancellationToken cancellationToken)
    {
        using var document = await _rest.CallAsync("scope", new JsonObject(), cancellationToken);
        var scopes = new List<string>();
        foreach (var item in JsonRead.Items(Result(document)))
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;
            var scope = item.GetString();
            if (!string.IsNullOrWhiteSpace(scope))
                scopes.Add(scope);
        }

        return scopes;
    }

    public async Task<byte[]> DownloadDiskFileAsync(int fileId, CancellationToken cancellationToken)
    {
        using var document = await _rest.CallAsync("disk.file.get", new JsonObject { ["id"] = fileId }, cancellationToken);
        var result = Result(document);
        var url = JsonRead.Str(result, "DOWNLOAD_URL", "downloadUrl");
        if (url.Length == 0)
            url = JsonRead.Str(JsonRead.Prop(result, "file"), "DOWNLOAD_URL", "downloadUrl");

        var portal = _sessions.Current.PortalDomain;
        url = PortalAddress.Absolute(portal, url) ?? "";
        if (!PortalAddress.IsPortalHttps(portal, url))
            throw new BitrixException("Ссылка на файл ведёт не на портал.", null, false, false, null);

        return await _rest.DownloadAuthorizedAsync(url, portal, MaxPreviewBytes, cancellationToken);
    }

    public async Task<byte[]> DownloadPictureAsync(string url, CancellationToken cancellationToken)
    {
        var portal = _sessions.Current.PortalDomain;
        if (PortalAddress.IsPortalHttps(portal, url))
            return await _rest.DownloadAuthorizedAsync(url, portal, MaxPreviewBytes, cancellationToken);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new BitrixException("Ссылка на аватар ведёт не на портал.", null, false, false, null);

        return await _rest.DownloadPublicAsync(url, MaxPreviewBytes, cancellationToken);
    }

    public async Task<IReadOnlyList<DialogSummary>> GetRecentDialogsAsync(CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["SKIP_OPENLINES"] = "Y",
            ["SKIP_DIALOG"] = "N",
            ["SKIP_CHAT"] = "N",
            ["ONLY_OPENLINES"] = "N",
            ["ONLY_COPILOT"] = "N",
            ["ONLY_CHANNEL"] = "N",
            ["PARSE_TEXT"] = "Y",
            ["LIMIT"] = 50
        };
        using var document = await _rest.CallAsync("im.recent.list", body, cancellationToken);
        var items = JsonRead.Prop(Result(document), "items");
        var portal = _sessions.Current.PortalDomain;
        var list = new List<DialogSummary>();
        foreach (var item in JsonRead.Items(items))
        {
            var dialogId = JsonRead.IdString(item, "id");
            var type = JsonRead.Str(item, "type");
            if (string.IsNullOrWhiteSpace(dialogId) || !IsDirectOrGroup(type, dialogId))
                continue;

            var message = JsonRead.Prop(item, "message");
            var avatar = JsonRead.Prop(item, "avatar");
            var avatarUrl = avatar.ValueKind == JsonValueKind.Object
                ? JsonRead.Str(avatar, "url")
                : JsonRead.Str(item, "avatar");
            var color = avatar.ValueKind == JsonValueKind.Object ? JsonRead.Str(avatar, "color") : "";
            var text = TextCleaner.ToPlain(JsonRead.Str(message, "text"), singleLine: true, portal);
            if (string.IsNullOrWhiteSpace(text) && HasAttachment(message))
                text = "Вложение";

            var presence = ReadPresence(item, type, dialogId);
            list.Add(new DialogSummary(
                dialogId,
                JsonRead.Str(item, "title"),
                PortalAddress.Absolute(portal, avatarUrl),
                string.IsNullOrWhiteSpace(color) ? "#2F80ED" : color,
                text,
                JsonRead.Int(message, "id"),
                JsonRead.Int(message, "author_id", "authorId"),
                JsonRead.Date(message, "date"),
                JsonRead.Int(item, "counter"),
                type,
                ReadCursor.IsDelivered(JsonRead.Str(message, "status")),
                HasPresence: presence.HasPresence,
                IsOnline: presence.IsOnline,
                LastSeenAt: presence.LastSeenAt,
                Gender: presence.Gender,
                ChatId: JsonRead.Int(item, "chat_id", "chatId"),
                AvatarKnown: true));
        }

        return list;
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(
        string dialogId,
        int? firstId,
        int? lastId,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["DIALOG_ID"] = dialogId,
            ["LIMIT"] = 50
        };
        if (firstId is int first)
            body["FIRST_ID"] = first;
        if (lastId is int last)
            body["LAST_ID"] = last;

        using var document = await _rest.CallAsync("im.dialog.messages.get", body, cancellationToken);
        var result = Result(document);
        var portal = _sessions.Current.PortalDomain;
        var users = ReadUsers(JsonRead.Prop(result, "users"), portal);
        var files = ReadFiles(JsonRead.Prop(result, "files"), portal);
        var messages = new List<ChatMessage>();
        foreach (var item in JsonRead.Items(JsonRead.Prop(result, "messages")))
        {
            var id = JsonRead.Int(item, "id");
            if (id <= 0)
                continue;
            var authorId = JsonRead.Int(item, "author_id", "authorId");
            var text = TextCleaner.ToPlain(JsonRead.Str(item, "text"), singleLine: false, portal);
            var attached = FilesFor(item, files);
            if (string.IsNullOrWhiteSpace(text) && attached.Count > 0)
                text = "";
            else if (string.IsNullOrWhiteSpace(text) && HasAttachment(item))
                text = "Вложение";
            users.TryGetValue(authorId, out var author);
            messages.Add(new ChatMessage(
                id,
                dialogId,
                authorId,
                authorId == 0 ? "Битрикс24" : author?.Name ?? "Сотрудник",
                text,
                JsonRead.Date(item, "date"),
                authorId == 0,
                attached,
                author?.AvatarUrl,
                JsonRead.Int(item, "chat_id", "chatId"),
                IsEdited: MessageFlags.IsEdited(item)));
        }

        messages.Sort((left, right) => left.Id.CompareTo(right.Id));
        return messages;
    }

    public async Task<ReactionPage> GetReactionsAsync(string dialogId, int limit, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["dialogId"] = dialogId,
            ["limit"] = limit
        };
        using var document = await _rest.CallAsync("im.v2.Chat.Message.list", body, cancellationToken);
        return ReactionPageReader.Read(Result(document), _sessions.Current.PortalDomain);
    }

    public async Task<ReactionPage> GetOlderReactionsAsync(
        int chatId,
        int olderThanId,
        int limit,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["chatId"] = chatId,
            ["limit"] = limit,
            ["filter"] = new JsonObject { ["lastId"] = olderThanId },
            ["order"] = new JsonObject { ["id"] = "DESC" }
        };
        using var document = await _rest.CallAsync("im.v2.Chat.Message.tail", body, cancellationToken);
        return ReactionPageReader.Read(Result(document), _sessions.Current.PortalDomain);
    }

    public Task AddReactionAsync(int messageId, string reaction, CancellationToken cancellationToken) =>
        CallAndForget("im.v2.Chat.Message.Reaction.add", ReactionBody(messageId, reaction), cancellationToken);

    public Task DeleteReactionAsync(int messageId, string reaction, CancellationToken cancellationToken) =>
        CallAndForget("im.v2.Chat.Message.Reaction.delete", ReactionBody(messageId, reaction), cancellationToken);

    private static JsonObject ReactionBody(int messageId, string reaction) => new()
    {
        ["messageId"] = messageId,
        ["reaction"] = reaction
    };

    public Task SendMessageAsync(string dialogId, string text, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["DIALOG_ID"] = dialogId,
            ["MESSAGE"] = text
        };
        return CallAndForget("im.message.add", body, cancellationToken);
    }

    public Task UpdateMessageAsync(int messageId, string text, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["MESSAGE_ID"] = messageId,
            ["MESSAGE"] = text
        };
        return CallAndForget("im.message.update", body, cancellationToken);
    }

    public const long MaxAttachmentBytes = 20L * 1024 * 1024;
    public const int MaxPreviewBytes = 12 * 1024 * 1024;

    public async Task SendFileAsync(string dialogId, string path, string message, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new BitrixException("Файл не найден.", null, false, false, null);
        if (info.Length > MaxAttachmentBytes)
            throw new BitrixException($"Файл «{info.Name}» больше 20 МБ.", null, false, false, null);

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var fields = new JsonObject
        {
            ["name"] = info.Name,
            ["content"] = Convert.ToBase64String(bytes)
        };
        if (!string.IsNullOrWhiteSpace(message))
            fields["message"] = message;

        var body = new JsonObject
        {
            ["dialogId"] = dialogId,
            ["fields"] = fields
        };
        await CallAndForget("im.v2.File.upload", body, cancellationToken, releaseDuringSend: true);
    }

    public Task MarkReadAsync(string dialogId, int messageId, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["DIALOG_ID"] = dialogId,
            ["MESSAGE_ID"] = messageId
        };
        return CallAndForget("im.dialog.read", body, cancellationToken);
    }

    public Task ReadReactionAnchorsAsync(int chatId, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["chatId"] = chatId
        };
        return CallAndForget("im.v2.Chat.Anchor.read", body, cancellationToken);
    }

    public async Task<int?> GetOthersReadMessageIdAsync(string dialogId, CancellationToken cancellationToken)
    {
        using var document = await _rest.CallAsync("im.dialog.get", new JsonObject
        {
            ["DIALOG_ID"] = dialogId
        }, cancellationToken);
        return ReadCursor.FromDialog(Result(document), _sessions.Current.UserId);
    }

    public async Task<IReadOnlyList<TaskSnapshot>> GetMemberTasksAsync(
        int userId,
        DateTimeOffset? changedSince,
        CancellationToken cancellationToken)
    {
        var all = new List<TaskSnapshot>();
        var index = new Dictionary<int, int>();
        foreach (var role in new[] { "RESPONSIBLE_ID", "CREATED_BY", "ACCOMPLICE", "AUDITOR" })
        {
            if (all.Count >= 200)
                break;

            var start = 0;
            while (all.Count < 200)
            {
                var (page, nextStart) = await RequestTasksAsync(role, userId, changedSince, start, cancellationToken);
                if (page.Count == 0)
                    break;

                foreach (var task in page)
                {
                    if (index.TryGetValue(task.Id, out var at))
                    {
                        all[at] = MergeRole(all[at], task);
                        continue;
                    }

                    if (all.Count >= 200)
                        break;
                    index[task.Id] = all.Count;
                    all.Add(task);
                }

                if (nextStart is not int next || next <= start)
                    break;
                start = next;
            }
        }

        return all;
    }

    private async Task<(List<TaskSnapshot> Tasks, int? Next)> RequestTasksAsync(
        string role,
        int userId,
        DateTimeOffset? changedSince,
        int start,
        CancellationToken cancellationToken)
    {
        var filter = new JsonObject
        {
            [role] = userId
        };
        if (changedSince is DateTimeOffset since)
            filter[">=CHANGED_DATE"] = since.ToString("yyyy-MM-ddTHH:mm:sszzz");

        var select = new JsonArray();
        foreach (var field in new[]
        {
            "ID", "TITLE", "STATUS", "DEADLINE", "RESPONSIBLE_ID", "CREATED_BY",
            "ACCOMPLICES", "AUDITORS", "IS_MUTED", "CHANGED_DATE", "CREATED_DATE", "COMMENTS_COUNT"
        })
            select.Add(field);

        var body = new JsonObject
        {
            ["filter"] = filter,
            ["select"] = select,
            ["order"] = new JsonObject { ["CHANGED_DATE"] = "desc" },
            ["start"] = start
        };

        using var document = await _rest.CallAsync("tasks.task.list", body, cancellationToken);
        var page = ParseTasks(JsonRead.Prop(Result(document), "tasks"), userId, role);
        int? nextStart = document.RootElement.TryGetProperty("next", out var next) && next.TryGetInt32(out var value)
            ? value
            : null;
        return (page, nextStart);
    }

    private static TaskSnapshot MergeRole(TaskSnapshot left, TaskSnapshot right) => left with
    {
        CreatedBy = left.CreatedBy > 0 ? left.CreatedBy : right.CreatedBy,
        Accomplice = left.Accomplice || right.Accomplice,
        Auditor = left.Auditor || right.Auditor,
        Muted = left.Muted || right.Muted
    };

    private async Task CallAndForget(
        string method,
        JsonObject body,
        CancellationToken cancellationToken,
        bool releaseDuringSend = false)
    {
        using var document = releaseDuringSend
            ? await _rest.CallReleasedAsync(method, body, cancellationToken)
            : await _rest.CallAsync(method, body, cancellationToken);
    }

    private static JsonElement Result(JsonDocument document)
    {
        if (document.RootElement.TryGetProperty("result", out var result))
            return result;
        return default;
    }

    private static bool IsDirectOrGroup(string type, string dialogId)
    {
        if (type is "user" or "chat" or "")
            return true;
        return dialogId.StartsWith("sg", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct Presence(bool HasPresence, bool IsOnline, DateTimeOffset? LastSeenAt, string Gender);

    private static Presence ReadPresence(JsonElement item, string type, string dialogId)
    {
        if (!IsPersonal(type, dialogId))
            return default;

        var user = JsonRead.Prop(item, "user");
        if (user.ValueKind != JsonValueKind.Object)
            return default;

        var idle = JsonRead.Date(user, "idle");
        var status = JsonRead.Str(user, "status");
        var lastSeen = JsonRead.Date(user, "last_activity_date", "lastActivityDate") ?? idle;
        var online = PresenceText.IsCurrent(status, idle, lastSeen, DateTimeOffset.Now);
        return new Presence(true, online, lastSeen, JsonRead.Str(user, "gender"));
    }

    private static bool IsPersonal(string type, string dialogId) =>
        type is "user" or ""
        && !dialogId.StartsWith("chat", StringComparison.OrdinalIgnoreCase)
        && !dialogId.StartsWith("sg", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<int, ChatFile> ReadFiles(JsonElement files, string portal)
    {
        var map = new Dictionary<int, ChatFile>();
        foreach (var file in JsonRead.Items(files))
        {
            var id = JsonRead.Int(file, "id", "ID");
            if (id <= 0)
                continue;
            var name = JsonRead.Str(file, "name", "NAME");
            if (string.IsNullOrWhiteSpace(name))
                name = "Файл";
            var url = Absolute(portal, JsonRead.Str(file, "urlDownload", "urlShow", "urlPreview"));
            var preview = Absolute(portal, JsonRead.Str(file, "urlPreview"));
            if (preview is null && IsImage(name))
                preview = Absolute(portal, JsonRead.Str(file, "urlShow"));
            map[id] = new ChatFile(name, url, ReadSize(file), preview, id);
        }

        return map;
    }

    private static long ReadSize(JsonElement file)
    {
        var value = JsonRead.Prop(file, "size", "SIZE");
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            return number;
        return 0;
    }

    private static bool IsImage(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Absolute(string portal, string url) =>
        string.IsNullOrWhiteSpace(url) ? null : PortalAddress.Absolute(portal, url);

    private static IReadOnlyList<ChatFile> FilesFor(JsonElement message, Dictionary<int, ChatFile> files)
    {
        var parameters = JsonRead.Prop(message, "params");
        var value = JsonRead.Prop(parameters, "FILE_ID", "fileId");
        var list = new List<ChatFile>();
        if (value.ValueKind is JsonValueKind.Number or JsonValueKind.String)
        {
            AddFile(list, files, LooseInt(value));
            return list;
        }

        foreach (var item in JsonRead.Items(value))
            AddFile(list, files, LooseInt(item));
        return list;
    }

    private static void AddFile(List<ChatFile> list, Dictionary<int, ChatFile> files, int id)
    {
        if (id <= 0)
            return;
        list.Add(files.TryGetValue(id, out var file) ? file : new ChatFile("Файл", null));
    }

    private static int LooseInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;
        return JsonRead.Int(value, "id", "ID");
    }

    private static bool HasAttachment(JsonElement message)
    {
        var file = JsonRead.Prop(message, "file");
        if (file.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Number or JsonValueKind.True)
            return true;

        var parameters = JsonRead.Prop(message, "params");
        var fileId = JsonRead.Prop(parameters, "FILE_ID", "fileId");
        if (fileId.ValueKind is JsonValueKind.Array or JsonValueKind.Number)
            return true;
        return fileId.ValueKind == JsonValueKind.String && JsonRead.Str(parameters, "FILE_ID", "fileId").Length > 0;
    }

    private sealed record ChatPerson(string Name, string? AvatarUrl);

    private static Dictionary<int, ChatPerson> ReadUsers(JsonElement users, string portal)
    {
        var people = new Dictionary<int, ChatPerson>();
        foreach (var user in JsonRead.Items(users))
        {
            var id = JsonRead.Int(user, "id", "ID");
            if (id <= 0)
                continue;
            var name = JsonRead.Str(user, "name", "NAME");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = string.Join(" ", new[]
                {
                    JsonRead.Str(user, "first_name", "firstName"),
                    JsonRead.Str(user, "last_name", "lastName")
                }.Where(part => part.Length > 0));
            }

            if (string.IsNullOrWhiteSpace(name))
                continue;
            people[id] = new ChatPerson(name, AvatarOf(user, portal));
        }

        return people;
    }

    private static string? UserPhoto(JsonElement result, string portal)
    {
        var photo = JsonRead.Prop(result, "PERSONAL_PHOTO", "personalPhoto");
        if (photo.ValueKind != JsonValueKind.String)
            return null;
        return PortalAddress.Absolute(portal, photo.GetString());
    }

    private static string? AvatarOf(JsonElement user, string portal)
    {
        var avatar = JsonRead.Prop(user, "avatar", "AVATAR");
        var url = avatar.ValueKind == JsonValueKind.Object
            ? JsonRead.Str(avatar, "url")
            : avatar.ValueKind == JsonValueKind.String ? avatar.GetString() ?? "" : "";
        return Absolute(portal, url);
    }

    private static List<TaskSnapshot> ParseTasks(JsonElement tasks, int userId, string role)
    {
        var list = new List<TaskSnapshot>();
        foreach (var task in JsonRead.Items(tasks))
        {
            var id = JsonRead.Int(task, "id", "ID");
            if (id <= 0)
                continue;

            var deadline = JsonRead.Str(task, "deadline", "DEADLINE");
            var counter = JsonRead.Prop(task, "counter");
            var counters = JsonRead.Prop(counter, "counters");
            var responsibleId = JsonRead.Int(task, "responsibleId", "RESPONSIBLE_ID");
            var createdBy = JsonRead.Int(task, "createdBy", "CREATED_BY");
            if (role == "RESPONSIBLE_ID" && responsibleId == 0)
                responsibleId = userId;
            if (role == "CREATED_BY" && createdBy == 0)
                createdBy = userId;

            list.Add(new TaskSnapshot(
                id,
                JsonRead.Str(task, "title", "TITLE"),
                JsonRead.Int(task, "status", "STATUS"),
                string.IsNullOrWhiteSpace(deadline) ? null : deadline,
                responsibleId,
                JsonRead.Int(task, "commentsCount", "COMMENTS_COUNT"),
                JsonRead.Int(counters, "newComments"),
                JsonRead.Date(task, "createdDate", "CREATED_DATE"),
                JsonRead.Date(task, "changedDate", "CHANGED_DATE"),
                createdBy,
                role == "ACCOMPLICE" || UserListed(task, userId, "accomplices", "ACCOMPLICES"),
                role == "AUDITOR" || UserListed(task, userId, "auditors", "AUDITORS"),
                ReadMuted(task)));
        }

        return list;
    }

    private static bool UserListed(JsonElement task, int userId, params string[] names)
    {
        if (userId <= 0)
            return false;

        var value = JsonRead.Prop(task, names);
        if (value.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            return LooseInt(value) == userId;

        foreach (var item in JsonRead.Items(value))
        {
            var id = item.ValueKind is JsonValueKind.Number or JsonValueKind.String
                ? LooseInt(item)
                : JsonRead.Int(item, "id", "ID");
            if (id == userId)
                return true;
        }

        return false;
    }

    private static bool ReadMuted(JsonElement task)
    {
        var text = JsonRead.Str(task, "isMuted", "IS_MUTED");
        return text.Equals("Y", StringComparison.OrdinalIgnoreCase)
            || text == "1"
            || text.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
