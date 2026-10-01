using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ComingSoon.Model;

namespace Jellyfin.Plugin.ComingSoon.Parsing;

/// <summary>One page of a Radarr/Sonarr queue.</summary>
public sealed record QueuePage(IReadOnlyList<QueueRecord> Records, int TotalRecords, int Skipped);

/// <summary>
/// Parses Radarr v5/v6 and Sonarr v4 API v3 responses (QueueResource, MovieResource, SeriesResource).
/// </summary>
public static class ArrParser
{
    public static QueuePage ParseRadarrQueue(JsonElement root) => ParseQueue(root, ParseRadarrRecord);

    public static QueuePage ParseSonarrQueue(JsonElement root) => ParseQueue(root, ParseSonarrRecord);

    /// <summary>Parses GET /api/v3/movie?tmdbId=X (an array with zero or one movie).</summary>
    public static MovieReleaseInfo ParseMovieLookup(JsonElement root, DateTimeOffset now)
    {
        var movie = FirstOrSelf(root);
        if (movie is null)
        {
            return new MovieReleaseInfo(false, null, null, null);
        }

        // A downloadable (digital/physical) release is what matters; cinema date is the fallback.
        var m = movie.Value;
        var next = new[] { m.Date("digitalRelease"), m.Date("physicalRelease") }.Where(d => d > now).Min()
            ?? new[] { m.Date("inCinemas") }.Where(d => d > now).Min();

        return new MovieReleaseInfo(true, m.Bool("isAvailable"), m.Bool("hasFile"), next);
    }

    /// <summary>Parses GET /api/v3/series?tvdbId=X (an array with zero or one series).</summary>
    public static SeriesReleaseInfo ParseSeriesLookup(JsonElement root)
    {
        var series = FirstOrSelf(root);
        if (series is null)
        {
            return new SeriesReleaseInfo(false, new Dictionary<int, SeasonReleaseInfo>());
        }

        var seasons = new Dictionary<int, SeasonReleaseInfo>();
        foreach (var s in series.Value.Array("seasons"))
        {
            var number = s.Int("seasonNumber");
            if (number is null)
            {
                continue;
            }

            var stats = s.Obj("statistics");
            seasons[number.Value] = new SeasonReleaseInfo(
                number.Value,
                stats?.Date("previousAiring"),
                stats?.Date("nextAiring"),
                stats?.Int("episodeFileCount") ?? 0,
                stats?.Int("episodeCount") ?? 0,
                stats?.Int("totalEpisodeCount") ?? 0);
        }

        return new SeriesReleaseInfo(true, seasons);
    }

    /// <summary>Parses GET /api/v3/system/status and returns the version string.</summary>
    public static string? ParseVersion(JsonElement root) => root.Str("version");

    private static QueuePage ParseQueue(JsonElement root, Func<JsonElement, QueueRecord?> parseRecord)
    {
        IEnumerable<JsonElement> items;
        int? total = null;

        if (root.ValueKind == JsonValueKind.Array)
        {
            items = root.EnumerateArray();
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            items = root.Array("records");
            total = root.Int("totalRecords");
        }
        else
        {
            items = [];
        }

        var records = new List<QueueRecord>();
        var skipped = 0;
        var seen = 0;
        foreach (var item in items)
        {
            seen++;
            QueueRecord? record = null;
            try
            {
                record = parseRecord(item);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
            {
                // Defensive: treat any unexpected shape as a skipped record.
            }

            if (record is null)
            {
                skipped++;
            }
            else
            {
                records.Add(record);
            }
        }

        return new QueuePage(records, total ?? seen, skipped);
    }

    private static QueueRecord? ParseRadarrRecord(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var movie = r.Obj("movie");
        var tmdb = movie?.Id("tmdbId");
        if (tmdb is null)
        {
            // Unknown item (not matched to a movie) or includeMovie missing: can't be placed.
            return null;
        }

        return BaseRecord(r, MediaKind.Movie) with
        {
            TmdbId = tmdb,
            Title = movie!.Value.Str("title"),
            Year = movie.Value.Id("year"),
            PosterUrl = RemoteImage(movie.Value, "poster"),
            BackdropUrl = RemoteImage(movie.Value, "fanart"),
            IsUpgrade = movie.Value.Bool("hasFile") == true,
            MovieAvailable = movie.Value.Bool("isAvailable"),
            ReleaseDate = new[] { movie.Value.Date("digitalRelease"), movie.Value.Date("physicalRelease") }.Min()
                ?? movie.Value.Date("inCinemas"),
        };
    }

    private static QueueRecord? ParseSonarrRecord(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var series = r.Obj("series");
        var episode = r.Obj("episode");
        var tvdb = series?.Id("tvdbId");
        var tmdb = series?.Id("tmdbId");
        var season = r.Int("seasonNumber") ?? episode?.Int("seasonNumber");
        if ((tvdb is null && tmdb is null) || season is null || season < 0)
        {
            return null;
        }

        return BaseRecord(r, MediaKind.Series) with
        {
            TvdbId = tvdb,
            TmdbId = tmdb,
            SeasonNumber = season,
            EpisodeNumber = episode?.Int("episodeNumber"),
            Title = series!.Value.Str("title"),
            Year = series.Value.Id("year"),
            PosterUrl = RemoteImage(series.Value, "poster"),
            BackdropUrl = RemoteImage(series.Value, "fanart"),
            IsUpgrade = (r.Bool("episodeHasFile") ?? episode?.Bool("hasFile")) == true,
            ReleaseDate = episode?.Date("airDateUtc"),
        };
    }

    private static QueueRecord BaseRecord(JsonElement r, MediaKind kind)
    {
        var size = Math.Max(0, r.Num("size") ?? 0);
        var left = Math.Clamp(r.Num("sizeleft") ?? size, 0, size);

        return new QueueRecord
        {
            Kind = kind,
            RecordId = r.Int("id") ?? 0,
            DownloadId = r.Str("downloadId"),
            Size = size,
            SizeLeft = left,
            TimeLeft = r.Duration("timeleft"),
            EstimatedCompletion = r.Date("estimatedCompletionTime"),
            Added = r.Date("added"),
            Status = Lower(r.Str("status")) ?? "unknown",
            TrackedState = Lower(r.Str("trackedDownloadState")),
            TrackedStatus = Lower(r.Str("trackedDownloadStatus")),
            Message = FirstMessage(r),
        };
    }

    private static string? FirstMessage(JsonElement r)
    {
        var error = r.Str("errorMessage");
        if (!string.IsNullOrWhiteSpace(error))
        {
            return error;
        }

        // statusMessages: [{ title: file name or summary, messages: [reasons] }]. Reasons are the useful part.
        foreach (var sm in r.Array("statusMessages"))
        {
            foreach (var m in sm.Array("messages"))
            {
                if (m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString()))
                {
                    return m.GetString();
                }
            }
        }

        return r.Array("statusMessages").Select(sm => sm.Str("title")).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
    }

    private static string? RemoteImage(JsonElement media, string coverType)
    {
        foreach (var img in media.Array("images"))
        {
            if (!string.Equals(img.Str("coverType"), coverType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // "url" is relative to the *arr server (/MediaCover/...), so prefer the public remoteUrl.
            var remote = img.Str("remoteUrl");
            if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http"))
            {
                return remote;
            }
        }

        return null;
    }

    private static JsonElement? FirstOrSelf(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    return item;
                }
            }

            return null;
        }

        return root.ValueKind == JsonValueKind.Object ? root : null;
    }

#pragma warning disable CA1308 // Lower-case is the canonical form of these *arr enum strings.
    private static string? Lower(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant();
#pragma warning restore CA1308
}
