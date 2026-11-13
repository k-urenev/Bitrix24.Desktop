using System.Globalization;
using System.Text.Json;

namespace Bitrix24.Desktop.Core;

public static class JsonRead
{
    public static JsonElement Prop(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return default;

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
                return value;
        }

        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            }
        }

        return default;
    }

    public static string Str(JsonElement element, params string[] names)
    {
        var value = Prop(element, names);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "Y",
            JsonValueKind.False => "",
            _ => ""
        };
    }

    public static string IdString(JsonElement element, params string[] names)
    {
        var value = Prop(element, names);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number.ToString(CultureInfo.InvariantCulture);
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";
        return "";
    }

    public static int Int(JsonElement element, params string[] names)
    {
        var value = Prop(element, names);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        return 0;
    }

    public static DateTimeOffset? Date(JsonElement element, params string[] names)
    {
        var text = Str(element, names);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;
        if (DateTimeOffset.TryParse(text, out parsed))
            return parsed;
        return null;
    }

    public static IEnumerable<JsonElement> Items(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                yield return item;
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                yield return property.Value;
        }
    }
}
