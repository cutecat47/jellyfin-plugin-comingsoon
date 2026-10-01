using System;

namespace Jellyfin.Plugin.ComingSoon.Tracking;

public enum RemovalDecision
{
    Keep,

    /// <summary>The real item is available: Seerr says so, or it exists in another Jellyfin library.</summary>
    RemoveArrived,

    /// <summary>Gone from every source (request declined/deleted, download removed) for longer than the grace period.</summary>
    RemoveAbandoned
}

/// <summary>Facts about one existing stub, gathered by the tracker.</summary>
public sealed record RemovalFacts
{
    public bool InQueue { get; init; }

    public bool InSeerr { get; init; }

    /// <summary>Gets whether Seerr reports the media available (null = unknown / couldn't ask).</summary>
    public bool? SeerrAvailable { get; init; }

    public bool InJellyfinLibrary { get; init; }

    /// <summary>Gets a value indicating whether every configured source answered on its latest poll.</summary>
    public bool AllSourcesHealthy { get; init; }

    /// <summary>Gets when the entry was first seen missing from all sources.</summary>
    public DateTimeOffset? MissingSince { get; init; }
}

/// <summary>
/// Removal rules: a stub goes once its entry has left the download queue AND the real item is
/// available. Entries that vanish from every source are dropped after a grace period, but only
/// while all sources are reachable, so an outage never wipes the library.
/// </summary>
public static class RemovalPolicy
{
    public static readonly TimeSpan AbandonGrace = TimeSpan.FromMinutes(10);

    public static RemovalDecision Decide(RemovalFacts facts, DateTimeOffset now)
    {
        if (facts.InQueue)
        {
            return RemovalDecision.Keep;
        }

        if (facts.SeerrAvailable == true || facts.InJellyfinLibrary)
        {
            return RemovalDecision.RemoveArrived;
        }

        if (facts.InSeerr)
        {
            return RemovalDecision.Keep;
        }

        if (facts.AllSourcesHealthy && facts.MissingSince is { } since && now - since >= AbandonGrace)
        {
            return RemovalDecision.RemoveAbandoned;
        }

        return RemovalDecision.Keep;
    }
}
