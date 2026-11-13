namespace Bitrix24.Desktop.Core;

public static class UnreadAnchor
{
    public readonly record struct Row(int Id, bool Mine);

    public static int? Find(IReadOnlyList<Row> rows, int unread)
    {
        if (unread <= 1 || rows.Count == 0)
            return null;

        int? oldest = null;
        var left = unread;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Mine)
                continue;
            oldest = rows[i].Id;
            left--;
            if (left == 0)
                return oldest;
        }

        return oldest;
    }

    public static IReadOnlyList<int> Collect(IReadOnlyList<Row> rows, int unread)
    {
        if (unread <= 0 || rows.Count == 0)
            return Array.Empty<int>();

        var found = new List<int>();
        for (var i = rows.Count - 1; i >= 0 && found.Count < unread; i--)
        {
            if (rows[i].Mine)
                continue;
            found.Add(rows[i].Id);
        }

        found.Reverse();
        return found;
    }
}
