using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ComingSoon.Library;
using Jellyfin.Plugin.ComingSoon.Playback;
using Jellyfin.Plugin.ComingSoon.Tracking;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

/// <summary>Stand-in with the same type name as Jellyfin's controller.</summary>
#pragma warning disable CA1812
internal sealed class MediaInfoController
{
}
#pragma warning restore CA1812

public class PlaybackTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "cs-playback-root");
    private readonly StubRegistry _registry = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly Mock<IUserDataManager> _userData = new();

    public PlaybackTests()
    {
        _registry.SetRoot(Root);
        _registry.Upsert(new StubInfo("movie-tmdb1", "Project Hail Mary", new DisplayState(TrackedStatus.Downloading, 60, EtaBucket.FewHours, null, null)));
        _sessions.SetupGet(s => s.Sessions).Returns([]);
    }

    private PlaybackBlocker Blocker() => new(_registry, _sessions.Object, _userData.Object, NullLogger<PlaybackBlocker>.Instance);

    private static string StubPath => Path.Combine(Root, "cs-movie-1", "cs-movie-1.mp4");

    private static ActionExecutingContext Context(string controllerName, string action, Guid itemId)
    {
        var descriptor = new ControllerActionDescriptor
        {
            ActionName = action,
            ControllerName = controllerName,
            ControllerTypeInfo = (controllerName == "MediaInfo" ? typeof(MediaInfoController) : typeof(PlaybackTests)).GetTypeInfo(),
        };
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-DeviceId", "device-1")], "test")),
        };
        return new ActionExecutingContext(
            new ActionContext(http, new RouteData(), descriptor),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?> { ["itemId"] = itemId },
            controller: new object());
    }

    private async Task<(ActionExecutingContext Context, bool CalledNext)> Run(string controller, string action, BaseItem? item)
    {
        var id = Guid.NewGuid();
        _library.Setup(l => l.GetItemById(id)).Returns(item);
        var filter = new PlaybackInfoFilter(_library.Object, Blocker(), NullLogger<PlaybackInfoFilter>.Instance);
        var context = Context(controller, action, id);
        var calledNext = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            calledNext = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });
        return (context, calledNext);
    }

    [Theory]
    [InlineData("GetPlaybackInfo")]
    [InlineData("GetPostedPlaybackInfo")]
    public async Task PlaybackInfo_ForStub_IsRefused(string action)
    {
        var (context, calledNext) = await Run("MediaInfo", action, new Movie { Path = StubPath, Name = "Project Hail Mary" });

        Assert.False(calledNext);
        var result = Assert.IsType<OkObjectResult>(context.Result);
        var response = Assert.IsType<PlaybackInfoResponse>(result.Value);
        Assert.Equal(PlaybackErrorCode.NotAllowed, response.ErrorCode);
        Assert.Empty(response.MediaSources);
    }

    [Fact]
    public async Task PlaybackInfo_ForRealItem_PassesThrough()
    {
        var (context, calledNext) = await Run("MediaInfo", "GetPlaybackInfo", new Movie { Path = "/data/media/movies/Real (2020)/Real.mkv" });
        Assert.True(calledNext);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task OtherActions_AreUntouched()
    {
        var (_, calledNext) = await Run("Items", "GetItem", new Movie { Path = StubPath });
        Assert.True(calledNext);
    }

    [Fact]
    public async Task UnknownItem_PassesThrough()
    {
        var (_, calledNext) = await Run("MediaInfo", "GetPlaybackInfo", null);
        Assert.True(calledNext);
    }

    [Fact]
    public void Message_UsesCurrentStatus()
    {
        Assert.Equal("Still downloading — a few hours left", Blocker().MessageFor(new Movie { Path = StubPath }));
        Assert.Equal("Not available yet — still downloading", Blocker().MessageFor(new Movie { Path = Path.Combine(Root, "cs-movie-2", "cs-movie-2.mp4") }));
    }

    [Fact]
    public void ClearUserData_ResetsWatchState()
    {
        var item = new Movie { Path = StubPath };
        var user = new User("viewer", "auth", "reset");
        var data = new UserItemData { Key = "k", PlaybackPositionTicks = 12345, Played = true, PlayCount = 1, LastPlayedDate = DateTime.UtcNow };
        _userData.Setup(u => u.GetUserData(user, item)).Returns(data);

        Blocker().ClearUserData(item, user);

        _userData.Verify(u => u.SaveUserData(user, item, It.Is<UserItemData>(d => d.PlaybackPositionTicks == 0 && !d.Played && d.PlayCount == 0 && d.LastPlayedDate == null), UserDataSaveReason.UpdateUserData, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ClearUserData_SkipsWhenAlreadyClean()
    {
        var item = new Movie { Path = StubPath };
        var user = new User("viewer", "auth", "reset");
        _userData.Setup(u => u.GetUserData(user, item)).Returns(new UserItemData { Key = "k" });

        Blocker().ClearUserData(item, user);

        _userData.Verify(u => u.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
