using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ComingSoon.Model;

public enum MediaKind
{
    Movie,
    Series
}

/// <summary>
/// One Radarr or Sonarr queue record, normalised. Sonarr emits one record per episode, and every
/// episode of a season pack carries the whole download's Size/SizeLeft (same DownloadId).
/// </summary>
public sealed record QueueRecord
{
    public required MediaKind Kind { get; init; }

    public int RecordId { get; init; }

    public int? TmdbId { get; init; }

    public int? TvdbId { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public string? Title { get; init; }

    public int? Year { get; init; }

    public string? DownloadId { get; init; }

    public double Size { get; init; }

    public double SizeLeft { get; init; }

    public TimeSpan? TimeLeft { get; init; }

    public DateTimeOffset? EstimatedCompletion { get; init; }

    public DateTimeOffset? Added { get; init; }

    /// <summary>Gets the raw queue status ("downloading", "queued", "paused", "warning", ...), lower-case.</summary>
    public string Status { get; init; } = "unknown";

    /// <summary>Gets the raw tracked download state ("downloading", "importPending", ...), lower-case.</summary>
    public string? TrackedState { get; init; }

    /// <summary>Gets the raw tracked download status ("ok", "warning", "error"), lower-case.</summary>
    public string? TrackedStatus { get; init; }

    public string? Message { get; init; }

    public string? PosterUrl { get; init; }

    public string? BackdropUrl { get; init; }

    /// <summary>Gets a value indicating whether the movie/episode already has a file (a quality upgrade, not a new arrival).</summary>
    public bool IsUpgrade { get; init; }

    /// <summary>Gets the episode air date (Sonarr) or the movie's digital/physical release date (Radarr).</summary>
    public DateTimeOffset? ReleaseDate { get; init; }

    /// <summary>Gets Radarr's "isAvailable" for the movie (null for series or when missing).</summary>
    public bool? MovieAvailable { get; init; }

    /// <summary>
    /// Whether the media isn't out yet. Grabs for unaired episodes are almost always fakes that
    /// never import, so these show as "Waiting for release" rather than "Importing".
    /// </summary>
    public bool IsUnreleased(DateTimeOffset now) => Kind == MediaKind.Series
        ? ReleaseDate > now
        : MovieAvailable == false;

    /// <summary>Gets a key identifying the underlying download, so season packs are counted once.</summary>
    public string DownloadKey => string.IsNullOrEmpty(DownloadId) ? "record:" + RecordId : DownloadId!;
}

/// <summary>An approved, not-yet-available Seerr request.</summary>
public sealed record SeerrRequest
{
    public int RequestId { get; init; }

    public required MediaKind Kind { get; init; }

    public int TmdbId { get; init; }

    public int? TvdbId { get; init; }

    public bool Is4k { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Gets requested season numbers (series only).</summary>
    public IReadOnlyList<int> Seasons { get; init; } = [];
}

/// <summary>Seerr's movie/tv details: title, artwork and availability.</summary>
public sealed record SeerrDetails
{
    public required MediaKind Kind { get; init; }

    public int TmdbId { get; init; }

    public int? TvdbId { get; init; }

    public string? Title { get; init; }

    public int? Year { get; init; }

    public string? PosterPath { get; init; }

    public string? BackdropPath { get; init; }

    public DateTimeOffset? ReleaseDate { get; init; }

    /// <summary>Gets Seerr's media status (5 = available), or null when Seerr has no media record.</summary>
    public int? MediaStatus { get; init; }

    public int? MediaStatus4k { get; init; }

    /// <summary>Gets per-season availability from mediaInfo.seasons (series only).</summary>
    public IReadOnlyDictionary<int, int> SeasonStatus { get; init; } = new Dictionary<int, int>();

    public IReadOnlyDictionary<int, int> SeasonStatus4k { get; init; } = new Dictionary<int, int>();

    /// <summary>Gets TMDB season air dates (series only).</summary>
    public IReadOnlyDictionary<int, DateTimeOffset> SeasonAirDates { get; init; } = new Dictionary<int, DateTimeOffset>();
}

/// <summary>Radarr's view of whether a movie can be grabbed yet.</summary>
public sealed record MovieReleaseInfo(bool InRadarr, bool? IsAvailable, bool? HasFile, DateTimeOffset? NextReleaseDate, bool? Monitored = null);

/// <summary>Sonarr's per-season statistics.</summary>
public sealed record SeasonReleaseInfo(
    int SeasonNumber,
    DateTimeOffset? PreviousAiring,
    DateTimeOffset? NextAiring,
    int EpisodeFileCount,
    int EpisodeCount,
    int TotalEpisodeCount,
    bool? Monitored = null);

/// <summary>Sonarr's view of a series.</summary>
public sealed record SeriesReleaseInfo(bool InSonarr, IReadOnlyDictionary<int, SeasonReleaseInfo> Seasons, bool? Monitored = null);

/// <summary>Seerr media status values (server/constants/media.ts).</summary>
public static class SeerrMediaStatus
{
    public const int Unknown = 1;
    public const int Pending = 2;
    public const int Processing = 3;
    public const int PartiallyAvailable = 4;
    public const int Available = 5;
    public const int Blocklisted = 6;
    public const int Deleted = 7;
}
