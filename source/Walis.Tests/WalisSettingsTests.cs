using Walis.Core.Models;

namespace Walis.Tests;

public class WalisSettingsTests
{
    [Fact]
    public void QuietHoursAcrossMidnightCoverBothEnds()
    {
        var settings = new WalisSettings { QuietHoursStart = "23:00", QuietHoursEnd = "08:00" };

        Assert.True(settings.IsQuietHour(new TimeOnly(23, 30)));
        Assert.True(settings.IsQuietHour(new TimeOnly(3, 0)));
        Assert.False(settings.IsQuietHour(new TimeOnly(8, 0)));
        Assert.False(settings.IsQuietHour(new TimeOnly(12, 0)));
    }

    [Fact]
    public void SameDayQuietHoursCoverOnlyTheInterval()
    {
        var settings = new WalisSettings { QuietHoursStart = "12:00", QuietHoursEnd = "14:00" };

        Assert.True(settings.IsQuietHour(new TimeOnly(13, 0)));
        Assert.False(settings.IsQuietHour(new TimeOnly(11, 59)));
        Assert.False(settings.IsQuietHour(new TimeOnly(14, 0)));
    }

    [Fact]
    public void MalformedQuietHoursAreTreatedAsUnset()
    {
        var settings = new WalisSettings { QuietHoursStart = "不是時間", QuietHoursEnd = "08:00" };

        Assert.False(settings.IsQuietHour(new TimeOnly(2, 0)));
    }

    [Fact]
    public void NotificationEventSwitchesMapToSettings()
    {
        var settings = new WalisSettings { NotifyOnSuccess = false, NotifyOnFailure = true };

        Assert.True(settings.IsEventEnabled(NotificationEventKind.BuildFailed));
        Assert.False(settings.IsEventEnabled(NotificationEventKind.BuildSucceeded));
        Assert.False(settings.IsEventEnabled("不存在的事件"));
    }
}