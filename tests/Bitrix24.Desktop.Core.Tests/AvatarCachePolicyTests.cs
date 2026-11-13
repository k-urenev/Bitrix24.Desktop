using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class AvatarCachePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fresh_complete_cache_with_the_same_url_is_kept()
    {
        Assert.False(AvatarCachePolicy.NeedsFetch(Now.AddDays(-13), "https://portal/a.png", "https://portal/a.png", Now, false, true));
    }

    [Fact]
    public void Cache_is_fetched_again_after_fourteen_days()
    {
        Assert.True(AvatarCachePolicy.NeedsFetch(Now.AddDays(-14), "https://portal/a.png", "https://portal/a.png", Now, false, true));
    }

    [Fact]
    public void A_new_url_a_missing_file_or_reload_fetches_again()
    {
        Assert.True(AvatarCachePolicy.NeedsFetch(Now, "https://portal/a.png", "https://portal/b.png", Now, false, true));
        Assert.True(AvatarCachePolicy.NeedsFetch(Now, "https://portal/a.png", "https://portal/a.png", Now, false, false));
        Assert.True(AvatarCachePolicy.NeedsFetch(Now, "https://portal/a.png", "https://portal/a.png", Now, true, true));
        Assert.True(AvatarCachePolicy.NeedsFetch(null, null, "https://portal/a.png", Now, false, false));
    }

    [Fact]
    public void Without_a_url_nothing_is_fetched()
    {
        Assert.False(AvatarCachePolicy.NeedsFetch(null, null, null, Now, true, false));
        Assert.False(AvatarCachePolicy.NeedsFetch(Now, "https://portal/a.png", "  ", Now, true, false));
    }

    [Fact]
    public void First_employees_skip_groups_and_stop_at_twenty()
    {
        var dialogs = new List<(bool IsGroup, int UserId)> { (true, 9), (false, 0), (false, 4), (false, 4) };
        for (var id = 1; id <= 25; id++)
            dialogs.Add((false, 100 + id));

        var ids = AvatarCachePolicy.FirstEmployees(dialogs);

        Assert.Equal(20, ids.Count);
        Assert.Equal(4, ids[0]);
        Assert.Equal(101, ids[1]);
        Assert.Equal(119, ids[^1]);
        Assert.DoesNotContain(9, ids);
    }
}
