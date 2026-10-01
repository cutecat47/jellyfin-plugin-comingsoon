using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComingSoon.Api;
using Jellyfin.Plugin.ComingSoon.Configuration;
using Jellyfin.Plugin.ComingSoon.Services;
using Jellyfin.Plugin.ComingSoon.Tracking;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

public class TrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FixtureClientFactory _clients = new();
    private readonly ManualTimeProvider _time = new(T0);
    private readonly ListLogger _log = new();

    private static PluginConfiguration Config() => new()
    {
        RadarrUrl = "http://radarr:7878",
        RadarrApiKey = "radarr-test-key",
        SonarrUrl = "http://sonarr:8989",
        SonarrApiKey = "sonarr-test-key",
        SeerrUrl = "http://seerr:5055",
        SeerrApiKey = "seerr-test-key",
    };

    private ComingSoonTracker NewTracker() => new(_clients, _log, _time);

    [Fact]
    public async Task FirstPoll_TracksEverything_AndLogsWithPrefix()
    {
        var snapshot = await NewTracker().PollAsync(Config(), null, TestContext.Current.CancellationToken);

        Assert.Equal(11, snapshot.Entries.Count);
        Assert.Equal(11, snapshot.Updates.Count);
        Assert.All(snapshot.Updates, u => Assert.True(u.IsNew));
        Assert.True(snapshot.AllSourcesHealthy);
        Assert.Empty(snapshot.Removals);

        var phm = snapshot.Updates.Single(u => u.Entry.Key == "movie-tmdb687163");
        Assert.Equal("Downloading — 60% — a few hours left", phm.Overview);

        var sev = snapshot.Updates.Single(u => u.Entry.Key == "tv-tvdb371980-s02");
        Assert.Equal("Downloading — 40% — under an hour left (4 episodes)", sev.Overview);

        Assert.Contains(_log.Messages(LogLevel.Information), m => m == "[ComingSoon] Tracking 'Project Hail Mary' (movie-tmdb687163): Downloading — 60% — a few hours left");
        Assert.All(_log.Messages(), m => Assert.StartsWith("[ComingSoon]", m, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondPoll_SameData_NoUpdates()
    {
        var tracker = NewTracker();
        await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(5));

        var snapshot = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        Assert.Equal(11, snapshot.Entries.Count);
        Assert.Empty(snapshot.Updates);
    }

    [Fact]
    public async Task Restart_SeededFromExistingStubs_NoDuplicatesOrNewItems()
    {
        var first = NewTracker();
        await first.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        var restarted = NewTracker();
        restarted.Seed(first.Current.Values, _time.LocalTimeZone, 5);
        var snapshot = await restarted.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        Assert.Equal(11, snapshot.Entries.Select(e => e.Key).Distinct().Count());
        Assert.DoesNotContain(snapshot.Updates, u => u.IsNew);
    }

    [Fact]
    public async Task SonarrOutage_KeepsLastKnown_LogsOnce_AndRecovers()
    {
        var tracker = NewTracker();
        await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        _clients.Sonarr.Throw("/api/v3/queue");
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            var down = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);
            Assert.False(down.AllSourcesHealthy);
            Assert.Equal(11, down.Entries.Count);
            Assert.Contains(down.Entries, e => e.Key == "tv-tvdb371980-s02" && e.InQueue);
        }

        Assert.Single(_log.Messages(LogLevel.Error), m => m.StartsWith("[ComingSoon] Sonarr: can't connect", StringComparison.Ordinal));

        _clients.Sonarr.On("/api/v3/queue", "sonarr/queue.json");
        _time.Advance(TimeSpan.FromSeconds(5));
        var up = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        Assert.True(up.AllSourcesHealthy);
        Assert.Contains(_log.Messages(LogLevel.Information), m => m == "[ComingSoon] Sonarr is reachable again (7 items)");
    }

    [Fact]
    public async Task OutageNeverRemovesStubs_EvenAfterGracePeriod()
    {
        var tracker = NewTracker();
        await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        _clients.Sonarr.Fail("/api/v3/queue", HttpStatusCode.ServiceUnavailable);
        _clients.Radarr.On("/api/v3/queue", "bad/empty_queue.json");
        _time.Advance(TimeSpan.FromMinutes(5));
        await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(30));

        var snapshot = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        Assert.Empty(snapshot.Removals); // Radarr-only items vanished, but Sonarr is down
        Assert.Equal(11, snapshot.Entries.Count);
    }

    [Fact]
    public async Task AfterImport_StubRemovedOnceSeerrReportsAvailable()
    {
        var tracker = NewTracker();
        await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        // Imported: gone from Radarr's queue; Seerr has synced and lists the movie as available.
        _clients.Radarr.On("/api/v3/queue", () => FixtureHandler.Ok(Fixtures.Text("radarr/queue.json").Replace("\"tmdbId\": 687163", "\"tmdbId\": 1", StringComparison.Ordinal)));
        _clients.Seerr.On("/api/v1/request", () => FixtureHandler.Ok(Fixtures.Text("seerr/requests.json").Replace("\"tmdbId\": 687163", "\"tmdbId\": 157336", StringComparison.Ordinal)));
        _clients.Seerr.On("/api/v1/movie/687163", () => FixtureHandler.Ok(Fixtures.Text("seerr/movie_687163.json").Replace("\"status\": 3", "\"status\": 5", StringComparison.Ordinal)));
        _time.Advance(TimeSpan.FromMinutes(1));

        var snapshot = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        var removal = Assert.Single(snapshot.Removals, r => r.Entry.Key == "movie-tmdb687163");
        Assert.Equal(RemovalDecision.RemoveArrived, removal.Reason);
        Assert.DoesNotContain(snapshot.Entries, e => e.Key == "movie-tmdb687163");
        Assert.Contains(_log.Messages(), m => m == "[ComingSoon] Removing 'Project Hail Mary' (movie-tmdb687163): now available");
    }

    [Fact]
    public async Task RealItemInJellyfin_RemovesStub_EvenWhileSeerrLags()
    {
        var tracker = NewTracker();
        await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        _clients.Radarr.On("/api/v3/queue", "bad/empty_queue.json");
        _time.Advance(TimeSpan.FromMinutes(1));
        var snapshot = await tracker.PollAsync(Config(), e => e.Key == "movie-tmdb687163", TestContext.Current.CancellationToken);

        Assert.Contains(snapshot.Removals, r => r.Entry.Key == "movie-tmdb687163" && r.Reason == RemovalDecision.RemoveArrived);
        Assert.Contains(snapshot.Entries, e => e.Key == "movie-tmdb550"); // other Seerr items untouched
    }

    [Fact]
    public async Task VanishedEverywhere_RemovedAfterGrace_WhenHealthy()
    {
        var tracker = NewTracker();
        await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        // The Running Man is Radarr-only; someone removes it from the queue.
        _clients.Radarr.On("/api/v3/queue", () => FixtureHandler.Ok(Fixtures.Text("radarr/queue.json").Replace("\"tmdbId\": 798645", "\"tmdbId\": 0", StringComparison.Ordinal)));
        _time.Advance(TimeSpan.FromMinutes(1));
        var during = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);
        Assert.Contains(during.Entries, e => e.Key == "movie-tmdb798645");

        _time.Advance(RemovalPolicy.AbandonGrace);
        var after = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);
        Assert.Contains(after.Removals, r => r.Entry.Key == "movie-tmdb798645" && r.Reason == RemovalDecision.RemoveAbandoned);
    }

    [Fact]
    public async Task ProgressChange_IsThrottledToOncePerMinute()
    {
        var tracker = NewTracker();
        await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        // 62% -> 75% done.
        _clients.Radarr.On("/api/v3/queue", () => FixtureHandler.Ok(Fixtures.Text("radarr/queue.json").Replace("3264175145", "2147483648", StringComparison.Ordinal)));
        _time.Advance(TimeSpan.FromSeconds(30));
        var early = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(early.Updates, u => u.Entry.Key == "movie-tmdb687163");

        _time.Advance(TimeSpan.FromSeconds(30));
        var later = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);
        var update = Assert.Single(later.Updates, u => u.Entry.Key == "movie-tmdb687163");
        Assert.Equal("Downloading — 75% — a few hours left", update.Overview);
    }

    [Fact]
    public async Task OnlyRadarrConfigured_Works()
    {
        var config = new PluginConfiguration { RadarrUrl = "http://radarr:7878", RadarrApiKey = "k" };
        var snapshot = await NewTracker().PollAsync(config, null, TestContext.Current.CancellationToken);

        Assert.Equal(4, snapshot.Entries.Count);
        Assert.True(snapshot.AllSourcesHealthy);
        Assert.Empty(_clients.Seerr.Requests);
    }

    [Fact]
    public async Task InvalidUrl_IsLoggedOnce_NotFatal()
    {
        var config = Config();
        config.SonarrUrl = "sonarr:8989";
        var tracker = NewTracker();

        await tracker.PollAsync(config, null, TestContext.Current.CancellationToken);
        await tracker.PollAsync(config, null, TestContext.Current.CancellationToken);

        Assert.Single(_log.Messages(LogLevel.Warning), m => m.Contains("Sonarr settings invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verbose_LogsRawRecordsOnlyWhenTheyChange_AndSummaryNotEveryPoll()
    {
        var config = Config();
        config.VerboseLogging = true;
        var tracker = NewTracker();

        for (var i = 0; i < 5; i++)
        {
            await tracker.PollAsync(config, null, TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromSeconds(5));
        }

        var records = _log.Messages().Where(m => m.StartsWith("[ComingSoon]   Sonarr record:", StringComparison.Ordinal)).ToList();
        Assert.Equal(7, records.Count); // logged once, not per poll
        Assert.Contains(records, m => m.Contains("Andor S02E01 | status=completed state=importblocked tracked=warning", StringComparison.Ordinal));
        Assert.Contains(records, m => m.Contains("UPGRADE(ignored)", StringComparison.Ordinal));
        Assert.Equal(2, _log.Messages().Count(m => m.StartsWith("[ComingSoon] Poll:", StringComparison.Ordinal))); // first poll + the one where updates drop to 0
    }

    [Fact]
    public async Task StuckImportWithWarning_IsStalled_WithSonarrsMessage()
    {
        _clients.Sonarr.On("/api/v3/queue", () => FixtureHandler.Ok(Fixtures.Text("sonarr/queue.json")
            .Replace("\"trackedDownloadState\": \"importing\"", "\"trackedDownloadState\": \"importPending\"", StringComparison.Ordinal)
            .Replace("\"trackedDownloadStatus\": \"ok\",\n      \"trackedDownloadState\": \"importPending\"", "\"trackedDownloadStatus\": \"warning\",\n      \"trackedDownloadState\": \"importPending\"", StringComparison.Ordinal)
            .Replace("\"downloadId\": \"SABnzbd_nzo_tlou207\"", "\"errorMessage\": \"Import failed, path does not exist or is not accessible by Sonarr: /data/usenet/complete/tlou\", \"downloadId\": \"SABnzbd_nzo_tlou207\"", StringComparison.Ordinal)));

        var snapshot = await NewTracker().PollAsync(Config(), null, TestContext.Current.CancellationToken);

        var tlou = snapshot.Updates.Single(u => u.Entry.Key == "tv-tvdb392256-s02");
        Assert.Equal("Stalled — 100% — Import failed, path does not exist or is not accessible by Sonarr", tlou.Overview);
    }

    [Fact]
    public async Task UnairedFakeGrab_ShowsWaitingForRelease_WithAirDate()
    {
        _clients.Sonarr.On("/api/v3/queue", "sonarr/queue_unaired_fakes.json");

        var snapshot = await NewTracker().PollAsync(Config(), null, TestContext.Current.CancellationToken);

        var ted = snapshot.Updates.Single(u => u.Entry.Key == "tv-tvdb383203-s04");
        Assert.Equal("Waiting for release — expected 14 Oct 2026", ted.Overview);
        Assert.Equal("Not released yet — expected 14 Oct 2026", StatusText.PlaybackMessage(ted.State));
    }

    [Fact]
    public async Task RealLogScenario_DeadGrabIsSearching_UnairedIsWaiting_AdminWarnedOnce()
    {
        // Mirrors the records from the user's server (2026-10-01): two rejected .exe fakes.
        _clients.Sonarr.On("/api/v3/queue", "sonarr/queue_dead_grabs.json");
        var tracker = NewTracker();

        var snapshot = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(5));
        await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        var lanterns = snapshot.Updates.Single(u => u.Entry.Key == "tv-tvdb376098-s01");
        Assert.Equal("Searching — looking for a release", lanterns.Overview);
        Assert.DoesNotContain("/data/", lanterns.Overview, StringComparison.Ordinal);

        var ted = snapshot.Updates.Single(u => u.Entry.Key == "tv-tvdb383203-s04");
        Assert.Equal("Waiting for release — expected 7 Oct 2026", ted.Overview);

        var warning = Assert.Single(_log.Messages(LogLevel.Warning));
        Assert.StartsWith("[ComingSoon] Lanterns S01E04: Sonarr grabbed a release with no usable files", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeriesRemovedFromSonarr_StubGoesAfterGrace_EvenThoughSeerrStillHasTheRequest()
    {
        var tracker = NewTracker();
        var first = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);
        Assert.Contains(first.Entries, e => e.Key == "tv-tvdb371980-s03");

        // User deletes Severance from Sonarr; the Seerr request stays open.
        _clients.Sonarr.On("/api/v3/series", () => FixtureHandler.Ok("[]"), "tvdbId=371980");
        _time.Advance(TimeSpan.FromMinutes(11)); // past the 10-minute lookup cache
        var during = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        Assert.Contains(during.Entries, e => e.Key == "tv-tvdb371980-s03");
        Assert.Contains(_log.Messages(), m => m.StartsWith("[ComingSoon] 'Severance - Season 3' is still requested in Seerr but Sonarr isn't downloading it", StringComparison.Ordinal));

        _time.Advance(RemovalPolicy.AbandonGrace);
        var after = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        Assert.Contains(after.Removals, r => r.Entry.Key == "tv-tvdb371980-s03" && r.Reason == RemovalDecision.RemoveAbandoned);
        Assert.Contains(after.Entries, e => e.Key == "tv-tvdb371980-s02"); // still in the download queue: untouched
    }

    [Fact]
    public async Task SeasonUnmonitoredInSonarr_IsTreatedTheSame()
    {
        var tracker = NewTracker();
        await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        _clients.Sonarr.On(
            "/api/v3/series",
            () => FixtureHandler.Ok(Fixtures.Text("sonarr/series_371980.json").Replace("\"seasonNumber\": 3,\n        \"monitored\": true", "\"seasonNumber\": 3,\n        \"monitored\": false", StringComparison.Ordinal)),
            "tvdbId=371980");
        _time.Advance(TimeSpan.FromMinutes(11));
        await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);
        _time.Advance(RemovalPolicy.AbandonGrace);
        var after = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);

        Assert.Contains(after.Removals, r => r.Entry.Key == "tv-tvdb371980-s03");
    }

    [Fact]
    public async Task RequestNeverInRadarr_IsNotShown_AndReappearsIfAdded()
    {
        _clients.Radarr.On("/api/v3/movie", () => FixtureHandler.Ok("[]"), "tmdbId=550");
        var tracker = NewTracker();

        var first = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(first.Entries, e => e.Key == "movie-tmdb550");

        // Seerr hands it to Radarr a moment later; "not found" is re-checked after a minute.
        _clients.Radarr.On("/api/v3/movie", "radarr/movie_550.json", "tmdbId=550");
        _time.Advance(TimeSpan.FromSeconds(61));
        var later = await tracker.PollAsync(Config(), null, TestContext.Current.CancellationToken);
        Assert.Contains(later.Entries, e => e.Key == "movie-tmdb550");
    }

    [Fact]
    public async Task ConnectionTester_ReportsEachService()
    {
        var logger = new ListLogger<ConnectionTester>();
        var tester = new ConnectionTester(_clients, logger);
        _clients.Sonarr.Fail("/api/v3/system/status", HttpStatusCode.Unauthorized);

        var results = (await tester.TestAsync(
            new ConnectionTestRequest("http://seerr:5055", "k1", "http://sonarr:8989", "bad", "http://radarr:7878", "k3"),
            TestContext.Current.CancellationToken)).ToDictionary(r => r.Service);

        Assert.True(results["Seerr"].Success);
        Assert.Equal("Connected (Seerr 3.5.0): 3 movie and 2 TV requests waiting", results["Seerr"].Message);
        Assert.False(results["Sonarr"].Success);
        Assert.Contains("API key rejected (401)", results["Sonarr"].Message, StringComparison.Ordinal);
        Assert.True(results["Radarr"].Success);
        Assert.Equal("Connected (Radarr 5.28.0.10274): 5 queued downloads", results["Radarr"].Message);
        Assert.Equal(3, logger.Inner.Entries.Count);
    }

    [Fact]
    public async Task ConnectionTester_Unconfigured()
    {
        var tester = new ConnectionTester(_clients, new ListLogger<ConnectionTester>());
        var results = await tester.TestAsync(new ConnectionTestRequest(null, null, "", "", "http://radarr:7878", ""), TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.False(r.Success));
        Assert.Equal("Not configured", results.Single(r => r.Service == "Seerr").Message);
        Assert.Equal("API key is empty", results.Single(r => r.Service == "Radarr").Message);
    }
}
