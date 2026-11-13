using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class OpenGraphTests
{
    [Fact]
    public void Title_description_and_image_are_read()
    {
        var html = """
            <html><head>
            <meta content="Заголовок &amp; Co" property="og:title">
            <meta property="og:description" content="Первая&#10;строка">
            <meta property="og:image" content="/pic.jpg">
            </head></html>
            """;

        var card = OpenGraph.Read(html, new Uri("https://example.com/a/b"));

        Assert.NotNull(card);
        Assert.Equal("Заголовок & Co", card.Title);
        Assert.Equal("Первая строка", card.Description);
        Assert.Equal("https://example.com/pic.jpg", card.ImageUrl);
    }

    [Fact]
    public void A_page_title_is_enough_when_open_graph_has_none()
    {
        var card = OpenGraph.Read("<html><head><title>  Страница  </title></head></html>", new Uri("https://example.com/"));

        Assert.NotNull(card);
        Assert.Equal("Страница", card.Title);
        Assert.Equal("", card.Description);
        Assert.Null(card.ImageUrl);
    }

    [Fact]
    public void Without_a_title_there_is_no_card()
    {
        var card = OpenGraph.Read("<html><head><meta property=\"og:description\" content=\"только текст\"></head></html>", new Uri("https://example.com/"));

        Assert.Null(card);
    }

    [Fact]
    public void A_protocol_relative_image_keeps_https()
    {
        var html = """
            <meta property="og:title" content="Имя">
            <meta property="og:image" content="//cdn.example/a.png">
            """;

        var card = OpenGraph.Read(html, new Uri("https://example.com/x"));

        Assert.NotNull(card);
        Assert.Equal("https://cdn.example/a.png", card.ImageUrl);
    }
}
