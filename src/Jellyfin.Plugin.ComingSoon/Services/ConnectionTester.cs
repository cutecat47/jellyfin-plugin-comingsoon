using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComingSoon.Api;
using Jellyfin.Plugin.ComingSoon.Clients;
using Jellyfin.Plugin.ComingSoon.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComingSoon.Services;

/// <summary>
/// Backs the config page's "Test connections" button: calls each service from the Jellyfin
/// server (so it tests the same network path the poller uses) and reports what it found.
/// </summary>
public sealed class ConnectionTester(IServiceClientFactory clients, ILogger<ConnectionTester> logger)
{
    public async Task<IReadOnlyList<ConnectionTestResult>> TestAsync(ConnectionTestRequest request, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(
            Run("Seerr", request.SeerrUrl, request.SeerrApiKey, TestSeerrAsync, cancellationToken),
            Run("Sonarr", request.SonarrUrl, request.SonarrApiKey, TestSonarrAsync, cancellationToken),
            Run("Radarr", request.RadarrUrl, request.RadarrApiKey, TestRadarrAsync, cancellationToken)).ConfigureAwait(false);

        foreach (var r in results)
        {
            logger.LogInformation("[ComingSoon] Test connection {Service:l}: {Outcome:l} - {Message:l}", r.Service, r.Success ? "OK" : "FAILED", r.Message);
        }

        return results;
    }

    private static async Task<ConnectionTestResult> Run(
        string name,
        string? url,
        string? key,
        Func<ServiceEndpoint, CancellationToken, Task<string>> test,
        CancellationToken cancellationToken)
    {
        var endpoint = ServiceEndpoint.TryCreate(name, url, key, out var error);
        if (endpoint is null)
        {
            return new ConnectionTestResult(name, false, error ?? "Not configured");
        }

        try
        {
            return new ConnectionTestResult(name, true, await test(endpoint, cancellationToken).ConfigureAwait(false));
        }
        catch (ServiceException ex)
        {
            return new ConnectionTestResult(name, false, ex.Message);
        }
    }

    private async Task<string> TestSeerrAsync(ServiceEndpoint endpoint, CancellationToken cancellationToken)
    {
        var client = clients.CreateSeerr(endpoint);
        var version = await client.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        await client.CheckAuthAsync(cancellationToken).ConfigureAwait(false);
        var requests = await client.GetActiveRequestsAsync(cancellationToken).ConfigureAwait(false);
        var movies = requests.Count(r => r.Kind == MediaKind.Movie);
        return $"Connected (Seerr {version ?? "?"}): {movies} movie and {requests.Count - movies} TV requests waiting";
    }

    private async Task<string> TestSonarrAsync(ServiceEndpoint endpoint, CancellationToken cancellationToken)
    {
        var client = clients.CreateSonarr(endpoint);
        var version = await client.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        var queue = await client.GetQueueAsync(cancellationToken).ConfigureAwait(false);
        var seasons = queue.Select(q => (q.TvdbId, q.TmdbId, q.SeasonNumber)).Distinct().Count();
        return $"Connected (Sonarr {version ?? "?"}): {queue.Count} queued episodes across {seasons} seasons";
    }

    private async Task<string> TestRadarrAsync(ServiceEndpoint endpoint, CancellationToken cancellationToken)
    {
        var client = clients.CreateRadarr(endpoint);
        var version = await client.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        var queue = await client.GetQueueAsync(cancellationToken).ConfigureAwait(false);
        return $"Connected (Radarr {version ?? "?"}): {queue.Count} queued downloads";
    }
}
