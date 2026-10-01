using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComingSoon.Model;
using Jellyfin.Plugin.ComingSoon.Parsing;

namespace Jellyfin.Plugin.ComingSoon.Clients;

public interface IRadarrClient
{
    Task<IReadOnlyList<QueueRecord>> GetQueueAsync(CancellationToken cancellationToken);

    Task<MovieReleaseInfo> GetMovieAsync(int tmdbId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<string?> GetVersionAsync(CancellationToken cancellationToken);
}

public interface ISonarrClient
{
    Task<IReadOnlyList<QueueRecord>> GetQueueAsync(CancellationToken cancellationToken);

    Task<SeriesReleaseInfo> GetSeriesAsync(int tvdbId, CancellationToken cancellationToken);

    Task<string?> GetVersionAsync(CancellationToken cancellationToken);
}

public interface ISeerrClient
{
    Task<IReadOnlyList<SeerrRequest>> GetActiveRequestsAsync(CancellationToken cancellationToken);

    Task<SeerrDetails?> GetDetailsAsync(MediaKind kind, int tmdbId, CancellationToken cancellationToken);

    Task<string?> GetVersionAsync(CancellationToken cancellationToken);

    /// <summary>Checks the API key (GET /status is public, so it can't prove the key works).</summary>
    Task CheckAuthAsync(CancellationToken cancellationToken);
}

/// <summary>Radarr v5/v6, API v3.</summary>
public sealed class RadarrClient(HttpClient http, ServiceEndpoint endpoint) : IRadarrClient
{
    public Task<IReadOnlyList<QueueRecord>> GetQueueAsync(CancellationToken cancellationToken)
        => ArrQueue.FetchAllAsync(
            http,
            endpoint,
            "api/v3/queue?includeMovie=true&includeUnknownMovieItems=false",
            ArrParser.ParseRadarrQueue,
            cancellationToken);

    public async Task<MovieReleaseInfo> GetMovieAsync(int tmdbId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var doc = await ServiceHttp.GetJsonAsync(http, endpoint, "api/v3/movie?tmdbId=" + tmdbId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        return ArrParser.ParseMovieLookup(doc.RootElement, now);
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        using var doc = await ServiceHttp.GetJsonAsync(http, endpoint, "api/v3/system/status", cancellationToken).ConfigureAwait(false);
        return ArrParser.ParseVersion(doc.RootElement);
    }
}

/// <summary>Sonarr v4, API v3.</summary>
public sealed class SonarrClient(HttpClient http, ServiceEndpoint endpoint) : ISonarrClient
{
    public Task<IReadOnlyList<QueueRecord>> GetQueueAsync(CancellationToken cancellationToken)
        => ArrQueue.FetchAllAsync(
            http,
            endpoint,
            "api/v3/queue?includeSeries=true&includeEpisode=true&includeUnknownSeriesItems=false",
            ArrParser.ParseSonarrQueue,
            cancellationToken);

    public async Task<SeriesReleaseInfo> GetSeriesAsync(int tvdbId, CancellationToken cancellationToken)
    {
        using var doc = await ServiceHttp.GetJsonAsync(http, endpoint, "api/v3/series?tvdbId=" + tvdbId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        return ArrParser.ParseSeriesLookup(doc.RootElement);
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        using var doc = await ServiceHttp.GetJsonAsync(http, endpoint, "api/v3/system/status", cancellationToken).ConfigureAwait(false);
        return ArrParser.ParseVersion(doc.RootElement);
    }
}

/// <summary>Seerr, API v1.</summary>
public sealed class SeerrClient(HttpClient http, ServiceEndpoint endpoint) : ISeerrClient
{
    private const int PageSize = 100;
    private const int MaxPages = 20;

    public async Task<IReadOnlyList<SeerrRequest>> GetActiveRequestsAsync(CancellationToken cancellationToken)
    {
        // filter=processing: approved requests whose media isn't available yet.
        var all = new List<SeerrRequest>();
        for (var page = 0; page < MaxPages; page++)
        {
            var skip = (page * PageSize).ToString(CultureInfo.InvariantCulture);
            using var doc = await ServiceHttp.GetJsonAsync(
                http,
                endpoint,
                $"api/v1/request?take={PageSize}&skip={skip}&filter=processing&sortDirection=desc",
                cancellationToken).ConfigureAwait(false);

            var parsed = SeerrParser.ParseRequests(doc.RootElement);
            all.AddRange(parsed.Requests);
            if (page + 1 >= parsed.Pages || parsed.Requests.Count + parsed.Skipped == 0)
            {
                break;
            }
        }

        return all;
    }

    public async Task<SeerrDetails?> GetDetailsAsync(MediaKind kind, int tmdbId, CancellationToken cancellationToken)
    {
        var path = (kind == MediaKind.Movie ? "api/v1/movie/" : "api/v1/tv/") + tmdbId.ToString(CultureInfo.InvariantCulture);
        using var doc = await ServiceHttp.GetJsonAsync(http, endpoint, path, cancellationToken).ConfigureAwait(false);
        return kind == MediaKind.Movie
            ? SeerrParser.ParseMovieDetails(doc.RootElement)
            : SeerrParser.ParseTvDetails(doc.RootElement);
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        using var doc = await ServiceHttp.GetJsonAsync(http, endpoint, "api/v1/status", cancellationToken).ConfigureAwait(false);
        return SeerrParser.ParseVersion(doc.RootElement);
    }

    public async Task CheckAuthAsync(CancellationToken cancellationToken)
    {
        using var doc = await ServiceHttp.GetJsonAsync(http, endpoint, "api/v1/request?take=1", cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Pages through a Radarr/Sonarr queue (the API defaults to 10 records per page).</summary>
internal static class ArrQueue
{
    private const int PageSize = 200;
    private const int MaxPages = 25;

    public static async Task<IReadOnlyList<QueueRecord>> FetchAllAsync(
        HttpClient http,
        ServiceEndpoint endpoint,
        string pathAndQuery,
        Func<System.Text.Json.JsonElement, QueuePage> parse,
        CancellationToken cancellationToken)
    {
        var all = new List<QueueRecord>();
        var seen = 0;
        for (var page = 1; page <= MaxPages; page++)
        {
            var url = string.Create(CultureInfo.InvariantCulture, $"{pathAndQuery}&page={page}&pageSize={PageSize}");
            using var doc = await ServiceHttp.GetJsonAsync(http, endpoint, url, cancellationToken).ConfigureAwait(false);
            var parsed = parse(doc.RootElement);
            all.AddRange(parsed.Records);

            var onPage = parsed.Records.Count + parsed.Skipped;
            seen += onPage;
            if (onPage == 0 || seen >= parsed.TotalRecords)
            {
                break;
            }
        }

        return all;
    }
}
