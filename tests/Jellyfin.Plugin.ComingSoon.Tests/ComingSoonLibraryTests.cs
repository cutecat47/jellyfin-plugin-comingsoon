using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComingSoon.Library;
using Jellyfin.Plugin.ComingSoon.Model;
using Jellyfin.Plugin.ComingSoon.Tracking;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

public sealed class ComingSoonLibraryTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "cs-lib-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IProviderManager> _providers = new();
    private readonly Mock<ILibraryMonitor> _monitor = new();
    private readonly Guid _moviesId = Guid.NewGuid();
    private readonly Guid _stubsId = Guid.NewGuid();

    public ComingSoonLibraryTests()
    {
        Directory.CreateDirectory(Path.Combine(_base, "movies", "Backrooms (2026)"));
        Directory.CreateDirectory(Path.Combine(_base, "coming-soon"));
        _libraryManager.Setup(l => l.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Movies", Locations = [Path.Combine(_base, "movies")], CollectionType = CollectionTypeOptions.movies, ItemId = _moviesId.ToString("N") },
            new VirtualFolderInfo { Name = "Coming Soon", Locations = [Path.Combine(_base, "coming-soon")], CollectionType = CollectionTypeOptions.movies, ItemId = _stubsId.ToString("N") },
        ]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, true);
        }
    }

    private ComingSoonLibrary Library()
    {
        var registry = new StubRegistry();
        registry.SetRoot(Path.Combine(_base, "coming-soon")); // set by StubSync.Initialize at startup
        return new(
        _libraryManager.Object,
        _providers.Object,
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<IFileSystem>(),
        _monitor.Object,
        registry,
        NullLogger<ComingSoonLibrary>.Instance);
    }

    private static TrackedEntry Backrooms(string? path) => new()
    {
        Key = "movie-tmdb1083381",
        Kind = MediaKind.Movie,
        TmdbId = 1083381,
        Title = "Backrooms",
        Status = TrackedStatus.Importing,
        OnDisk = true,
        ArrPath = path,
    };

    [Fact]
    public void FolderInsideALibrary_IsReportedToJellyfin_ThenThrottled()
    {
        var library = Library();
        var folder = Path.Combine(_base, "movies", "Backrooms (2026)");

        library.RequestScanFor(Backrooms(folder));
        library.RequestScanFor(Backrooms(folder)); // within 5 minutes: ignored

        _monitor.Verify(m => m.ReportFileSystemChanged(folder), Times.Once);
        _providers.Verify(p => p.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()), Times.Never);
    }

    [Fact]
    public void PathJellyfinCantSee_FallsBackToScanningMoviesLibraries_NotTheStubLibrary()
    {
        var library = Library();

        library.RequestScanFor(Backrooms("/radarr-only/movies/Backrooms (2026)"));

        _monitor.Verify(m => m.ReportFileSystemChanged(It.IsAny<string>()), Times.Never);
        _providers.Verify(p => p.QueueRefresh(_moviesId, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal), Times.Once);
        _providers.Verify(p => p.QueueRefresh(_stubsId, It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()), Times.Never);
    }
}
