using Walis.Core.Database;

namespace Walis.Tests;

public class DatabaseClockTests
{
    [Fact]
    public void StringWithZParsesAsUtcNotLocal()
    {
        var value = DatabaseClock.ToUtc("2026-09-05T13:02:30.625Z");
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(new DateTime(2026, 9, 5, 13, 2, 30, 625, DateTimeKind.Utc), value);
    }

    [Fact]
    public void StringWithoutZoneIsTreatedAsUtc()
    {
        var value = DatabaseClock.ToUtc("2026-09-05 13:02:30");
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(13, value.Hour);
    }

    [Fact]
    public void LocalTimeIsConvertedToUtcAndOthersOnlyMarkedUtc()
    {
        var local = new DateTime(2026, 9, 5, 21, 0, 0, DateTimeKind.Local);
        Assert.Equal(local.ToUniversalTime(), DatabaseClock.ToUtc(local));

        var unspecified = new DateTime(2026, 9, 5, 13, 0, 0, DateTimeKind.Unspecified);
        var normalized = DatabaseClock.ToUtc(unspecified);
        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        Assert.Equal(13, normalized.Hour);

        var offset = new DateTimeOffset(2026, 9, 5, 21, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal(13, DatabaseClock.ToUtc(offset).Hour);
    }
}