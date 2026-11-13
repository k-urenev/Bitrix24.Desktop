using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class FormatTextTests
{
    [Fact]
    public void Bold_wraps_text()
    {
        var segments = FormatText.Split("[b]hi[/b]").ToList();
        var segment = Assert.Single(segments);
        Assert.Equal("hi", segment.Text);
        Assert.True(segment.Style.Bold);
    }

    [Fact]
    public void Nested_styles_combine()
    {
        var segments = FormatText.Split("a [b]b [i]c[/i] d[/b] e").ToList();
        Assert.Equal(5, segments.Count);
        Assert.Equal("a ", segments[0].Text);
        Assert.Equal("b ", segments[1].Text);
        Assert.True(segments[1].Style.Bold);
        Assert.Equal("c", segments[2].Text);
        Assert.True(segments[2].Style.Bold);
        Assert.True(segments[2].Style.Italic);
        Assert.Equal(" d", segments[3].Text);
        Assert.True(segments[3].Style.Bold);
        Assert.Equal(" e", segments[4].Text);
    }

    [Fact]
    public void User_profile_url_uses_portal_domain()
    {
        Assert.Equal(
            "https://company.bitrix24.ru/company/personal/user/42/",
            TextCleaner.UserProfileUrl("company.bitrix24.ru", 42));
    }
}
