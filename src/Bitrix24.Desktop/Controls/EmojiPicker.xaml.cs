using System.Windows;
using System.Windows.Controls;

namespace Bitrix24.Desktop.Controls;

public partial class EmojiPicker : UserControl
{
    private Button? _selected;
    private bool _built;

    public EmojiPicker()
    {
        InitializeComponent();
        Loaded += (_, _) => Build();
    }

    public event EventHandler<EmojiChosenEventArgs>? Chosen;

    private void Build()
    {
        if (_built)
            return;

        _built = true;
        var sections = EmojiCatalog.Shared.Sections;
        if (sections.Count == 0)
        {
            Body.Visibility = Visibility.Collapsed;
            EmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        Button? first = null;
        EmojiSection? firstSection = null;
        foreach (var section in sections)
        {
            var button = new Button
            {
                Style = (Style)FindResource("EmojiCategory"),
                Content = new TextBlock
                {
                    Text = section.Title,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center
                },
                Tag = section
            };
            button.Click += (_, _) => Show(button, section);
            Categories.Children.Add(button);
            first ??= button;
            firstSection ??= section;
        }

        if (first != null && firstSection != null)
            Show(first, firstSection);
    }

    private void Show(Button button, EmojiSection section)
    {
        if (_selected != null)
            Paint(_selected, false);

        _selected = button;
        Paint(button, true);

        Cells.Children.Clear();
        var cellStyle = (Style)FindResource("EmojiCell");
        foreach (var entry in section.Items)
        {
            var picture = EmojiCatalog.Shared.Picture(entry.CodePoint, 28);
            if (picture == null)
                continue;

            var cell = new Button
            {
                Style = cellStyle,
                Content = picture,
                ToolTip = string.IsNullOrEmpty(entry.Name) ? null : entry.Name,
                Tag = entry.Text
            };
            cell.Click += (_, _) => Chosen?.Invoke(this, new EmojiChosenEventArgs(entry.Text));
            Cells.Children.Add(cell);
        }
    }

    private void Paint(Button button, bool selected)
    {
        button.Background = selected ? (System.Windows.Media.Brush)FindResource("Accent") : System.Windows.Media.Brushes.Transparent;
        var foreground = selected ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)FindResource("Ink");
        button.Foreground = foreground;
        if (button.Content is TextBlock label)
            label.Foreground = foreground;
    }
}

public sealed class EmojiChosenEventArgs : EventArgs
{
    public EmojiChosenEventArgs(string character) => Character = character;

    public string Character { get; }
}
