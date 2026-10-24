using Walis.Core.Scheduling;

namespace Walis.Tests;

public class CronExpressionTests
{
    [Theory]
    [InlineData("* * * * *")]
    [InlineData("30 2 * * *")]
    [InlineData("*/15 * * * *")]
    [InlineData("0 18 * * 5")]
    [InlineData("0 0 1 * *")]
    [InlineData("0 9-17/2 * * 1-5")]
    public void ValidExpressionParses(string expression)
    {
        Assert.True(CronExpression.TryParse(expression, out var cron, out var error));
        Assert.NotNull(cron);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("99 * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("a * * * *")]
    [InlineData("*/0 * * * *")]
    public void InvalidExpressionReportsError(string? expression)
    {
        Assert.False(CronExpression.TryParse(expression, out var cron, out var error));
        Assert.Null(cron);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void DailyScheduleMatchesOnlyGivenMinute()
    {
        var cron = CronExpression.Parse("30 2 * * *");

        Assert.True(cron.Matches(new DateTime(2026, 9, 1, 2, 30, 0)));
        Assert.False(cron.Matches(new DateTime(2026, 9, 1, 2, 31, 0)));
        Assert.False(cron.Matches(new DateTime(2026, 9, 1, 3, 30, 0)));
    }

    [Fact]
    public void StepExpressionCoversWholeRange()
    {
        var cron = CronExpression.Parse("*/15 * * * *");

        Assert.True(cron.Matches(new DateTime(2026, 9, 1, 8, 0, 0)));
        Assert.True(cron.Matches(new DateTime(2026, 9, 1, 8, 45, 0)));
        Assert.False(cron.Matches(new DateTime(2026, 9, 1, 8, 20, 0)));
    }

    [Fact]
    public void WeekdaysAreZeroToSixAndSevenIsSunday()
    {
        var sunday = new DateTime(2026, 9, 6, 10, 0, 0);
        Assert.Equal(DayOfWeek.Sunday, sunday.DayOfWeek);

        Assert.True(CronExpression.Parse("0 10 * * 0").Matches(sunday));
        Assert.True(CronExpression.Parse("0 10 * * 7").Matches(sunday));
        Assert.False(CronExpression.Parse("0 10 * * 1").Matches(sunday));
    }

    [Fact]
    public void DayAndWeekdayRestrictionsAreUnioned()
    {
        // The 1st of every month or every Friday
        var cron = CronExpression.Parse("0 0 1 * 5");

        Assert.True(cron.Matches(new DateTime(2026, 9, 1, 0, 0, 0))); // Tuesday, but the 1st
        Assert.True(cron.Matches(new DateTime(2026, 9, 4, 0, 0, 0))); // Friday
        Assert.False(cron.Matches(new DateTime(2026, 9, 3, 0, 0, 0))); // Thursday and not the 1st
    }

    [Fact]
    public void NextOccurrenceExcludesStartTime()
    {
        var cron = CronExpression.Parse("30 2 * * *");
        var next = cron.GetNextOccurrence(new DateTime(2026, 9, 1, 2, 30, 0));

        Assert.Equal(new DateTime(2026, 9, 2, 2, 30, 0), next);
    }

    [Fact]
    public void NextOccurrenceCrossesIntoNextDay()
    {
        var cron = CronExpression.Parse("0 9 * * *");
        var next = cron.GetNextOccurrence(new DateTime(2026, 9, 1, 18, 5, 0));

        Assert.Equal(new DateTime(2026, 9, 2, 9, 0, 0), next);
    }

    [Fact]
    public void NextFridayOccurrenceFallsOnCorrectDate()
    {
        var cron = CronExpression.Parse("0 18 * * 5");
        var next = cron.GetNextOccurrence(new DateTime(2026, 9, 1, 12, 0, 0));

        Assert.Equal(new DateTime(2026, 9, 4, 18, 0, 0), next);
        Assert.Equal(DayOfWeek.Friday, next!.Value.DayOfWeek);
    }
}