using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Controls;

public partial class FileTypeIcon : UserControl
{
    private static readonly SolidColorBrush DocumentBrush = Frozen(255, 85, 136);
    private static readonly SolidColorBrush SheetBrush = Frozen(16, 163, 127);
    private static readonly SolidColorBrush CodeBrush = Frozen(255, 110, 60);
    private static readonly SolidColorBrush AudioBrush = Frozen(255, 107, 53);
    private static readonly SolidColorBrush VideoBrush = Frozen(139, 92, 246);
    private static readonly SolidColorBrush GenericBrush = Frozen(0, 0, 255);

    public static readonly DependencyProperty FileNameProperty = DependencyProperty.Register(
        nameof(FileName),
        typeof(string),
        typeof(FileTypeIcon),
        new PropertyMetadata("", OnFileNameChanged));

    public FileTypeIcon()
    {
        InitializeComponent();
        Show(FileTileKind.Generic);
    }

    public string FileName
    {
        get => (string)GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    private static void OnFileNameChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((FileTypeIcon)sender).Show(FileTile.FromName(args.NewValue as string));
    }

    private void Show(FileTileKind kind)
    {
        Tile.Fill = kind switch
        {
            FileTileKind.Document => DocumentBrush,
            FileTileKind.Sheet => SheetBrush,
            FileTileKind.Code => CodeBrush,
            FileTileKind.Audio => AudioBrush,
            FileTileKind.Video => VideoBrush,
            _ => GenericBrush
        };
        GenericGlyph.Visibility = kind == FileTileKind.Generic ? Visibility.Visible : Visibility.Collapsed;
        DocumentGlyph.Visibility = kind == FileTileKind.Document ? Visibility.Visible : Visibility.Collapsed;
        SheetGlyph.Visibility = kind == FileTileKind.Sheet ? Visibility.Visible : Visibility.Collapsed;
        CodeGlyph.Visibility = kind == FileTileKind.Code ? Visibility.Visible : Visibility.Collapsed;
        AudioGlyph.Visibility = kind == FileTileKind.Audio ? Visibility.Visible : Visibility.Collapsed;
        VideoGlyph.Visibility = kind == FileTileKind.Video ? Visibility.Visible : Visibility.Collapsed;
    }

    private static SolidColorBrush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
