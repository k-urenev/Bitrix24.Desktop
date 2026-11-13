using System.Windows;
using System.Windows.Media;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.ViewModels;

public sealed class ReactionFace
{
    public ReactionFace(ImageSource? picture, string initials, string color, bool overlap)
    {
        Picture = picture;
        Initials = initials;
        Color = string.IsNullOrWhiteSpace(color) ? "#00ACE3" : color;
        Margin = overlap ? new Thickness(-4, 0, 0, 0) : new Thickness(0);
    }

    public ImageSource? Picture { get; }

    public string Initials { get; }

    public string Color { get; }

    public Thickness Margin { get; }

    public bool HasAvatar => Picture is not null;
}

public sealed class ReactionChip
{
    public ReactionChip(int messageId, ChatReaction reaction, Func<ChatReactionFace, double, ImageSource?>? picture = null)
    {
        MessageId = messageId;
        Code = reaction.Code;
        Title = ReactionCatalog.Title(reaction.Code);
        Count = reaction.Count;
        Mine = reaction.Mine;
        var glyph = ReactionImages.Shared.Glyph(reaction.Code);
        Image = glyph.Image;
        IconWidth = glyph.Width;
        IconHeight = glyph.Height;
        FaceGap = new Thickness(glyph.Width / 5, 0, 0, 0);
        ShowFaces = reaction.Faces.Count > 0;
        ShowCount = !ShowFaces;
        var faces = new ReactionFace[reaction.Faces.Count];
        for (var i = 0; i < reaction.Faces.Count; i++)
        {
            var face = reaction.Faces[i];
            faces[i] = new ReactionFace(picture?.Invoke(face, glyph.Height), TextCleaner.Initials(face.Name), face.Color, i > 0);
        }

        Faces = faces;
        if (ShowFaces)
        {
            var names = string.Join(", ", reaction.Faces.Select(face => face.Name).Where(name => name.Length > 0));
            Tip = names.Length == 0 ? Title : Title + ": " + names;
        }
        else
            Tip = Count > 1 ? Title + " · " + Count : Title;
    }

    public int MessageId { get; }

    public string Code { get; }

    public string Title { get; }

    public int Count { get; }

    public bool Mine { get; }

    public ImageSource? Image { get; }

    public double IconWidth { get; }

    public double IconHeight { get; }

    public Thickness FaceGap { get; }

    public bool ShowFaces { get; }

    public bool ShowCount { get; }

    public IReadOnlyList<ReactionFace> Faces { get; }

    public string Tip { get; }
}
