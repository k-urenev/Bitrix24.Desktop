namespace Bitrix24.Desktop.Core;

public static class ReactionToggle
{
    public static IReadOnlyList<ChatReaction> Apply(
        IReadOnlyList<ChatReaction>? current,
        string code,
        ChatReactionFace mine)
    {
        var list = new List<ChatReaction>(current?.Count ?? 1);
        if (current is not null)
            list.AddRange(current);

        var index = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Code == code)
            {
                index = i;
                break;
            }
        }

        if (index >= 0 && list[index].Mine)
            list[index] = WithoutMe(list[index], mine.Id);
        else if (index >= 0)
            list[index] = WithMe(list[index], mine);
        else
            list.Add(new ChatReaction(code, 1, true, new[] { mine }));

        if (index >= 0 && list[index].Count <= 0)
            list.RemoveAt(index);

        return ReactionCatalog.Sort(list);
    }

    private static ChatReaction WithoutMe(ChatReaction reaction, int userId)
    {
        var count = reaction.Count - 1;
        if (count <= 0)
            return reaction with { Count = 0, Mine = false, Faces = Array.Empty<ChatReactionFace>() };

        return reaction with
        {
            Count = count,
            Mine = false,
            Faces = DropFace(reaction.Faces, userId, count)
        };
    }

    private static ChatReaction WithMe(ChatReaction reaction, ChatReactionFace mine)
    {
        var count = reaction.Count + 1;
        IReadOnlyList<ChatReactionFace> faces = Array.Empty<ChatReactionFace>();
        if (count <= ReactionCatalog.FaceLimit && reaction.Faces.Count == reaction.Count)
        {
            var next = new List<ChatReactionFace>(count);
            foreach (var face in reaction.Faces)
            {
                if (face.Id != mine.Id)
                    next.Add(face);
            }

            next.Add(mine);
            if (next.Count == count)
                faces = next;
        }

        return reaction with { Count = count, Mine = true, Faces = faces };
    }

    private static IReadOnlyList<ChatReactionFace> DropFace(IReadOnlyList<ChatReactionFace> faces, int userId, int count)
    {
        if (faces.Count == 0)
            return Array.Empty<ChatReactionFace>();

        var next = new List<ChatReactionFace>(faces.Count);
        foreach (var face in faces)
        {
            if (face.Id != userId)
                next.Add(face);
        }

        return next.Count == count ? next : Array.Empty<ChatReactionFace>();
    }
}
