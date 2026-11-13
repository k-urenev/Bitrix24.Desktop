using System.Text.Json;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Storage;

internal static class FileListJson
{
    public static string Write(IReadOnlyList<ChatFile> files) =>
        files.Count == 0 ? "" : JsonSerializer.Serialize(files);

    public static IReadOnlyList<ChatFile> Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<ChatFile>();

        try
        {
            return JsonSerializer.Deserialize<List<ChatFile>>(json) ?? [];
        }
        catch (JsonException)
        {
            return Array.Empty<ChatFile>();
        }
    }
}
