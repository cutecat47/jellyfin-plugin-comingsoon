using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ComingSoon.Model;

namespace Jellyfin.Plugin.ComingSoon.Tracking;

/// <summary>Queue records for one movie or one series season, aggregated.</summary>
public sealed record QueueGroup
{
    public required MediaKind Kind { get; init; }

    public int? TmdbId { get; init; }

    public int? TvdbId { get; init; }

    public int? SeasonNumber { get; init; }

    public string? Title { get; init; }

    public int? Year { get; init; }

    public string? PosterUrl { get; init; }

    public string? BackdropUrl { get; init; }

    public TrackedStatus Status { get; init; }

    public double? Percent { get; init; }

    public TimeSpan? TimeLeft { get; init; }

    public string? Detail { get; init; }

    public DateTimeOffset? Added { get; init; }

    /// <summary>Gets the earliest upcoming air/release date (Waiting for release groups).</summary>
    public DateTimeOffset? ReleaseDate { get; init; }

    public int EpisodeCount { get; init; }

    public bool Matches(MediaKind kind, int? tmdbId, int? tvdbId, int? season)
    {
        if (kind != Kind || season != SeasonNumber)
        {
            return false;
        }

        return (tvdbId is not null && tvdbId == TvdbId) || (tmdbId is not null && tmdbId == TmdbId);
    }
}

/// <summary>
/// Groups Radarr/Sonarr queue records per movie and per series+season, and works out the
/// group's status bucket, progress and time left.
/// </summary>
public static partial class QueueAggregator
{
    public static IReadOnlyList<QueueGroup> Aggregate(IEnumerable<QueueRecord> records, DateTimeOffset now)
    {
        return records
            .Where(r => !r.IsUpgrade)
            .GroupBy(r => r.Kind == MediaKind.Movie
                ? (r.Kind, Id: "tmdb" + r.TmdbId, Season: -1)
                : (r.Kind, Id: r.TvdbId is not null ? "tvdb" + r.TvdbId : "tmdb" + r.TmdbId, Season: r.SeasonNumber ?? -1))
            .Select(g => BuildGroup(g.ToList(), now))
            .ToList();
    }

    /// <summary>
    /// A finished download that contains nothing importable - typically a fake (.exe/.lnk) release whose
    /// files the download client refused. It will never import, so viewers should see "Searching".
    /// </summary>
    public static bool IsDeadGrab(QueueRecord r)
    {
        if (Classify(r) != TrackedStatus.Stalled)
        {
            return false;
        }

        var finished = r.Status == "completed" || r.TrackedState is "importpending" or "importblocked";
        return (finished && r.Size <= 0)
            || (r.Message?.StartsWith("No files found are eligible for import", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Removes server paths from a Sonarr/Radarr message before it's shown to viewers.</summary>
    public static string? SanitizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var cleaned = PathPattern().Replace(message, string.Empty).Trim().TrimEnd(':', ',', '.').Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*(\b(in|at|from|to)\s+)?['""\[]?(/|[A-Za-z]:\\)\S.*$")]
    private static partial System.Text.RegularExpressions.Regex PathPattern();

    public static TrackedStatus Classify(QueueRecord r)
    {
        if (r.TrackedState == "imported")
        {
            return TrackedStatus.Importing; // done; leaves the queue shortly
        }

        // A warning wins over the import state: Sonarr/Radarr leave stuck imports as
        // "importPending" + warning (e.g. "No files found are eligible for import").
        if (r.TrackedStatus is "warning" or "error")
        {
            return TrackedStatus.Stalled;
        }

        switch (r.TrackedState)
        {
            case "importpending":
            case "importing":
                return TrackedStatus.Importing;
            case "importblocked":
            case "failedpending":
            case "failed":
                return TrackedStatus.Stalled;
        }

        return r.Status switch
        {
            "downloading" => TrackedStatus.Downloading,
            "completed" => TrackedStatus.Importing,
            "failed" or "warning" or "downloadclientunavailable" => TrackedStatus.Stalled,
            _ => TrackedStatus.Queued, // queued, paused, delay, fallback, unknown
        };
    }

    private static QueueGroup BuildGroup(List<QueueRecord> all, DateTimeOffset now)
    {
        var first = all[0];

        // Grabs for unaired episodes / unreleased movies are almost always fakes that never import,
        // and "dead" grabs (no usable files) will never import either. If that's all there is, the item
        // is waiting for release or still searching; otherwise only the real downloads count.
        var records = all.Where(r => !r.IsUnreleased(now) && !IsDeadGrab(r)).ToList();
        if (records.Count == 0)
        {
            var upcoming = all.Select(r => r.ReleaseDate).Where(d => d > now).Min();
            return Identity(all, first) with
            {
                Status = all.Any(r => r.IsUnreleased(now)) ? TrackedStatus.WaitingForRelease : TrackedStatus.Searching,
                ReleaseDate = upcoming,
            };
        }

        // Season packs: every episode record repeats the pack's size, so count each download once.
        var downloads = records.GroupBy(r => r.DownloadKey).Select(g => g.First()).ToList();
        var size = downloads.Sum(d => d.Size);
        var left = downloads.Sum(d => d.SizeLeft);

        var classified = records.Select(r => (Record: r, Status: Classify(r))).ToList();
        var status = Pick(classified.Select(c => c.Status));

        double? percent = status == TrackedStatus.Importing
            ? 100
            : size > 0 ? Math.Clamp((size - left) / size * 100, 0, 100) : null;

        TimeSpan? timeLeft = null;
        foreach (var d in downloads.Where(d => Classify(d) == TrackedStatus.Downloading))
        {
            var t = d.TimeLeft ?? (d.EstimatedCompletion is { } eta && eta > now ? eta - now : null);
            if (t is not null && (timeLeft is null || t > timeLeft))
            {
                timeLeft = t;
            }
        }

        return Identity(all, first) with
        {
            Status = status,
            Percent = percent,
            TimeLeft = status == TrackedStatus.Downloading ? timeLeft : null,
            Detail = DetailFor(status, classified),
            EpisodeCount = first.Kind == MediaKind.Series
                ? records.Select(r => r.EpisodeNumber ?? -r.RecordId).Distinct().Count()
                : 0,
        };
    }

    private static QueueGroup Identity(List<QueueRecord> records, QueueRecord first) => new()
    {
        Kind = first.Kind,
        TmdbId = records.Select(r => r.TmdbId).FirstOrDefault(i => i is not null),
        TvdbId = records.Select(r => r.TvdbId).FirstOrDefault(i => i is not null),
        SeasonNumber = first.Kind == MediaKind.Series ? first.SeasonNumber : null,
        Title = records.Select(r => r.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
        Year = records.Select(r => r.Year).FirstOrDefault(y => y is not null),
        PosterUrl = records.Select(r => r.PosterUrl).FirstOrDefault(u => u is not null),
        BackdropUrl = records.Select(r => r.BackdropUrl).FirstOrDefault(u => u is not null),
        Added = records.Select(r => r.Added).Where(a => a is not null).Min(),
    };

    /// <summary>Group priority: anything actively downloading wins, then problems, then waiting, then importing.</summary>
    private static TrackedStatus Pick(IEnumerable<TrackedStatus> statuses)
    {
        var set = statuses.ToHashSet();
        foreach (var s in new[] { TrackedStatus.Downloading, TrackedStatus.Stalled, TrackedStatus.Queued, TrackedStatus.Importing })
        {
            if (set.Contains(s))
            {
                return s;
            }
        }

        return TrackedStatus.Queued;
    }

    private static string? DetailFor(TrackedStatus status, List<(QueueRecord Record, TrackedStatus Status)> classified)
    {
        var matching = classified.Where(c => c.Status == status).Select(c => c.Record).ToList();
        switch (status)
        {
            case TrackedStatus.Stalled:
                var message = matching.Select(r => r.Message).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
                if (message is null && matching.Any(r => r.Status == "downloadclientunavailable"))
                {
                    message = "Download client unavailable";
                }

                return Trim(SanitizeMessage(message));
            case TrackedStatus.Queued:
                if (matching.Count > 0 && matching.All(r => r.Status == "paused"))
                {
                    return "Paused";
                }

                if (matching.Count > 0 && matching.All(r => r.Status == "delay"))
                {
                    return "Delayed by profile";
                }

                return null;
            default:
                return null;
        }
    }

    private static string? Trim(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        s = s.Trim();
        return s.Length <= 120 ? s : string.Concat(s.AsSpan(0, 117), "...");
    }
}
