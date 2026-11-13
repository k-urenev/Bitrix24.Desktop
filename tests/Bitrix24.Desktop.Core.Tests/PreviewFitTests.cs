using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class PreviewFitTests
{
    [Fact]
    public void Stored_size_fits_inside_1000_by_500_and_does_not_grow()
    {
        Assert.Equal((1000, 500), PreviewFit.StoredPixels(2000, 1000));
        Assert.Equal((500, 500), PreviewFit.StoredPixels(2000, 2000));
        Assert.Equal((80, 40), PreviewFit.StoredPixels(80, 40));
    }

    [Fact]
    public void Display_uses_the_tighter_side_and_keeps_the_ratio()
    {
        var frame = PreviewFit.Display(2000, 1000, windowWidth: 1000, windowHeight: 800, contentMaxWidth: 800);

        Assert.Equal(450, frame.Width, 0);
        Assert.Equal(225, frame.Height, 0);
    }

    [Fact]
    public void Display_does_not_enlarge_a_small_picture()
    {
        var frame = PreviewFit.Display(40, 20, windowWidth: 2000, windowHeight: 1000, contentMaxWidth: 900);

        Assert.Equal(40, frame.Width);
        Assert.Equal(20, frame.Height);
    }

    [Fact]
    public void Viewer_box_shrinks_the_screen_by_the_insets()
    {
        Assert.Equal((1872, 955), PreviewFit.ViewerBox(1920, 1080, 48, 125));
        Assert.Equal((1, 1), PreviewFit.ViewerBox(100, 80, 400, 400));
    }

    [Fact]
    public void Viewer_decode_keeps_a_smaller_original_and_fits_a_larger_one()
    {
        Assert.Equal(0, PreviewFit.ViewerDecodeWidth(80, 40, 1000, 800));
        Assert.Equal(0, PreviewFit.ViewerDecodeWidth(1000, 800, 1000, 800));
        Assert.Equal(1000, PreviewFit.ViewerDecodeWidth(2000, 1000, 1000, 800));
        Assert.Equal(400, PreviewFit.ViewerDecodeWidth(2000, 4000, 1000, 800));
    }

    [Fact]
    public void Block_is_at_least_the_file_row_and_stops_at_the_chat()
    {
        Assert.Equal(274, PreviewFit.BlockWidth(imageWidth: 100, fileRowWidth: 300, contentMaxWidth: 250, padding: 24));
    }
}
