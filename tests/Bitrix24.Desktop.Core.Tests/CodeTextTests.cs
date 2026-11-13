using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class CodeTextTests
{
    [Fact]
    public void Feed_keeps_the_code_block_and_the_text_around_it()
    {
        var raw = "причем код может быть очень длинным\n[code]\nunsigned int generate_next_lorem_hash()\n{\n    return 1;\n}\n[/code]";

        var feed = TextCleaner.ToPlain(raw, singleLine: false);

        Assert.Equal("причем код может быть очень длинным\n[code]unsigned int generate_next_lorem_hash()\n{\n    return 1;\n}[/code]", feed);
    }

    [Fact]
    public void Preview_keeps_the_code_text_without_the_tags()
    {
        var raw = "причем код может быть очень длинным\n[CODE]\nunsigned int generate_next_lorem_hash()\n{\n    return 1;\n}\n[/CODE]";

        var preview = TextCleaner.ToPlain(raw, singleLine: true);

        Assert.DoesNotContain("[code]", preview, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("причем код может быть очень длинным", preview);
        Assert.Contains("unsigned int generate_next_lorem_hash()", preview);
        Assert.Contains("return 1;", preview);
    }

    [Fact]
    public void Tags_inside_code_stay_literal_and_tags_outside_are_cleaned()
    {
        var raw = "[color=#fff]привет[/color] [code][USER=5]admin[/USER][/code]";

        var feed = TextCleaner.ToPlain(raw, singleLine: false, "portal.test");

        Assert.Equal("привет [code][USER=5]admin[/USER][/code]", feed);
    }
}
