using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComingSoon.Playback;

/// <summary>
/// First line of defence: answers <c>GET/POST Items/{itemId}/PlaybackInfo</c> for stubs with
/// "not allowed" and no media sources, so clients refuse before they start, and shows the user why.
/// </summary>
public sealed class PlaybackInfoFilter(
    ILibraryManager libraryManager,
    PlaybackBlocker blocker,
    ILogger<PlaybackInfoFilter> logger) : IAsyncActionFilter
{
    private const string DeviceIdClaim = "Jellyfin-DeviceId";
    private const string UserIdClaim = "Jellyfin-UserId";

    public static bool IsPlaybackInfoAction(ActionExecutingContext context)
        => context.ActionDescriptor is ControllerActionDescriptor action
            && action.ControllerTypeInfo.Name == "MediaInfoController"
            && action.ActionName is "GetPlaybackInfo" or "GetPostedPlaybackInfo";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!IsPlaybackInfoAction(context)
            || !context.ActionArguments.TryGetValue("itemId", out var raw)
            || raw is not Guid itemId)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var item = libraryManager.GetItemById(itemId);
        if (item is null || !blocker.Registry.IsStubPath(item.Path))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var user = context.HttpContext.User;
        var deviceId = user.FindFirst(DeviceIdClaim)?.Value;
        var userId = Guid.TryParse(user.FindFirst(UserIdClaim)?.Value, out var u) ? u : Guid.Empty;
        var session = blocker.FindSession(deviceId, userId);

        logger.LogInformation(
            "[ComingSoon] Refused playback of '{Name:l}' on {Client:l}: {Message:l}",
            item.Name,
            session?.Client ?? "unknown client",
            blocker.MessageFor(item));

        context.Result = new OkObjectResult(new PlaybackInfoResponse
        {
            MediaSources = Array.Empty<MediaSourceInfo>(),
            ErrorCode = PlaybackErrorCode.NotAllowed,
        });

        if (session is not null)
        {
            await blocker.SendMessageAsync(session, item, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
