using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComingSoon.Configuration;
using Jellyfin.Plugin.ComingSoon.Library;
using Jellyfin.Plugin.ComingSoon.Tracking;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

/// <summary>Records what the sync asks of Jellyfin.</summary>
internal sealed class FakeStubLibrary : IStubLibrary
{
    public LibraryState State { get; set; } = LibraryState.Existing;

    /// <summary>Gets or sets a value indicating whether scanned stubs are "in the library" (ApplyAsync finds them).</summary>
    public bool Indexed { get; set; } = true;

    public int Scans { get; private set; }

    public List<string> Applied { get; } = [];

    public List<string> Removed { get; } = [];

    public HashSet<string> RealItems { get; } = [];

    public Task<LibraryState> EnsureLibraryAsync(string root, string name, CancellationToken cancellationToken) => Task.FromResult(State);

    public Task ScanAsync(CancellationToken cancellationToken)
    {
        Scans++;
        return Task.CompletedTask;
    }

    public Task<StubRecord?> ApplyAsync(string mediaPath, StubRecord record, DisplayState state, CancellationToken cancellationToken)
    {
        if (!Indexed || !File.Exists(mediaPath))
        {
            return Task.FromResult<StubRecord?>(null);
        }

        Applied.Add(record.Entry.Key);
        return Task.FromResult<StubRecord?>(record with { MetadataApplied = true, AppliedPosterUrl = record.Entry.PosterUrl });
    }

    public void RemoveItem(string mediaPath) => Removed.Add(mediaPath);

    public bool ExistsOutsideStubs(TrackedEntry entry) => RealItems.Contains(entry.Key);
}

public sealed class StubSyncTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Video = Encoding.ASCII.GetBytes("fake-video");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-sync-" + Guid.NewGuid().ToString("N"));
    private readonly FixtureClientFactory _clients = new();
    private readonly ManualTimeProvider _time = new(T0);
    private readonly ListLogger _log = new();
    private readonly FakeStubLibrary _library = new();
    private readonly StubRegistry _registry = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private PluginConfiguration Config() => new()
    {
        RadarrUrl = "http://radarr:7878",
        RadarrApiKey = "k",
        SonarrUrl = "http://sonarr:8989",
        SonarrApiKey = "k",
        SeerrUrl = "http://seerr:5055",
        SeerrApiKey = "k",
        StubFolderPath = _root,
    };

    private (ComingSoonTracker Tracker, StubSync Sync) Start()
    {
        var tracker = new ComingSoonTracker(_clients, _log, _time);
        var sync = new StubSync(_library, _registry, _log, _time, () => Video);
        tracker.Seed(sync.Initialize(Config()), _time.LocalTimeZone, 5);
        return (tracker, sync);
    }

    private async Task Tick(ComingSoonTracker tracker, StubSync sync)
    {
        await sync.EnsureLibraryAsync(Config(), TestContext.Current.CancellationToken);
        var snapshot = await tracker.PollAsync(Config(), sync.ExistsOutsideStubs, TestContext.Current.CancellationToken);
        await sync.ApplyAsync(snapshot, Config(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FirstPoll_CreatesStubs_ScansOnce_WritesMetadata()
    {
        var (tracker, sync) = Start();

        await Tick(tracker, sync);

        var folders = Directory.GetDirectories(_root).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(11, folders.Count);
        Assert.Contains("cs-movie-687163", folders);
        Assert.Contains("cs-tv-371980-s02", folders);
        Assert.Equal(2, _library.Scans); // once when the library was found, once after creating stubs
        Assert.Equal(11, _library.Applied.Count);
        Assert.All(new StubStore(_root, () => Video).LoadAll(), r => Assert.True(r.MetadataApplied));
    }

    [Fact]
    public async Task Restart_LoadsStubs_NoDuplicates_NoRewrites()
    {
        var (tracker, sync) = Start();
        await Tick(tracker, sync);
        _library.Applied.Clear();
        var scansBefore = _library.Scans;

        var (tracker2, sync2) = Start();
        _time.Advance(TimeSpan.FromSeconds(30));
        await Tick(tracker2, sync2);

        Assert.Equal(11, Directory.GetDirectories(_root).Length);
        Assert.Empty(_library.Applied);                 // nothing changed, nothing rewritten
        Assert.Equal(1, _library.Scans - scansBefore);  // just the "library found" scan
        Assert.Contains(_log.Messages(), m => m == $"[ComingSoon] Stub folder {Path.GetFullPath(_root)}: 11 existing stubs loaded");
    }

    [Fact]
    public async Task Removal_DeletesFolderAndItem()
    {
        var (tracker, sync) = Start();
        await Tick(tracker, sync);

        _library.RealItems.Add("movie-tmdb550"); // Fight Club shows up in the Movies library
        _time.Advance(TimeSpan.FromMinutes(2));
        await Tick(tracker, sync);

        Assert.False(Directory.Exists(Path.Combine(_root, "cs-movie-550")));
        Assert.Single(_library.Removed, p => p.EndsWith("cs-movie-550.mp4", StringComparison.Ordinal));
        Assert.Contains(_log.Messages(), m => m == "[ComingSoon] Deleted stub 'Fight Club' (movie-tmdb550)");
    }

    [Fact]
    public async Task LibraryNotReady_StubsStillWritten_MetadataWhenReady()
    {
        _library.State = LibraryState.Unavailable;
        var (tracker, sync) = Start();
        await Tick(tracker, sync);

        Assert.Equal(11, Directory.GetDirectories(_root).Length);
        Assert.Equal(0, _library.Scans);
        Assert.Empty(_library.Applied);

        _library.State = LibraryState.Existing;
        _time.Advance(TimeSpan.FromMinutes(11));
        await Tick(tracker, sync);

        Assert.Equal(11, _library.Applied.Distinct().Count());
    }

    [Fact]
    public async Task LibraryRecreated_RewritesAllMetadata()
    {
        var (tracker, sync) = Start();
        await Tick(tracker, sync);
        _library.Applied.Clear();

        // Someone deleted the library in Jellyfin; after a restart the plugin creates it again.
        _library.State = LibraryState.Created;
        var (tracker2, sync2) = Start();
        await Tick(tracker2, sync2);

        Assert.Equal(11, _library.Applied.Count);
    }

    [Fact]
    public async Task UpgradeFromOlderStubs_RerendersEachOnce()
    {
        var (tracker, sync) = Start();
        await Tick(tracker, sync);

        // Pretend the stubs were written by an older plugin version.
        var store = new StubStore(_root, () => Video);
        foreach (var record in store.LoadAll())
        {
            store.Save(record with { SchemaVersion = 1 });
        }

        _library.Applied.Clear();
        var (tracker2, sync2) = Start();
        await Tick(tracker2, sync2);
        Assert.Equal(11, _library.Applied.Count);

        _library.Applied.Clear();
        var (tracker3, sync3) = Start();
        await Tick(tracker3, sync3);
        Assert.Empty(_library.Applied); // and only once
    }

    [Fact]
    public async Task NotYetIndexed_RetriedLater_WarnsOnce()
    {
        _library.Indexed = false;
        var (tracker, sync) = Start();
        await Tick(tracker, sync);
        Assert.Empty(_library.Applied);

        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await Tick(tracker, sync);
        }

        Assert.Equal(11, _log.Messages(LogLevel.Warning).Count(m => m.Contains("still isn't in the library", StringComparison.Ordinal)));
        Assert.True(_library.Scans >= 3); // keeps rescanning (every 2 min) while items aren't indexed

        _library.Indexed = true;
        _time.Advance(TimeSpan.FromSeconds(5));
        await Tick(tracker, sync);
        Assert.Equal(11, _library.Applied.Count);
    }

    [Fact]
    public async Task StubDeletedByHand_IsRecreated()
    {
        var (tracker, sync) = Start();
        await Tick(tracker, sync);
        Directory.Delete(Path.Combine(_root, "cs-movie-687163"), true);

        _time.Advance(TimeSpan.FromSeconds(5));
        await Tick(tracker, sync);

        Assert.True(File.Exists(Path.Combine(_root, "cs-movie-687163", "cs-movie-687163.mp4")));
    }

    [Fact]
    public async Task Registry_KnowsCurrentStatusForPlaybackMessages()
    {
        var (tracker, sync) = Start();
        await Tick(tracker, sync);

        var store = new StubStore(_root, () => Video);
        var info = _registry.Find(store.MediaPathFor("movie-tmdb687163"))!;
        Assert.Equal("Still downloading — a few hours left", StatusText.PlaybackMessage(info.State));
        Assert.True(_registry.IsStubPath(store.MediaPathFor("movie-tmdb687163")));
    }
}
