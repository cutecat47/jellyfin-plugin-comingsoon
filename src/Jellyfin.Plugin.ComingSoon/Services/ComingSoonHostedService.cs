using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComingSoon.Clients;
using Jellyfin.Plugin.ComingSoon.Configuration;
using Jellyfin.Plugin.ComingSoon.Library;
using Jellyfin.Plugin.ComingSoon.Tracking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComingSoon.Services;

/// <summary>
/// Background poller: tracker (Radarr/Sonarr/Seerr) -> stub sync (folder, library items, metadata).
/// </summary>
public class ComingSoonHostedService : BackgroundService
{
    private readonly ILogger<ComingSoonHostedService> _logger;
    private readonly ComingSoonTracker _tracker;
    private readonly StubSync _sync;

    public ComingSoonHostedService(
        IServiceClientFactory clients,
        IStubLibrary library,
        StubRegistry registry,
        ILogger<ComingSoonHostedService> logger)
    {
        _logger = logger;
        _tracker = new ComingSoonTracker(clients, logger, TimeProvider.System);
        _sync = new StubSync(library, registry, logger, TimeProvider.System);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ComingSoon] Poller started");
        var warnedUnconfigured = false;

        // Let Jellyfin finish starting up (library manager, plugins) before touching the library.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
                var interval = Math.Clamp(config.PollIntervalSeconds, 1, 3600);

                if (IsUnconfigured(config))
                {
                    if (!warnedUnconfigured)
                    {
                        _logger.LogInformation("[ComingSoon] No services configured yet - set them up on the plugin's settings page");
                        warnedUnconfigured = true;
                    }
                }
                else
                {
                    warnedUnconfigured = false;
                    try
                    {
                        await TickAsync(config, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Never let one bad poll kill the loop.
                        _logger.LogError(ex, "[ComingSoon] Poll failed unexpectedly; will retry in {Interval}s", interval);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("[ComingSoon] Poller stopped");
    }

    private static bool IsUnconfigured(PluginConfiguration c)
        => string.IsNullOrWhiteSpace(c.SeerrUrl) && string.IsNullOrWhiteSpace(c.SonarrUrl) && string.IsNullOrWhiteSpace(c.RadarrUrl);

    private async Task TickAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (!_sync.IsInitializedFor(config.StubFolderPath))
        {
            var seeds = _sync.Initialize(config);
            _tracker.Seed(seeds, TimeProvider.System.LocalTimeZone, config.PercentStep);
        }

        await _sync.EnsureLibraryAsync(config, cancellationToken).ConfigureAwait(false);
        var snapshot = await _tracker.PollAsync(config, _sync.ExistsOutsideStubs, cancellationToken).ConfigureAwait(false);
        await _sync.ApplyAsync(snapshot, config, cancellationToken).ConfigureAwait(false);
    }
}
