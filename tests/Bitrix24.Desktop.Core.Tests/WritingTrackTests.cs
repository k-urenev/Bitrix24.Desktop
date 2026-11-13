using Bitrix24.Desktop.Core.Polling;

namespace Bitrix24.Desktop.Core.Tests;

public class WritingTrackTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void First_signal_waits_default_interval_plus_two_seconds()
    {
        var track = new WritingTrack();
        Assert.Equal(TimeSpan.FromSeconds(7), track.HoldAfter(4, Start));
    }

    [Fact]
    public void Later_gap_replaces_the_pause()
    {
        var track = new WritingTrack();
        _ = track.HoldAfter(4, Start);
        Assert.Equal(TimeSpan.FromSeconds(32), track.HoldAfter(4, Start.AddSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(7), track.HoldAfter(4, Start.AddSeconds(35)));
    }

    [Fact]
    public void Burst_does_not_shrink_the_pause()
    {
        var track = new WritingTrack();
        _ = track.HoldAfter(4, Start);
        _ = track.HoldAfter(4, Start.AddSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(7), track.HoldAfter(4, Start.AddMilliseconds(5200)));
    }

    [Fact]
    public void Private_dialog_matches_the_writer()
    {
        Assert.True(WritingTrack.Belongs("20", 0, false, 10, 20, "10"));
        Assert.False(WritingTrack.Belongs("30", 0, false, 10, 20, "10"));
        Assert.False(WritingTrack.Belongs("20", 0, false, 10, 10, "20"));
    }

    [Fact]
    public void Group_dialog_matches_chat_id()
    {
        Assert.True(WritingTrack.Belongs("chat5", 5, true, 10, 20, "chat5"));
        Assert.True(WritingTrack.Belongs("sg17", 9, true, 10, 20, "chat9"));
        Assert.False(WritingTrack.Belongs("chat5", 5, true, 10, 20, "chat8"));
    }
}
