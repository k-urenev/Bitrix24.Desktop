using System.Text.Json;

namespace Bitrix24.Desktop.Core;

public sealed record ReactionPage(
    int ChatId,
    IReadOnlyList<int> MessageIds,
    IReadOnlyDictionary<int, IReadOnlyList<ChatReaction>> ByMessage)
{
    public IReadOnlyList<ChatReaction> For(int messageId) =>
        ByMessage.TryGetValue(messageId, out var found) ? found : Array.Empty<ChatReaction>();
}

public static class ReactionPageReader
{
    public static ReactionPage Read(JsonElement result, string portal)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return Empty();

        var people = ReadPeople(result, portal);
        var messageIds = new List<int>();
        var seen = new HashSet<int>();
        var chatId = 0;
        foreach (var message in JsonRead.Items(JsonRead.Prop(result, "messages")))
        {
            var id = JsonRead.Int(message, "id", "ID");
            if (id <= 0 || !seen.Add(id))
                continue;
            messageIds.Add(id);
            if (chatId == 0)
                chatId = JsonRead.Int(message, "chatId", "chat_id");
        }

        var byMessage = new Dictionary<int, IReadOnlyList<ChatReaction>>();
        foreach (var item in JsonRead.Items(JsonRead.Prop(result, "reactions")))
        {
            var messageId = JsonRead.Int(item, "messageId", "message_id");
            if (messageId <= 0)
                continue;

            var own = new HashSet<string>(StringComparer.Ordinal);
            foreach (var code in JsonRead.Items(JsonRead.Prop(item, "ownReactions", "own_reactions")))
            {
                if (code.ValueKind != JsonValueKind.String)
                    continue;
                var text = code.GetString();
                if (!string.IsNullOrEmpty(text))
                    own.Add(text);
            }

            var counters = JsonRead.Prop(item, "reactionCounters", "reaction_counters");
            var users = JsonRead.Prop(item, "reactionUsers", "reaction_users");
            var reactions = new List<ChatReaction>();
            if (counters.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in counters.EnumerateObject())
                {
                    var count = Number(property.Value);
                    if (count <= 0)
                        continue;
                    reactions.Add(new ChatReaction(
                        property.Name,
                        count,
                        own.Contains(property.Name),
                        Faces(users, property.Name, count, people)));
                }
            }

            byMessage[messageId] = ReactionCatalog.Sort(reactions);
        }

        return new ReactionPage(chatId, messageIds, byMessage);
    }

    private static ReactionPage Empty() =>
        new(0, Array.Empty<int>(), new Dictionary<int, IReadOnlyList<ChatReaction>>());

    private static IReadOnlyList<ChatReactionFace> Faces(
        JsonElement users,
        string code,
        int count,
        Dictionary<int, ChatReactionFace> people)
    {
        if (count > ReactionCatalog.FaceLimit)
            return Array.Empty<ChatReactionFace>();

        var ids = new List<int>();
        foreach (var item in JsonRead.Items(JsonRead.Prop(users, code)))
        {
            var id = Number(item);
            if (id > 0)
                ids.Add(id);
        }

        if (ids.Count != count)
            return Array.Empty<ChatReactionFace>();

        var faces = new List<ChatReactionFace>(ids.Count);
        foreach (var id in ids)
        {
            if (!people.TryGetValue(id, out var face))
                return Array.Empty<ChatReactionFace>();
            faces.Add(face);
        }

        return faces;
    }

    private static Dictionary<int, ChatReactionFace> ReadPeople(JsonElement result, string portal)
    {
        var people = new Dictionary<int, ChatReactionFace>();
        AddPeople(people, JsonRead.Prop(result, "usersShort", "users_short"), portal);
        AddPeople(people, JsonRead.Prop(result, "users"), portal);
        return people;
    }

    private static void AddPeople(Dictionary<int, ChatReactionFace> people, JsonElement source, string portal)
    {
        foreach (var user in JsonRead.Items(source))
        {
            var id = JsonRead.Int(user, "id", "ID");
            if (id <= 0)
                continue;

            var name = JsonRead.Str(user, "name", "NAME");
            if (name.Length == 0)
            {
                name = string.Join(" ", new[]
                {
                    JsonRead.Str(user, "firstName", "first_name"),
                    JsonRead.Str(user, "lastName", "last_name")
                }.Where(part => part.Length > 0));
            }

            if (name.Length == 0)
                name = "Сотрудник";

            var color = JsonRead.Str(user, "color", "COLOR");
            if (color.Length == 0)
                color = "#00ACE3";

            var avatar = JsonRead.Prop(user, "avatar", "AVATAR");
            var url = avatar.ValueKind == JsonValueKind.Object
                ? JsonRead.Str(avatar, "url")
                : avatar.ValueKind == JsonValueKind.String ? avatar.GetString() ?? "" : "";
            people[id] = new ChatReactionFace(id, name, PortalAddress.Absolute(portal, url), color);
        }
    }

    private static int Number(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number))
            return number;
        return 0;
    }
}
