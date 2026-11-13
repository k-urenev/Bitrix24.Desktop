using System.Text.Json;

namespace Bitrix24.Desktop.Core;

public static class MessageFlags
{
    public static bool IsEdited(JsonElement message)
    {
        var parameters = JsonRead.Prop(message, "params");
        return Flag(parameters) || Flag(message);
    }

    private static bool Flag(JsonElement element)
    {
        var value = JsonRead.Prop(element, "IS_EDITED", "isEdited");
        if (value.ValueKind == JsonValueKind.True)
            return true;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number != 0;

        var text = JsonRead.Str(element, "IS_EDITED", "isEdited");
        return text.Equals("Y", StringComparison.OrdinalIgnoreCase)
            || text == "1"
            || text.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
