using Bitrix24.Desktop.Core;

namespace Bitrix24.Desktop.Core.Tests;

public class PresenceTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Online_is_a_single_word()
    {
        Assert.Equal("Онлайн", PresenceText.Format(true, Now.AddMinutes(-2), "M", Now));
    }

    [Fact]
    public void Fresh_online_status_counts_as_online()
    {
        Assert.True(PresenceText.IsCurrent("online", null, Now.AddMinutes(-2), Now));
        Assert.True(PresenceText.IsCurrent("online", null, null, Now));
    }

    [Fact]
    public void Stale_online_flag_uses_the_last_activity()
    {
        Assert.False(PresenceText.IsCurrent("online", null, Now.AddDays(-11), Now));
        Assert.False(PresenceText.IsCurrent("online", Now.AddMinutes(-1), Now.AddMinutes(-1), Now));
        Assert.False(PresenceText.IsCurrent("away", null, Now.AddMinutes(-1), Now));
    }

    [Theory]
    [InlineData(0, "был 1 минуту назад")]
    [InlineData(1, "был 1 минуту назад")]
    [InlineData(2, "был 2 минуты назад")]
    [InlineData(5, "был 5 минут назад")]
    [InlineData(11, "был 11 минут назад")]
    [InlineData(21, "был 21 минуту назад")]
    [InlineData(22, "был 22 минуты назад")]
    public void Minutes_use_the_right_word(int minutes, string expected)
    {
        Assert.Equal(expected, PresenceText.Format(false, Now.AddMinutes(-minutes), "M", Now));
    }

    [Theory]
    [InlineData(1, "был 1 час назад")]
    [InlineData(3, "был 3 часа назад")]
    [InlineData(5, "был 5 часов назад")]
    [InlineData(23, "был 23 часа назад")]
    public void Hours_use_the_right_word(int hours, string expected)
    {
        Assert.Equal(expected, PresenceText.Format(false, Now.AddHours(-hours), "M", Now));
    }

    [Theory]
    [InlineData(1, "был 1 день назад")]
    [InlineData(2, "был 2 дня назад")]
    [InlineData(5, "был 5 дней назад")]
    [InlineData(11, "был 11 дней назад")]
    [InlineData(21, "был 21 день назад")]
    public void Days_use_the_right_word(int days, string expected)
    {
        Assert.Equal(expected, PresenceText.Format(false, Now.AddDays(-days), "M", Now));
    }

    [Theory]
    [InlineData(30, "был 1 месяц назад")]
    [InlineData(60, "был 2 месяца назад")]
    [InlineData(150, "был 5 месяцев назад")]
    [InlineData(330, "был 11 месяцев назад")]
    public void Months_use_the_right_word(int days, string expected)
    {
        Assert.Equal(expected, PresenceText.Format(false, Now.AddDays(-days), "M", Now));
    }

    [Fact]
    public void Woman_uses_the_feminine_verb()
    {
        Assert.Equal("была 5 минут назад", PresenceText.Format(false, Now.AddMinutes(-5), "F", Now));
    }

    [Fact]
    public void Missing_activity_stays_blank()
    {
        Assert.Equal("", PresenceText.Format(false, null, "M", Now));
    }
}
