using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ComingSoon.Model;
using Jellyfin.Plugin.ComingSoon.Parsing;

namespace Jellyfin.Plugin.ComingSoon.Tracking;

/// <summary>Everything the merge needs, already fetched (the builder itself does no I/O).</summary>
public sealed record MergeInput
{
    public IReadOnlyList<QueueRecord> QueueRecords { get; init; } = [];

    public IReadOnlyList<SeerrRequest> SeerrRequests { get; init; } = [];

    /// <summary>Gets Seerr details keyed by (kind, tmdbId). Missing entries are fine.</summary>
    public IReadOnlyDictionary<(MediaKind Kind, int TmdbId), SeerrDetails> Details { get; init; } = new Dictionary<(MediaKind, int), SeerrDetails>();

    /// <summary>Gets Radarr movie info keyed by tmdbId.</summary>
    public IReadOnlyDictionary<int, MovieReleaseInfo> Movies { get; init; } = new Dictionary<int, MovieReleaseInfo>();

    /// <summary>Gets Sonarr series info keyed by tvdbId.</summary>
    public IReadOnlyDictionary<int, SeriesReleaseInfo> Series { get; init; } = new Dictionary<int, SeriesReleaseInfo>();

    public DateTimeOffset Now { get; init; }
}

/// <summary>
/// Merges queue groups (by tmdbId for movies, tvdbId - or tmdbId as a fallback - for series)
/// with Seerr requests into the list of "Coming Soon" entries.
/// </summary>
public static class EntryBuilder
{
    public const string PosterSize = "w780";
    public const string BackdropSize = "w1280";

    public static IReadOnlyList<TrackedEntry> Build(MergeInput input)
    {
        var groups = QueueAggregator.Aggregate(input.QueueRecords, input.Now).ToList();
        var used = new HashSet<QueueGroup>();
        var entries = new Dictionary<string, TrackedEntry>(StringComparer.Ordinal);

        foreach (var request in input.SeerrRequests)
        {
            input.Details.TryGetValue((request.Kind, request.TmdbId), out var details);

            if (request.Kind == MediaKind.Movie)
            {
                var group = groups.FirstOrDefault(g => g.Matches(MediaKind.Movie, request.TmdbId, null, null));
                var entry = group is not null
                    ? FromGroup(group, details, request)
                    : FromRequestOnly(request, details, null, input);
                Add(entries, entry, group, used);
                continue;
            }

            var tvdb = request.TvdbId ?? details?.TvdbId;
            foreach (var season in request.Seasons)
            {
                int? seasonStatus = details is null ? null : (request.Is4k ? details.SeasonStatus4k : details.SeasonStatus).GetValueOrDefault(season);
                var group = groups.FirstOrDefault(g => g.Matches(MediaKind.Series, request.TmdbId, tvdb, season));
                if (group is null && seasonStatus == SeerrMediaStatus.Available)
                {
                    continue; // This season has already arrived.
                }

                var entry = group is not null
                    ? FromGroup(group, details, request) with { TvdbId = group.TvdbId ?? tvdb }
                    : FromRequestOnly(request, details, season, input) with { TvdbId = tvdb };
                Add(entries, entry, group, used);
            }
        }

        // Downloads added straight in Radarr/Sonarr (no Seerr request) still count.
        foreach (var group in groups.Where(g => !used.Contains(g)))
        {
            var details = group.TmdbId is int tmdb && input.Details.TryGetValue((group.Kind, tmdb), out var d) ? d : null;
            Add(entries, FromGroup(group, details, null), group, used);
        }

        return entries.Values
            .Select(e => e.Kind == MediaKind.Series && e.TvdbId is int tvdb && e.SeasonNumber is int season
                && input.Series.TryGetValue(tvdb, out var info) && info.Seasons.TryGetValue(season, out var stats)
                    ? e with { AiredEpisodes = stats.EpisodeCount }
                    : e)
            .OrderByDescending(e => e.RequestedAt ?? DateTimeOffset.MinValue)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static void Add(Dictionary<string, TrackedEntry> entries, TrackedEntry entry, QueueGroup? group, HashSet<QueueGroup> used)
    {
        if (group is not null)
        {
            used.Add(group);
        }

        if (entries.TryGetValue(entry.Key, out var existing))
        {
            // Same title requested twice (e.g. HD and 4K): keep the most informative one.
            if (existing.InQueue && !entry.InQueue)
            {
                return;
            }

            entry = entry with
            {
                RequestedAt = Earliest(existing.RequestedAt, entry.RequestedAt),
                InSeerr = existing.InSeerr || entry.InSeerr,
            };
        }

        entries[entry.Key] = entry;
    }

    private static TrackedEntry FromGroup(QueueGroup group, SeerrDetails? details, SeerrRequest? request)
    {
        var key = group.Kind == MediaKind.Movie
            ? TrackedEntry.MovieKey(group.TmdbId ?? request?.TmdbId ?? 0)
            : TrackedEntry.SeasonKey(group.TvdbId ?? request?.TvdbId ?? details?.TvdbId, group.TmdbId ?? request?.TmdbId, group.SeasonNumber ?? 0);

        var detail = group.Detail;
        if (group.Kind == MediaKind.Series && detail is null && group.EpisodeCount > 1)
        {
            detail = string.Create(CultureInfo.InvariantCulture, $"{group.EpisodeCount} episodes");
        }

        return new TrackedEntry
        {
            Key = key,
            Kind = group.Kind,
            TmdbId = group.TmdbId ?? request?.TmdbId,
            TvdbId = group.TvdbId ?? request?.TvdbId,
            SeasonNumber = group.SeasonNumber,
            Title = details?.Title ?? group.Title ?? "Unknown",
            Year = details?.Year ?? group.Year,
            Status = group.Status,
            Percent = group.Percent,
            TimeLeft = group.TimeLeft,
            Detail = detail,
            ReleaseDate = group.ReleaseDate,
            RequestedAt = request?.CreatedAt ?? group.Added,
            PosterUrl = SeerrParser.TmdbImageUrl(details?.PosterPath, PosterSize) ?? group.PosterUrl,
            BackdropUrl = SeerrParser.TmdbImageUrl(details?.BackdropPath, BackdropSize) ?? group.BackdropUrl,
            InQueue = true,
            InSeerr = request is not null,
        };
    }

    private static TrackedEntry FromRequestOnly(SeerrRequest request, SeerrDetails? details, int? season, MergeInput input)
    {
        var tvdb = request.TvdbId ?? details?.TvdbId;
        var key = request.Kind == MediaKind.Movie
            ? TrackedEntry.MovieKey(request.TmdbId)
            : TrackedEntry.SeasonKey(tvdb, request.TmdbId, season ?? 0);

        var (status, detail, releaseDate) = request.Kind == MediaKind.Movie
            ? MovieReleaseStatus(request, details, input)
            : SeasonReleaseStatus(tvdb, season ?? 0, details, input);

        return new TrackedEntry
        {
            Key = key,
            Kind = request.Kind,
            TmdbId = request.TmdbId,
            TvdbId = tvdb,
            SeasonNumber = request.Kind == MediaKind.Series ? season : null,
            Title = details?.Title ?? "Unknown",
            Year = details?.Year,
            Status = status,
            Detail = detail,
            ReleaseDate = releaseDate,
            RequestedAt = request.CreatedAt,
            PosterUrl = SeerrParser.TmdbImageUrl(details?.PosterPath, PosterSize),
            BackdropUrl = SeerrParser.TmdbImageUrl(details?.BackdropPath, BackdropSize),
            InQueue = false,
            InSeerr = true,
        };
    }

    private static (TrackedStatus Status, string? Detail, DateTimeOffset? Release) MovieReleaseStatus(SeerrRequest request, SeerrDetails? details, MergeInput input)
    {
        if (input.Movies.TryGetValue(request.TmdbId, out var radarr) && radarr.InRadarr)
        {
            if (radarr.HasFile == true)
            {
                return (TrackedStatus.Importing, "Waiting for library scan", null);
            }

            if (radarr.IsAvailable == false)
            {
                return (TrackedStatus.WaitingForRelease, null, radarr.NextReleaseDate);
            }

            return (TrackedStatus.Searching, null, null);
        }

        if (details?.ReleaseDate is { } release && release > input.Now)
        {
            return (TrackedStatus.WaitingForRelease, null, release);
        }

        return (TrackedStatus.Searching, null, null);
    }

    private static (TrackedStatus Status, string? Detail, DateTimeOffset? Release) SeasonReleaseStatus(int? tvdb, int season, SeerrDetails? details, MergeInput input)
    {
        if (tvdb is int id && input.Series.TryGetValue(id, out var sonarr) && sonarr.InSonarr
            && sonarr.Seasons.TryGetValue(season, out var s))
        {
            var progress = s.EpisodeCount > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{s.EpisodeFileCount} of {Math.Max(s.EpisodeCount, s.TotalEpisodeCount)} episodes")
                : null;

            if (s.PreviousAiring is null)
            {
                return (TrackedStatus.WaitingForRelease, null, s.NextAiring);
            }

            if (s.EpisodeCount > 0 && s.EpisodeFileCount >= s.EpisodeCount)
            {
                // Everything aired is downloaded: either more episodes are coming, or Seerr hasn't caught up yet.
                return s.TotalEpisodeCount > s.EpisodeCount || s.NextAiring is not null
                    ? (TrackedStatus.WaitingForRelease, progress, s.NextAiring)
                    : (TrackedStatus.Importing, "Waiting for library scan", null);
            }

            return (TrackedStatus.Searching, progress, null);
        }

        if (details?.SeasonAirDates.TryGetValue(season, out var air) == true && air > input.Now)
        {
            return (TrackedStatus.WaitingForRelease, null, air);
        }

        return (TrackedStatus.Searching, null, null);
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : (a < b ? a : b);
}
