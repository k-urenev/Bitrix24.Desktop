using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class LinkTextTests
{
    [Fact]
    public void Plain_text_has_no_link()
    {
        var piece = Assert.Single(LinkText.Split("просто текст"));
        Assert.Null(piece.Target);
        Assert.Equal("просто текст", piece.Text);
    }

    [Fact]
    public void Http_and_https_become_links_without_the_trailing_dot()
    {
        var pieces = LinkText.Split("см. https://example.com/a?b=1 и http://site.ru.");

        Assert.Equal("см. ", pieces[0].Text);
        Assert.Null(pieces[0].Target);
        Assert.Equal("https://example.com/a?b=1", pieces[1].Text);
        Assert.Equal("https://example.com/a?b=1", pieces[1].Target);
        Assert.Equal(" и ", pieces[2].Text);
        Assert.Equal("http://site.ru", pieces[3].Text);
        Assert.Equal("http://site.ru", pieces[3].Target);
        Assert.Equal(".", pieces[4].Text);
        Assert.Null(pieces[4].Target);
    }

    [Fact]
    public void A_paren_around_the_address_stays_outside()
    {
        var pieces = LinkText.Split("(https://example.com/path)");

        Assert.Equal("(", pieces[0].Text);
        Assert.Equal("https://example.com/path", pieces[1].Text);
        Assert.Equal("https://example.com/path", pieces[1].Target);
        Assert.Equal(")", pieces[2].Text);
    }

    [Fact]
    public void A_balanced_paren_inside_the_address_stays()
    {
        var pieces = LinkText.Split("https://en.wikipedia.org/wiki/C_(language).");

        Assert.Equal("https://en.wikipedia.org/wiki/C_(language)", pieces[0].Text);
        Assert.Equal("https://en.wikipedia.org/wiki/C_(language)", pieces[0].Target);
        Assert.Equal(".", pieces[1].Text);
        Assert.Null(pieces[1].Target);
    }

    [Fact]
    public void Www_opens_as_https()
    {
        var link = Assert.Single(LinkText.Split("www.example.com/a"));

        Assert.Equal("www.example.com/a", link.Text);
        Assert.Equal("https://www.example.com/a", link.Target);
    }

    [Fact]
    public void An_address_glued_to_a_word_or_an_email_stays_text()
    {
        var glued = Assert.Single(LinkText.Split("смотриhttps://example.com"));
        Assert.Null(glued.Target);

        var email = Assert.Single(LinkText.Split("a@b.ru и https://a"));
        Assert.Null(email.Target);
    }

    [Fact]
    public void Localhost_is_a_link()
    {
        var link = Assert.Single(LinkText.Split("http://localhost:8080/a"));

        Assert.Equal("http://localhost:8080/a", link.Target);
    }

    [Fact]
    public void Url_tag_uses_the_label_and_the_address()
    {
        var pieces = LinkText.Split("до [URL=https://example.com/x]сайт[/URL] после");

        Assert.Equal("до ", pieces[0].Text);
        Assert.Equal("сайт", pieces[1].Text);
        Assert.Equal("https://example.com/x", pieces[1].Target);
        Assert.Equal(" после", pieces[2].Text);
    }

    [Fact]
    public void Url_tag_without_a_label_shows_the_address()
    {
        var link = Assert.Single(LinkText.Split("[url]https://example.com/x[/url]"));

        Assert.Equal("https://example.com/x", link.Text);
        Assert.Equal("https://example.com/x", link.Target);
    }

    [Fact]
    public void A_script_address_is_not_a_link()
    {
        var piece = Assert.Single(LinkText.Split("[URL=javascript:alert(1)]нажми[/URL]"));

        Assert.Equal("нажми", piece.Text);
        Assert.Null(piece.Target);
    }

    [Fact]
    public void The_first_link_is_the_one_in_reading_order()
    {
        Assert.Equal(
            "https://first.example/a",
            LinkText.FirstTarget("см. https://first.example/a и https://second.example/b"));
        Assert.Equal(
            "https://visible.example/a",
            LinkText.FirstTarget("[code]https://code.example/a[/code]\nhttps://visible.example/a"));
        Assert.Null(LinkText.FirstTarget("без адреса"));
    }

    [Fact]
    public void Visible_text_drops_the_url_tags()
    {
        Assert.Equal(
            "one and https://b.com",
            LinkText.Visible("[URL=https://a.com]one[/URL] and [URL]https://b.com[/URL]"));
    }

    [Theory]
    [InlineData("[b]hi[/b]", false, "[b]hi[/b]")]
    [InlineData("[u]hi[/u]", false, "[u]hi[/u]")]
    [InlineData("[USER=1]Ann[/USER] hello", false, "[URL=https://portal.test/company/personal/user/1/]Ann[/URL] hello")]
    [InlineData("см. [URL=https://a.com]сайт[/URL]", false, "см. [URL=https://a.com]сайт[/URL]")]
    [InlineData("см. [URL=https://a.com]сайт[/URL]", true, "см. сайт")]
    [InlineData("[b][URL]https://a.com[/URL][/b]", false, "[b][URL]https://a.com[/URL][/b]")]
    [InlineData("[b][URL]https://a.com[/URL][/b]", true, "https://a.com")]
    [InlineData("[URL=https://a.com][/URL]", true, "https://a.com")]
    public void Cleaner_keeps_url_tags_in_the_message_and_drops_them_in_the_preview(string raw, bool singleLine, string expected)
    {
        Assert.Equal(expected, TextCleaner.ToPlain(raw, singleLine, "portal.test"));
    }

    [Fact]
    public void Cleaner_strips_basic_tags_in_the_preview()
    {
        Assert.Equal("hi", TextCleaner.ToPlain("[b]hi[/b]", singleLine: true, "portal.test"));
    }
}
