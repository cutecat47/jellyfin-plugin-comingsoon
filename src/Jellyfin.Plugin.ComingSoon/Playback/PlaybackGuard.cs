using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComingSoon.Playback;

/// <summary>
/// Fallback for clients that start playback without asking PlaybackInfo (or ignore its error):
/// stop the session, show the status message, and wipe the watch state so the stub never appears in
/// Continue Watching or as played.
/// </summary>
public sealed class PlaybackGuard(
    ISessionManager sessionManager,
    PlaybackBlocker blocker,
    ILogger<PlaybackGuard> logger) : IHostedService
{
    /// <summary>Some clients ignore a Stop sent while the player is still starting up.</summary>
    public static readonly TimeSpan StopDelay = TimeSpan.FromMilliseconds(1500);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        sessionManager.PlaybackStart += OnPlaybackStart;
        sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        sessionManager.PlaybackStart -= OnPlaybackStart;
        sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is null || e.Session is null || !blocker.Registry.IsStubPath(e.Item.Path))
        {
            return;
        }

        var item = e.Item;
        var session = e.Session;
        var users = e.Users?.ToArray() ?? [];
        logger.LogInformation("[ComingSoon] Playback of '{Name:l}' started on {Client:l}; stopping it", item.Name, session.Client);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StopDelay).ConfigureAwait(false);
                await blocker.StopAsync(session, CancellationToken.None).ConfigureAwait(false);
                await blocker.SendMessageAsync(session, item, CancellationToken.None).ConfigureAwait(false);

                // Clear again once the client has reported its final position.
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                blocker.ClearUserData(item, users);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[ComingSoon] Stopping playback of '{Name:l}' failed", item.Name);
            }
        });
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Item is null || !blocker.Registry.IsStubPath(e.Item.Path))
        {
            return;
        }

        try
        {
            blocker.ClearUserData(e.Item, e.Users?.ToArray() ?? []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "[ComingSoon] Clearing watch state for '{Name:l}' failed", e.Item.Name);
        }
    }
}
