using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ComingSoon.Model;

namespace Jellyfin.Plugin.ComingSoon.Parsing;

/// <summary>One page of GET /api/v1/request.</summary>
public sealed record SeerrRequestPage(IReadOnlyList<SeerrRequest> Requests, int Pages, int Skipped);

/// <summary>
/// Parses Seerr (Overseerr/Jellyseerr successor) API v1 responses.
/// </summary>
public static class SeerrParser
{
    private const int RequestStatusApproved = 2;

    public static SeerrRequestPage ParseRequests(JsonElement root)
    {
        var requests = new List<SeerrRequest>();
        var skipped = 0;
        var pages = root.Obj("pageInfo")?.Int("pages") ?? 1;

        foreach (var r in root.Array("results"))
        {
            SeerrRequest? request = null;
            try
            {
                request = ParseRequest(r);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
            {
                // Defensive: skip anything with an unexpected shape.
            }

            if (request is null)
            {
                skipped++;
            }
            else
            {
                requests.Add(request);
            }
        }

        return new SeerrRequestPage(requests, pages, skipped);
    }

    public static SeerrDetails? ParseMovieDetails(JsonElement root)
    {
        var tmdb = root.Id("id");
        if (tmdb is null)
        {
            return null;
        }

        var release = root.Date("releaseDate");
        var media = root.Obj("mediaInfo");
        return new SeerrDetails
        {
            Kind = MediaKind.Movie,
            TmdbId = tmdb.Value,
            Title = root.Str("title") ?? root.Str("originalTitle"),
            Year = release?.Year,
            PosterPath = root.Str("posterPath"),
            BackdropPath = root.Str("backdropPath"),
            ReleaseDate = release,
            MediaStatus = media?.Int("status"),
            MediaStatus4k = media?.Int("status4k"),
        };
    }

    public static SeerrDetails? ParseTvDetails(JsonElement root)
    {
        var tmdb = root.Id("id");
        if (tmdb is null)
        {
            return null;
        }

        var firstAir = root.Date("firstAirDate");
        var media = root.Obj("mediaInfo");

        var airDates = new Dictionary<int, DateTimeOffset>();
        foreach (var s in root.Array("seasons"))
        {
            var n = s.Int("seasonNumber");
            var air = s.Date("airDate");
            if (n is not null && air is not null)
            {
                airDates[n.Value] = air.Value;
            }
        }

        var status = new Dictionary<int, int>();
        var status4k = new Dictionary<int, int>();
        if (media is not null)
        {
            foreach (var s in media.Value.Array("seasons"))
            {
                var n = s.Int("seasonNumber");
                if (n is null)
                {
                    continue;
                }

                if (s.Int("status") is int st)
                {
                    status[n.Value] = st;
                }

                if (s.Int("status4k") is int st4k)
                {
                    status4k[n.Value] = st4k;
                }
            }
        }

        return new SeerrDetails
        {
            Kind = MediaKind.Series,
            TmdbId = tmdb.Value,
            TvdbId = root.Obj("externalIds")?.Id("tvdbId") ?? media?.Id("tvdbId"),
            Title = root.Str("name") ?? root.Str("originalName"),
            Year = firstAir?.Year,
            PosterPath = root.Str("posterPath"),
            BackdropPath = root.Str("backdropPath"),
            ReleaseDate = firstAir,
            MediaStatus = media?.Int("status"),
            MediaStatus4k = media?.Int("status4k"),
            SeasonStatus = status,
            SeasonStatus4k = status4k,
            SeasonAirDates = airDates,
        };
    }

    /// <summary>Parses GET /api/v1/status and returns the version string.</summary>
    public static string? ParseVersion(JsonElement root) => root.Str("version");

    /// <summary>Turns a TMDB image path ("/abc.jpg") into a full URL.</summary>
    public static string? TmdbImageUrl(string? path, string size)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return "https://image.tmdb.org/t/p/" + size + (path.StartsWith('/') ? path : "/" + path);
    }

    private static SeerrRequest? ParseRequest(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var status = r.Int("status");
        if (status is not null && status != RequestStatusApproved)
        {
            return null;
        }

        var media = r.Obj("media");
        var tmdb = media?.Id("tmdbId");
        if (tmdb is null)
        {
            return null;
        }

        var type = r.Str("type") ?? media!.Value.Str("mediaType");
        MediaKind kind;
        if (string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase))
        {
            kind = MediaKind.Movie;
        }
        else if (string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase))
        {
            kind = MediaKind.Series;
        }
        else
        {
            return null;
        }

        var is4k = r.Bool("is4k") ?? false;

        // Already available: nothing to wait for.
        var mediaStatus = is4k ? media!.Value.Int("status4k") : media!.Value.Int("status");
        if (mediaStatus == SeerrMediaStatus.Available)
        {
            return null;
        }

        var seasons = r.Array("seasons")
            .Select(s => s.Int("seasonNumber"))
            .Where(n => n is not null && n >= 0)
            .Select(n => n!.Value)
            .Distinct()
            .Order()
            .ToList();

        if (kind == MediaKind.Series && seasons.Count == 0)
        {
            return null;
        }

        return new SeerrRequest
        {
            RequestId = r.Int("id") ?? 0,
            Kind = kind,
            TmdbId = tmdb.Value,
            TvdbId = media.Value.Id("tvdbId"),
            Is4k = is4k,
            CreatedAt = r.Date("createdAt"),
            Seasons = seasons,
        };
    }
}
