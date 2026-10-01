using System;
using Jellyfin.Plugin.ComingSoon.Tracking;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

public class ThrottleAndRemovalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static DisplayState State(int percent) => new(TrackedStatus.Downloading, percent, EtaBucket.FewHours, null, null);

    [Fact]
    public void Throttle_FirstPushAlwaysAllowed()
        => Assert.True(new UpdateThrottle().ShouldPush("a", State(10), T0));

    [Fact]
    public void Throttle_UnchangedStateIsNeverPushed()
    {
        var t = new UpdateThrottle();
        t.MarkPushed("a", State(10), T0);
        Assert.False(t.ShouldPush("a", State(10), T0.AddMinutes(30)));
    }

    [Fact]
    public void Throttle_ChangeWithin60s_IsHeldThenPushed()
    {
        var t = new UpdateThrottle();
        t.MarkPushed("a", State(10), T0);

        Assert.False(t.ShouldPush("a", State(15), T0.AddSeconds(5)));
        Assert.False(t.ShouldPush("a", State(20), T0.AddSeconds(59)));
        Assert.True(t.ShouldPush("a", State(20), T0.AddSeconds(60)));
    }

    [Fact]
    public void Throttle_ChangeThatRevertsIsNotPushed()
    {
        var t = new UpdateThrottle();
        t.MarkPushed("a", State(10), T0);
        Assert.False(t.ShouldPush("a", State(10), T0.AddMinutes(2))); // went 10 -> 15 -> 10 between polls
    }

    [Fact]
    public void Throttle_IsPerItem_AndForgettable()
    {
        var t = new UpdateThrottle();
        t.MarkPushed("a", State(10), T0);
        Assert.True(t.ShouldPush("b", State(15), T0.AddSeconds(1)));

        t.Forget("a");
        Assert.True(t.ShouldPush("a", State(15), T0.AddSeconds(1)));
    }

    public static TheoryData<RemovalFacts, int, RemovalDecision> RemovalCases => new()
    {
        // Still downloading: always keep, even if Seerr/Jellyfin say available (e.g. a re-grab).
        { new RemovalFacts { InQueue = true, SeerrAvailable = true, InJellyfinLibrary = true, AllSourcesHealthy = true }, 0, RemovalDecision.Keep },
        // Left the queue and arrived.
        { new RemovalFacts { SeerrAvailable = true, AllSourcesHealthy = true, MissingSince = T0 }, 0, RemovalDecision.RemoveArrived },
        { new RemovalFacts { InSeerr = true, InJellyfinLibrary = true, AllSourcesHealthy = true }, 0, RemovalDecision.RemoveArrived },
        // Left the queue, Seerr still waiting for its library sync: keep.
        { new RemovalFacts { InSeerr = true, SeerrAvailable = false, AllSourcesHealthy = true }, 60, RemovalDecision.Keep },
        // Gone everywhere: keep during grace, remove after.
        { new RemovalFacts { AllSourcesHealthy = true, MissingSince = T0 }, 9, RemovalDecision.Keep },
        { new RemovalFacts { AllSourcesHealthy = true, MissingSince = T0 }, 10, RemovalDecision.RemoveAbandoned },
        // Gone, but a source is down: never treat as abandoned.
        { new RemovalFacts { AllSourcesHealthy = false, MissingSince = T0 }, 600, RemovalDecision.Keep },
        // Availability unknown (Seerr not configured / lookup failed): grace rule still applies.
        { new RemovalFacts { SeerrAvailable = null, AllSourcesHealthy = true, MissingSince = T0 }, 11, RemovalDecision.RemoveAbandoned },
    };

    [Theory]
    [MemberData(nameof(RemovalCases))]
    public void Removal(RemovalFacts facts, int minutesLater, RemovalDecision expected)
        => Assert.Equal(expected, RemovalPolicy.Decide(facts, T0.AddMinutes(minutesLater)));
}
