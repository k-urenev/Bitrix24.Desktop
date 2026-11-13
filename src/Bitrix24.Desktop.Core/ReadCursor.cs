using System.Text.Json;

namespace Bitrix24.Desktop.Core;

public static class ReadCursor
{
    public static bool IsDelivered(string status) =>
        status.Equals("delivered", StringComparison.OrdinalIgnoreCase);

    public static int? FromDialog(JsonElement dialog, int userId)
    {
        if (userId <= 0)
            return null;

        var list = JsonRead.Prop(dialog, "readed_list", "readedList");
        if (list.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            return null;

        var read = 0;
        foreach (var item in JsonRead.Items(list))
        {
            var reader = JsonRead.Int(item, "user_id", "userId");
            if (reader <= 0 || reader == userId)
                continue;
            var messageId = JsonRead.Int(item, "message_id", "messageId");
            if (messageId > read)
                read = messageId;
        }

        return read;
    }
}
