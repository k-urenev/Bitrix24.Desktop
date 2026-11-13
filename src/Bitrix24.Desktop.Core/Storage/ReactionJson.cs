using System.Text.Json;

namespace Bitrix24.Desktop.Core.Storage;

internal static class ReactionJson
{
    public static string Write(IReadOnlyList<ChatReaction> reactions) =>
        reactions.Count == 0 ? "" : JsonSerializer.Serialize(reactions);

    public static IReadOnlyList<ChatReaction> Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<ChatReaction>();

        try
        {
            return JsonSerializer.Deserialize<List<ChatReaction>>(json) ?? [];
        }
        catch (JsonException)
        {
            return Array.Empty<ChatReaction>();
        }
    }
}
