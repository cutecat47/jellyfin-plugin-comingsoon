using System;
using Jellyfin.Plugin.ComingSoon.Model;
using Jellyfin.Plugin.ComingSoon.Tracking;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

public class StatusTextTests
{
    // 09:00 UTC = 19:00 in Brisbane (UTC+10, no DST), so "today" ends 5 hours later.
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Brisbane = TimeZoneInfo.CreateCustomTimeZone("Test/UTC+10", TimeSpan.FromHours(10), "UTC+10", "UTC+10");

    [Theory]
    [InlineData(null, EtaBucket.Unknown)]
    [InlineData(0, EtaBucket.UnderAnHour)]
    [InlineData(59, EtaBucket.UnderAnHour)]
    [InlineData(60, EtaBucket.FewHours)]
    [InlineData(5 * 60 + 59, EtaBucket.FewHours)]
    [InlineData(6 * 60, EtaBucket.Tomorrow)]          // finishes 01:00 tomorrow local
    [InlineData(28 * 60, EtaBucket.Tomorrow)]         // 23:00 tomorrow
    [InlineData(30 * 60, EtaBucket.FewDays)]          // 01:00 the day after
    [InlineData(7 * 24 * 60, EtaBucket.OverAWeek)]
    public void EtaBuckets_InEvening(int? minutes, EtaBucket expected)
    {
        TimeSpan? left = minutes is null ? null : TimeSpan.FromMinutes(minutes.Value);
        Assert.Equal(expected, StatusText.BucketEta(left, Now, Brisbane));
    }

    [Fact]
    public void EtaBucket_Today_WhenItFinishesBeforeMidnight()
    {
        var morning = new DateTimeOffset(2026, 10, 1, 22, 0, 0, TimeSpan.Zero); // 08:00 local
        Assert.Equal(EtaBucket.Today, StatusText.BucketEta(TimeSpan.FromHours(8), morning, Brisbane));
    }

    [Fact]
    public void EtaBucket_NegativeIsUnknown()
        => Assert.Equal(EtaBucket.Unknown, StatusText.BucketEta(TimeSpan.FromMinutes(-5), Now, Brisbane));

    [Theory]
    [InlineData(62.0, 5, 60)]
    [InlineData(64.99, 5, 60)]
    [InlineData(65.0, 5, 65)]
    [InlineData(99.9, 5, 95)]
    [InlineData(100.0, 5, 100)]
    [InlineData(42.86, 10, 40)]
    [InlineData(42.86, 1, 42)]
    [InlineData(42.86, 0, 42)]     // invalid step is clamped to 1
    [InlineData(150.0, 5, 100)]
    [InlineData(-3.0, 5, 0)]
    public void StepPercent_FloorsToStep(double percent, int step, int expected)
        => Assert.Equal(expected, StatusText.StepPercent(percent, step));

    [Fact]
    public void StepPercent_NullAndNaN()
    {
        Assert.Null(StatusText.StepPercent(null, 5));
        Assert.Null(StatusText.StepPercent(double.NaN, 5));
    }

    [Fact]
    public void Overview_Texts()
    {
        Assert.Equal("Downloading — 60% — a few hours left", StatusText.Overview(new DisplayState(TrackedStatus.Downloading, 60, EtaBucket.FewHours, null, null)));
        Assert.Equal("Downloading — 40% — under an hour left (4 episodes)", StatusText.Overview(new DisplayState(TrackedStatus.Downloading, 40, EtaBucket.UnderAnHour, "4 episodes", null)));
        Assert.Equal("Stalled — 45% — The download is stalled with no connections", StatusText.Overview(new DisplayState(TrackedStatus.Stalled, 45, EtaBucket.Unknown, "The download is stalled with no connections", null)));
        Assert.Equal("Queued — waiting to start", StatusText.Overview(new DisplayState(TrackedStatus.Queued, null, EtaBucket.Unknown, null, null)));
        Assert.Equal("Queued — paused", StatusText.Overview(new DisplayState(TrackedStatus.Queued, null, EtaBucket.Unknown, "Paused", null)));
        Assert.Equal("Waiting for release — expected 5 Mar 2027", StatusText.Overview(new DisplayState(TrackedStatus.WaitingForRelease, null, EtaBucket.Unknown, null, new DateOnly(2027, 3, 5))));
        Assert.Equal("Waiting for release", StatusText.Overview(new DisplayState(TrackedStatus.WaitingForRelease, null, EtaBucket.Unknown, null, null)));
        Assert.Equal("Searching — looking for a release (3 of 8 episodes)", StatusText.Overview(new DisplayState(TrackedStatus.Searching, null, EtaBucket.Unknown, "3 of 8 episodes", null)));
        Assert.Equal("Importing — almost ready", StatusText.Overview(new DisplayState(TrackedStatus.Importing, null, EtaBucket.Unknown, "Waiting for library scan", null)));
        Assert.Equal("Downloading — time left unknown", StatusText.Overview(new DisplayState(TrackedStatus.Downloading, null, EtaBucket.Unknown, null, null)));
    }

    [Fact]
    public void PlaybackMessage_Texts()
    {
        Assert.Equal("Still downloading — a few hours left", StatusText.PlaybackMessage(new DisplayState(TrackedStatus.Downloading, 60, EtaBucket.FewHours, null, null)));
        Assert.Equal("Not released yet — expected 15 Jan 2027", StatusText.PlaybackMessage(new DisplayState(TrackedStatus.WaitingForRelease, null, EtaBucket.Unknown, null, new DateOnly(2027, 1, 15))));
    }

    [Fact]
    public void LocalDate_DateOnlyValuesKeepTheirDate_TimestampsAreConverted()
    {
        var newYork = TimeZoneInfo.CreateCustomTimeZone("Test/UTC-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");
        var dateOnly = new DateTimeOffset(2027, 3, 5, 0, 0, 0, TimeSpan.Zero);
        var airTime = new DateTimeOffset(2027, 1, 15, 2, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2027, 3, 5), StatusText.LocalDate(dateOnly, newYork));
        Assert.Equal(new DateOnly(2027, 1, 14), StatusText.LocalDate(airTime, newYork));
        Assert.Equal(new DateOnly(2027, 1, 15), StatusText.LocalDate(airTime, Brisbane));
    }

    [Fact]
    public void DisplayState_IgnoresInvisibleChanges()
    {
        var e1 = new TrackedEntry { Key = "k", Kind = MediaKind.Movie, Status = TrackedStatus.Downloading, Percent = 61.2, TimeLeft = TimeSpan.FromHours(2) };
        var e2 = e1 with { Percent = 63.9, TimeLeft = TimeSpan.FromHours(3) };
        var e3 = e1 with { Percent = 65.0 };

        Assert.Equal(StatusText.ToDisplayState(e1, 5, Now, Brisbane), StatusText.ToDisplayState(e2, 5, Now, Brisbane));
        Assert.NotEqual(StatusText.ToDisplayState(e1, 5, Now, Brisbane), StatusText.ToDisplayState(e3, 5, Now, Brisbane));
    }
}
