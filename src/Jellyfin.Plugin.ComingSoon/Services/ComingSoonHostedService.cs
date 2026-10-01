using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComingSoon.Clients;
using Jellyfin.Plugin.ComingSoon.Configuration;
using Jellyfin.Plugin.ComingSoon.Tracking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComingSoon.Services;

/// <summary>
/// Background poller: runs the tracker at the configured interval.
/// This build only tracks and logs; writing stubs into the library comes next.
/// </summary>
public class ComingSoonHostedService : BackgroundService
{
    private readonly ILogger<ComingSoonHostedService> _logger;
    private readonly ComingSoonTracker _tracker;

    public ComingSoonHostedService(IServiceClientFactory clients, ILogger<ComingSoonHostedService> logger)
    {
        _logger = logger;
        _tracker = new ComingSoonTracker(clients, logger, TimeProvider.System);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ComingSoon] Poller started");
        var warnedUnconfigured = false;

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
                        await _tracker.PollAsync(config, null, stoppingToken).ConfigureAwait(false);
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
}
