using System.Text.Json;

namespace Bitrix24.Desktop.Core.Polling;

public enum PullNoticeKind
{
    Chat,
    ReplaceChannel,
    ReloadConfig,
    Writing,
    Reaction
}

public readonly record struct PullNotice(
    PullNoticeKind Kind,
    string? Mid,
    string? ChannelType,
    string? ChannelId,
    string? DialogId = null,
    int UserId = 0,
    int ChatId = 0,
    int MessageId = 0,
    bool ReactionActive = false,
    bool WholeChat = false);

public sealed class PullSession
{
    public const int Revision = 19;

    public int Version { get; private set; }
    public bool WebSocketEnabled { get; private set; }
    public string? WebSocketUrl { get; private set; }
    public string? LongPollUrl { get; private set; }
    public string? Jwt { get; private set; }
    public string? ClientId { get; private set; }
    public string? Hostname { get; private set; }
    public string? ServerMode { get; private set; }
    public string? PrivateId { get; set; }
    public string? SharedId { get; set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    public bool HasLongPoll => !string.IsNullOrWhiteSpace(LongPollUrl);

    public bool NeedsRefresh(DateTimeOffset now) =>
        ExpiresAt is DateTimeOffset end && end <= now.AddMinutes(2);

    public void ReplaceChannel(string? type, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        if (string.Equals(type, "shared", StringComparison.OrdinalIgnoreCase))
            SharedId = id;
        else
            PrivateId = id;
    }

    public string? BuildUrl(bool longPolling, string? mid)
    {
        var baseUrl = longPolling ? LongPollUrl : WebSocketUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(Jwt))
        {
            parts.Add("token=" + Uri.EscapeDataString(Jwt));
        }
        else
        {
            var channels = new List<string>();
            if (!string.IsNullOrEmpty(PrivateId))
                channels.Add(PrivateId);
            if (!string.IsNullOrEmpty(SharedId))
                channels.Add(SharedId);
            if (channels.Count == 0)
                return null;

            parts.Add("CHANNEL_ID=" + Uri.EscapeDataString(string.Join("/", channels)));
        }

        if (Version >= 5)
            parts.Add("jsonRpc=true");
        else if (Version >= 4)
            parts.Add("binaryMode=true");
        if (string.Equals(ServerMode, "shared", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(ClientId))
            parts.Add("clientId=" + Uri.EscapeDataString(ClientId));
        if (!string.IsNullOrEmpty(Hostname))
            parts.Add("hostname=" + Uri.EscapeDataString(Hostname));
        if (!string.IsNullOrEmpty(mid))
            parts.Add("mid=" + Uri.EscapeDataString(mid));
        if (Version >= 4)
            parts.Add("revision=" + Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return baseUrl + separator + string.Join("&", parts);
    }

    public static PullSession? TryParse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return null;

        var server = JsonRead.Prop(result, "server");
        if (server.ValueKind != JsonValueKind.Object)
            return null;
        if (Flag(server, "server_enabled") == false)
            return null;

        var session = new PullSession
        {
            Version = JsonRead.Int(server, "version"),
            WebSocketUrl = FirstUrl(server, "websocket_secure", "websocket"),
            LongPollUrl = FirstUrl(server, "long_pooling_secure", "long_polling_secure", "long_polling"),
            Jwt = BlankToNull(JsonRead.Str(result, "jwt")),
            ClientId = BlankToNull(JsonRead.Str(result, "clientId")),
            Hostname = BlankToNull(JsonRead.Str(server, "hostname")),
            ServerMode = BlankToNull(JsonRead.Str(server, "mode"))
        };
        session.WebSocketEnabled = Flag(server, "websocket_enabled") ?? !string.IsNullOrEmpty(session.WebSocketUrl);

        var channels = JsonRead.Prop(result, "channels");
        session.PrivateId = ChannelId(channels, "private");
        session.SharedId = ChannelId(channels, "shared");
        session.ExpiresAt = EarliestEnd(result, channels);

        if (string.IsNullOrEmpty(session.Jwt) && string.IsNullOrEmpty(session.PrivateId) && string.IsNullOrEmpty(session.SharedId))
            return null;
        if (string.IsNullOrEmpty(session.WebSocketUrl) && string.IsNullOrEmpty(session.LongPollUrl))
            return null;

        return session;
    }

    public static IReadOnlyList<PullNotice> ReadNotices(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return [];

        var notices = new List<PullNotice>();
        if (payload.Contains("#!NGINXNMS!#", StringComparison.Ordinal))
        {
            foreach (var chunk in payload.Split("#!NGINXNMS!#", StringSplitOptions.RemoveEmptyEntries))
            {
                var end = chunk.IndexOf("#!NGINXNME!#", StringComparison.Ordinal);
                var json = end >= 0 ? chunk[..end] : chunk;
                ReadJson(json.Trim(), notices);
            }

            return notices;
        }

        ReadJson(payload.Trim(), notices);
        return notices;
    }

    public static bool ShouldRefreshChats(string moduleId, string command)
    {
        if (!string.Equals(moduleId, "im", StringComparison.OrdinalIgnoreCase) || command.Length == 0)
            return false;
        if (command.Contains("riting", StringComparison.OrdinalIgnoreCase))
            return false;
        if (command.Contains("status", StringComparison.OrdinalIgnoreCase))
            return false;
        if (command.Contains("online", StringComparison.OrdinalIgnoreCase))
            return false;

        return command.Contains("message", StringComparison.OrdinalIgnoreCase)
            || command.Contains("chat", StringComparison.OrdinalIgnoreCase)
            || command.Contains("read", StringComparison.OrdinalIgnoreCase)
            || command.Contains("notify", StringComparison.OrdinalIgnoreCase)
            || command.Contains("file", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReadJson(string json, List<PullNotice> notices)
    {
        if (json.Length == 0 || json.Equals("ping", StringComparison.OrdinalIgnoreCase))
            return;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in document.RootElement.EnumerateArray())
                    ReadEnvelope(item, notices);
                return;
            }

            ReadEnvelope(document.RootElement, notices);
        }
    }

    private static void ReadEnvelope(JsonElement envelope, List<PullNotice> notices)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
            return;

        var mid = BlankToNull(JsonRead.Str(envelope, "mid"));
        var text = JsonRead.Prop(envelope, "text");
        if (text.ValueKind == JsonValueKind.String)
        {
            var inner = text.GetString();
            if (string.IsNullOrWhiteSpace(inner) || inner.Equals("ping", StringComparison.OrdinalIgnoreCase))
                return;
            if (inner.TrimStart().StartsWith('{'))
            {
                ReadJson(inner, notices);
                if (mid is not null && notices.Count > 0 && notices[^1].Mid is null)
                    notices[^1] = notices[^1] with { Mid = mid };
            }

            return;
        }

        if (text.ValueKind != JsonValueKind.Object)
            text = envelope;

        var moduleId = JsonRead.Str(text, "module_id");
        var command = JsonRead.Str(text, "command");
        if (string.Equals(moduleId, "pull", StringComparison.OrdinalIgnoreCase))
        {
            ReadService(text, mid, command, notices);
            return;
        }

        if (IsWriting(moduleId, command))
        {
            ReadWriting(text, mid, notices);
            return;
        }

        var reaction = ReadReaction(moduleId, command, text, mid);
        if (reaction is not null)
        {
            notices.AddRange(reaction);
            if (!ShouldRefreshChats(moduleId, command))
                return;
        }

        if (ShouldRefreshChats(moduleId, command))
            notices.Add(new PullNotice(PullNoticeKind.Chat, mid, null, null));
    }

    private static List<PullNotice>? ReadReaction(string moduleId, string command, JsonElement text, string? mid)
    {
        if (!string.Equals(moduleId, "im", StringComparison.OrdinalIgnoreCase))
            return null;

        var kind = ReactionCommand(command);
        if (kind == ReactionCommandKind.None)
            return null;

        var parameters = JsonRead.Prop(text, "params");
        if (parameters.ValueKind != JsonValueKind.Object)
            parameters = text;

        if (kind == ReactionCommandKind.ClearChats)
        {
            var notices = new List<PullNotice>();
            foreach (var item in JsonRead.Items(JsonRead.Prop(parameters, "chatIds", "chat_ids")))
            {
                var clearedChat = AsInt(item);
                if (clearedChat <= 0)
                    continue;
                notices.Add(new PullNotice(PullNoticeKind.Reaction, mid, null, null, ChatId: clearedChat, WholeChat: true));
            }

            return notices;
        }

        if (kind == ReactionCommandKind.AddAnchor)
        {
            var type = JsonRead.Str(parameters, "type");
            if (!type.Equals("reaction", StringComparison.OrdinalIgnoreCase))
                return new List<PullNotice>();
        }

        var dialogId = JsonRead.IdString(parameters, "dialogId", "dialog_id");
        var chatId = JsonRead.Int(parameters, "chatId", "chat_id");
        var messageId = ReactionMessageId(parameters);
        var actor = kind == ReactionCommandKind.AddAnchor
            ? JsonRead.Int(parameters, "fromUserId", "from_user_id")
            : JsonRead.Int(parameters, "userId", "user_id");
        var active = kind is ReactionCommandKind.Add or ReactionCommandKind.AddAnchor;
        var wholeChat = kind == ReactionCommandKind.ClearChat || (messageId <= 0 && !active);
        return
        [
            new PullNotice(
                PullNoticeKind.Reaction,
                mid,
                null,
                null,
                dialogId.Length == 0 ? null : dialogId,
                actor,
                chatId,
                messageId,
                active,
                wholeChat)
        ];
    }

    private enum ReactionCommandKind
    {
        None,
        Add,
        AddAnchor,
        Clear,
        ClearChat,
        ClearChats
    }

    private static ReactionCommandKind ReactionCommand(string command)
    {
        if (command.Equals("deleteChatAnchors", StringComparison.OrdinalIgnoreCase))
            return ReactionCommandKind.ClearChat;
        if (command.Equals("deleteAnchors", StringComparison.OrdinalIgnoreCase))
            return ReactionCommandKind.ClearChats;
        if (command.Equals("deleteAnchor", StringComparison.OrdinalIgnoreCase))
            return ReactionCommandKind.Clear;
        if (command.Equals("addAnchor", StringComparison.OrdinalIgnoreCase))
            return ReactionCommandKind.AddAnchor;
        if (command.Equals("deleteReaction", StringComparison.OrdinalIgnoreCase)
            || command.Equals("messageReactionDelete", StringComparison.OrdinalIgnoreCase))
            return ReactionCommandKind.Clear;
        if (command.Equals("addReaction", StringComparison.OrdinalIgnoreCase)
            || command.Equals("messageReaction", StringComparison.OrdinalIgnoreCase))
            return ReactionCommandKind.Add;
        return ReactionCommandKind.None;
    }

    private static int ReactionMessageId(JsonElement parameters)
    {
        var id = JsonRead.Int(parameters, "messageId", "message_id");
        if (id > 0)
            return id;

        var reaction = JsonRead.Prop(parameters, "reaction");
        id = JsonRead.Int(reaction, "messageId", "message_id");
        if (id > 0)
            return id;

        var actual = JsonRead.Prop(parameters, "actualReactions", "actual_reactions");
        id = JsonRead.Int(actual, "messageId", "message_id");
        if (id > 0)
            return id;

        return JsonRead.Int(JsonRead.Prop(actual, "reaction"), "messageId", "message_id");
    }

    private static int AsInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number))
            return number;
        if (value.ValueKind == JsonValueKind.Object)
            return JsonRead.Int(value, "chatId", "chat_id", "id");
        return 0;
    }

    private static bool IsWriting(string moduleId, string command) =>
        string.Equals(moduleId, "im", StringComparison.OrdinalIgnoreCase)
        && command.Contains("startWriting", StringComparison.OrdinalIgnoreCase);

    private static void ReadWriting(JsonElement text, string? mid, List<PullNotice> notices)
    {
        var parameters = JsonRead.Prop(text, "params");
        if (parameters.ValueKind != JsonValueKind.Object)
            parameters = text;

        var dialogId = JsonRead.IdString(parameters, "dialogId", "DIALOG_ID");
        var userId = JsonRead.Int(parameters, "userId", "USER_ID");
        if (dialogId.Length == 0 && userId <= 0)
            return;

        notices.Add(new PullNotice(PullNoticeKind.Writing, mid, null, null, dialogId, userId));
    }

    private static void ReadService(JsonElement text, string? mid, string command, List<PullNotice> notices)
    {
        if (command.Equals("channel_expire", StringComparison.OrdinalIgnoreCase))
        {
            var parameters = JsonRead.Prop(text, "params");
            var action = JsonRead.Str(parameters, "action");
            if (action.Equals("reconnect", StringComparison.OrdinalIgnoreCase))
            {
                var id = JsonRead.Str(JsonRead.Prop(parameters, "new_channel"), "id");
                var type = JsonRead.Str(JsonRead.Prop(parameters, "channel"), "type");
                if (id.Length > 0)
                {
                    notices.Add(new PullNotice(PullNoticeKind.ReplaceChannel, mid, type, id));
                    return;
                }
            }

            notices.Add(new PullNotice(PullNoticeKind.ReloadConfig, mid, null, null));
            return;
        }

        if (command.Equals("config_expire", StringComparison.OrdinalIgnoreCase)
            || command.Equals("server_restart", StringComparison.OrdinalIgnoreCase))
            notices.Add(new PullNotice(PullNoticeKind.ReloadConfig, mid, null, null));
    }

    private static string? ChannelId(JsonElement channels, string type)
    {
        if (channels.ValueKind != JsonValueKind.Object)
            return null;
        return BlankToNull(JsonRead.Str(JsonRead.Prop(channels, type), "id"));
    }

    private static DateTimeOffset? EarliestEnd(JsonElement result, JsonElement channels)
    {
        DateTimeOffset? earliest = JsonRead.Date(result, "exp");
        if (channels.ValueKind != JsonValueKind.Object)
            return earliest;

        foreach (var type in new[] { "private", "shared" })
        {
            var end = JsonRead.Date(JsonRead.Prop(channels, type), "end");
            if (end is not DateTimeOffset moment)
                continue;
            if (earliest is null || moment < earliest)
                earliest = moment;
        }

        return earliest;
    }

    private static string? FirstUrl(JsonElement server, params string[] names)
    {
        foreach (var name in names)
        {
            var url = JsonRead.Str(server, name);
            if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return url;
        }

        return null;
    }

    private static string? BlankToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool? Flag(JsonElement element, string name)
    {
        var value = JsonRead.Prop(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString() is "Y" or "y" or "true" or "1",
            JsonValueKind.Number => value.TryGetInt64(out var number) ? number != 0 : null,
            _ => null
        };
    }
}

public static class PullReconnect
{
    public static TimeSpan Delay(int failures)
    {
        var seconds = failures switch
        {
            <= 2 => 5,
            <= 4 => 25,
            < 10 => 45,
            _ => 60
        };
        var spread = seconds / 5;
        if (spread > 0)
            seconds += Random.Shared.Next(0, spread + 1);
        return TimeSpan.FromSeconds(seconds);
    }
}
