using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class QuoteTextTests
{
    private const string Bar = "------------------------------------------------------";

    [Fact]
    public void Composed_quote_matches_the_messenger_and_parses_back()
    {
        var today = new DateTime(2026, 11, 11);
        var yesterday = new DateTimeOffset(2026, 11, 10, 10, 50, 0, TimeSpan.FromHours(3));
        var older = new DateTimeOffset(2026, 10, 22, 13, 47, 0, TimeSpan.FromHours(3));
        var lastYear = new DateTimeOffset(2025, 10, 22, 13, 47, 0, TimeSpan.FromHours(3));

        var when = QuoteText.QuoteWhen(yesterday);
        var admin = QuoteText.Compose("admin", yesterday, "1", 520, 3000415, "замер");
        Assert.Equal(Bar + "\nadmin [" + when + "] #1:520/3000415\nзамер\n" + Bar + "\n", admin);
        Assert.Equal("вчера, 10:50", QuoteText.QuoteWhen(yesterday, today));
        Assert.Equal("22 октября, 13:47", QuoteText.QuoteWhen(older, today));
        Assert.Equal("22 октября 2025, 13:47", QuoteText.QuoteWhen(lastYear, today));
        Assert.Equal("#632:520/3000926", QuoteText.QuoteMark("632", 520, 3000926));
        Assert.Equal("#chat175/2987329", QuoteText.QuoteMark("chat175", 520, 2987329));

        var group = QuoteText.Compose("Андрей Винокуров", older, "chat175", 520, 2987329, "второй");
        var quote = Assert.IsType<MessageQuote>(Assert.Single(QuoteText.Parse(group.TrimEnd('\n'))));
        Assert.Equal("Андрей Винокуров", quote.Author);
        Assert.Equal(QuoteText.QuoteWhen(older), quote.When);
        Assert.Equal("второй", quote.Body);
    }

    [Fact]
    public void Bitrix_quote_keeps_the_author_the_time_and_the_reply()
    {
        var text = Bar + "\nАндрей Винокуров [сегодня, 14:20] #chat175/2987145\nи там и там nginx отдает 403\n" + Bar + "\nу меня на homm.store такая же ситуация";

        var pieces = QuoteText.Parse(text);

        var quote = Assert.IsType<MessageQuote>(pieces[0]);
        Assert.Equal("Андрей Винокуров", quote.Author);
        Assert.Equal("сегодня, 14:20", quote.When);
        Assert.Equal("и там и там nginx отдает 403", quote.Body);
        var reply = Assert.IsType<MessageText>(pieces[1]);
        Assert.Equal("у меня на homm.store такая же ситуация", reply.Text);
        Assert.Equal("[Цитата] у меня на homm.store такая же ситуация", QuoteText.Flatten(text));
    }

    [Fact]
    public void A_line_of_equals_is_the_same_kind_of_quote()
    {
        var bar = new string('=', 20);
        var pieces = QuoteText.Parse(bar + "\nИмя [12:47]\nтекст\n" + bar + "\nответ");

        var quote = Assert.IsType<MessageQuote>(pieces[0]);
        Assert.Equal("Имя", quote.Author);
        Assert.Equal("12:47", quote.When);
        Assert.Equal("текст", quote.Body);
        Assert.Equal("ответ", Assert.IsType<MessageText>(pieces[1]).Text);
    }

    [Fact]
    public void A_quote_already_folded_into_one_line_becomes_a_mark()
    {
        var folded = Bar + " Константин Уренев [сегодня, 12:04] #1:520/3000415 текст цитаты " + Bar + " я поправил";
        var only = Bar + " Константин Уренев [сегодня, 12:04] #chat175/2987145 текст цитаты " + Bar;
        var around = "смотри " + new string('=', 20) + " Имя [12:47] текст " + new string('=', 20) + " ответ";

        Assert.Equal("[Цитата] я поправил", QuoteText.Flatten(folded));
        Assert.Equal("[Цитата]", QuoteText.Flatten(only));
        Assert.Equal("смотри [Цитата] ответ", QuoteText.Flatten(around));
    }

    [Fact]
    public void A_short_line_of_dashes_stays_text()
    {
        var pieces = QuoteText.Parse("смотри ----\nдальше");

        var text = Assert.IsType<MessageText>(Assert.Single(pieces));
        Assert.Equal("смотри ----\nдальше", text.Text);
    }

    [Fact]
    public void A_code_block_stays_code_and_keeps_its_lines()
    {
        var text = "причем код может быть очень длинным\n[code]\nunsigned int generate_next_lorem_hash()\n{\n    return 1;\n}\n[/code]";

        var pieces = QuoteText.Parse(text);

        Assert.Equal("причем код может быть очень длинным", Assert.IsType<MessageText>(pieces[0]).Text);
        Assert.Equal("unsigned int generate_next_lorem_hash()\n{\n    return 1;\n}", Assert.IsType<MessageCode>(pieces[1]).Text);
        Assert.Equal("причем код может быть очень длинным unsigned int generate_next_lorem_hash() {     return 1; }", QuoteText.Flatten(text));
    }

    [Fact]
    public void Dashes_inside_code_do_not_become_a_quote()
    {
        var text = "[code]\n" + Bar + "\nint x;\n" + Bar + "\n[/code]";

        var code = Assert.IsType<MessageCode>(Assert.Single(QuoteText.Parse(text)));

        Assert.Contains(Bar, code.Text);
        Assert.Contains("int x;", code.Text);
    }

    [Fact]
    public void Code_inside_a_quote_stays_in_the_quote()
    {
        var text = Bar + "\nИмя [12:47]\nсмотри\n[code]\nint x;\n[/code]\n" + Bar;

        var quote = Assert.IsType<MessageQuote>(Assert.Single(QuoteText.Parse(text)));

        Assert.Equal("Имя", quote.Author);
        Assert.Contains("[code]int x;[/code]", quote.Body);
        Assert.Equal("[Цитата]", QuoteText.Flatten(text));
    }
}
