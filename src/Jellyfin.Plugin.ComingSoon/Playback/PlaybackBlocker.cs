using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ComingSoon.Library;
using Jellyfin.Plugin.ComingSoon.Tracking;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComingSoon.Playback;

/// <summary>Shared actions for refusing playback of stubs: on-screen message, stop, and user-data cleanup.</summary>
public sealed class PlaybackBlocker(
    StubRegistry registry,
    ISessionManager sessionManager,
    IUserDataManager userDataManager,
    ILogger<PlaybackBlocker> logger)
{
    public const string MessageHeader = "Coming Soon";
    public const long MessageTimeoutMs = 8000;

    public StubRegistry Registry => registry;

    /// <summary>Text for the on-screen message, e.g. "Still downloading — a few hours left".</summary>
    public string MessageFor(BaseItem item)
    {
        var info = registry.Find(item.Path);
        return info is null ? "Not available yet — still downloading" : StatusText.PlaybackMessage(info.State);
    }

    public async Task SendMessageAsync(SessionInfo session, BaseItem item, CancellationToken cancellationToken)
    {
        var text = MessageFor(item);
        try
        {
            await sessionManager.SendMessageCommand(
                null!,
                session.Id,
                new MessageCommand { Header = MessageHeader, Text = text, TimeoutMs = MessageTimeoutMs },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Some clients don't accept remote messages; the refusal itself still works.
            logger.LogInformation("[ComingSoon] Couldn't show message on {Client:l}: {Error:l}", session.Client, ex.Message);
        }
    }

    public async Task StopAsync(SessionInfo session, CancellationToken cancellationToken)
    {
        try
        {
            await sessionManager.SendPlaystateCommand(
                null!,
                session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Stop, ControllingUserId = session.UserId.ToString("N") },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("[ComingSoon] Couldn't stop playback on {Client:l}: {Error:l}", session.Client, ex.Message);
        }
    }

    /// <summary>Resets watch state so stubs never land in Continue Watching or get marked played.</summary>
    public void ClearUserData(BaseItem item, params User[] users)
    {
        foreach (var user in users.Where(u => u is not null).DistinctBy(u => u.Id))
        {
            var data = userDataManager.GetUserData(user, item);
            if (data is null || (data.PlaybackPositionTicks == 0 && !data.Played && data.LastPlayedDate is null && data.PlayCount == 0))
            {
                continue;
            }

            data.PlaybackPositionTicks = 0;
            data.Played = false;
            data.LastPlayedDate = null;
            data.PlayCount = 0;
            userDataManager.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserData, CancellationToken.None);
        }
    }

    public SessionInfo? FindSession(string? deviceId, Guid userId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        return sessionManager.Sessions.FirstOrDefault(s =>
            string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal) && (userId == Guid.Empty || s.UserId == userId));
    }
}
