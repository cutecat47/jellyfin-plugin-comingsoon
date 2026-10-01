using System;
using System.Globalization;
using Jellyfin.Plugin.ComingSoon.Model;

namespace Jellyfin.Plugin.ComingSoon.Tracking;

/// <summary>The status buckets shown to users.</summary>
public enum TrackedStatus
{
    Searching,
    WaitingForRelease,
    Queued,
    Downloading,
    Stalled,
    Importing
}

/// <summary>
/// One "Coming Soon" item: a movie, or one season of a series.
/// </summary>
public sealed record TrackedEntry
{
    /// <summary>Gets the stable identity, e.g. "movie-tmdb603692" or "tv-tvdb81189-s02". Also used for stub file names.</summary>
    public required string Key { get; init; }

    public required MediaKind Kind { get; init; }

    public int? TmdbId { get; init; }

    public int? TvdbId { get; init; }

    public int? SeasonNumber { get; init; }

    public string Title { get; init; } = "Unknown";

    public int? Year { get; init; }

    public TrackedStatus Status { get; init; }

    /// <summary>Gets download progress 0-100, when known.</summary>
    public double? Percent { get; init; }

    public TimeSpan? TimeLeft { get; init; }

    /// <summary>Gets extra context: a warning message, "paused", "3 of 10 episodes", ...</summary>
    public string? Detail { get; init; }

    /// <summary>Gets the expected release/air date (Waiting for release).</summary>
    public DateTimeOffset? ReleaseDate { get; init; }

    public DateTimeOffset? RequestedAt { get; init; }

    public string? PosterUrl { get; init; }

    public string? BackdropUrl { get; init; }

    /// <summary>Gets how many episodes of the season have aired, per Sonarr (series only; null when unknown).</summary>
    public int? AiredEpisodes { get; init; }

    /// <summary>
    /// Gets a value indicating whether the entry is requested in Seerr but Radarr/Sonarr won't download it:
    /// the movie/series isn't there any more, or it (or the season) isn't monitored.
    /// </summary>
    public bool NotInArr { get; init; }

    /// <summary>Gets a value indicating whether the entry is in the Radarr/Sonarr queue.</summary>
    public bool InQueue { get; init; }

    /// <summary>Gets a value indicating whether the entry has an active Seerr request.</summary>
    public bool InSeerr { get; init; }

    /// <summary>Gets the display name: "Title" or "Title - Season 2".</summary>
    public string DisplayName => Kind == MediaKind.Series && SeasonNumber is int s
        ? string.Create(CultureInfo.InvariantCulture, $"{Title} - {(s == 0 ? "Specials" : "Season " + s)}")
        : Title;

    public static string MovieKey(int tmdbId) => string.Create(CultureInfo.InvariantCulture, $"movie-tmdb{tmdbId}");

    public static string SeasonKey(int? tvdbId, int? tmdbId, int season)
        => tvdbId is int tvdb
            ? string.Create(CultureInfo.InvariantCulture, $"tv-tvdb{tvdb}-s{season:00}")
            : string.Create(CultureInfo.InvariantCulture, $"tv-tmdb{tmdbId ?? 0}-s{season:00}");
}
