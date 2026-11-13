using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Controls;

public class EmojiText : TextBlock
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(string),
        typeof(EmojiText),
        new PropertyMetadata("", (sender, _) => ((EmojiText)sender).Rebuild()));

    public static readonly DependencyProperty EmojiSizeProperty = DependencyProperty.Register(
        nameof(EmojiSize),
        typeof(double),
        typeof(EmojiText),
        new PropertyMetadata(20d, (sender, _) => ((EmojiText)sender).Rebuild()));

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public double EmojiSize
    {
        get => (double)GetValue(EmojiSizeProperty);
        set => SetValue(EmojiSizeProperty, value);
    }

    private void Rebuild()
    {
        Inlines.Clear();
        var catalog = EmojiCatalog.Shared;
        foreach (var piece in EmojiPieces.Split(Message, catalog.Has))
        {
            if (piece.CodePoint == 0)
            {
                Inlines.Add(new Run(piece.Text));
                continue;
            }

            var picture = catalog.Picture(piece.CodePoint, EmojiSize > 0 ? EmojiSize : 20);
            if (picture == null)
            {
                Inlines.Add(new Run(piece.Text));
                continue;
            }

            Inlines.Add(new InlineUIContainer(picture) { BaselineAlignment = BaselineAlignment.Center });
        }
    }
}
