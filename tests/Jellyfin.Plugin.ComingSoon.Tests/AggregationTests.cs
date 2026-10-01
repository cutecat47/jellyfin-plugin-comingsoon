using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ComingSoon.Model;
using Jellyfin.Plugin.ComingSoon.Parsing;
using Jellyfin.Plugin.ComingSoon.Tracking;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

public class AggregationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<QueueRecord> Load(string fixture)
    {
        using var doc = Fixtures.Json(fixture);
        return fixture.StartsWith("radarr", StringComparison.Ordinal)
            ? ArrParser.ParseRadarrQueue(doc.RootElement).Records
            : ArrParser.ParseSonarrQueue(doc.RootElement).Records;
    }

    [Fact]
    public void SeasonPack_IsCountedOnce_AndGroupedWithSingleEpisode()
    {
        var groups = QueueAggregator.Aggregate(Load("sonarr/queue.json"), Now);
        var severance = groups.Single(g => g.TvdbId == 371980);

        // Pack: 12 GiB (6 GiB left) shared by E01-E03, plus E04 on its own: 2 GiB (2 GiB left).
        // (14 - 8) / 14 = 42.86%. Summing per record would give a wrong 46.15%.
        Assert.Equal(2, severance.SeasonNumber);
        Assert.Equal(6.0 / 14.0 * 100, severance.Percent!.Value, 6);
        Assert.Equal(TrackedStatus.Downloading, severance.Status);
        Assert.Equal(TimeSpan.FromMinutes(42) + TimeSpan.FromSeconds(10), severance.TimeLeft);
        Assert.Equal(4, severance.EpisodeCount);
    }

    [Fact]
    public void Sonarr_StatusBuckets_AndUpgradesSkipped()
    {
        var groups = QueueAggregator.Aggregate(Load("sonarr/queue.json"), Now);

        Assert.Equal(3, groups.Count); // Slow Horses (episode already has a file) is an upgrade
        Assert.Equal(TrackedStatus.Importing, groups.Single(g => g.TvdbId == 392256).Status);
        Assert.Equal(100, groups.Single(g => g.TvdbId == 392256).Percent);

        var andor = groups.Single(g => g.TvdbId == 393189);
        Assert.Equal(TrackedStatus.Stalled, andor.Status);
        Assert.StartsWith("Episode file already imported", andor.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Radarr_StatusBuckets()
    {
        var groups = QueueAggregator.Aggregate(Load("radarr/queue.json"), Now).ToDictionary(g => g.TmdbId!.Value);

        Assert.Equal(4, groups.Count); // Dune: Part Two is an upgrade
        Assert.Equal(TrackedStatus.Downloading, groups[687163].Status);
        Assert.Equal(62.0, groups[687163].Percent!.Value, 1);
        Assert.Equal(TrackedStatus.Queued, groups[798645].Status);
        Assert.Equal(TrackedStatus.Stalled, groups[967941].Status);
        Assert.Equal("The download is stalled with no connections", groups[967941].Detail);
        Assert.Equal(TrackedStatus.Importing, groups[1061474].Status);
    }

    [Theory]
    [InlineData("downloading", "downloading", "ok", TrackedStatus.Downloading)]
    [InlineData("queued", "downloading", "ok", TrackedStatus.Queued)]
    [InlineData("paused", "downloading", "ok", TrackedStatus.Queued)]
    [InlineData("delay", null, null, TrackedStatus.Queued)]
    [InlineData("downloadclientunavailable", null, null, TrackedStatus.Stalled)]
    [InlineData("warning", "downloading", "warning", TrackedStatus.Stalled)]
    [InlineData("failed", "failedpending", "error", TrackedStatus.Stalled)]
    [InlineData("completed", "downloading", "ok", TrackedStatus.Importing)]
    [InlineData("completed", "importpending", "ok", TrackedStatus.Importing)]
    [InlineData("completed", "importblocked", "warning", TrackedStatus.Stalled)]
    [InlineData("completed", "imported", "ok", TrackedStatus.Importing)]
    [InlineData("completed", "importpending", "warning", TrackedStatus.Stalled)] // stuck: "No files found are eligible for import"
    [InlineData("completed", "imported", "warning", TrackedStatus.Importing)]
    [InlineData("somethingnew", null, null, TrackedStatus.Queued)]
    public void Classify(string status, string? state, string? trackedStatus, TrackedStatus expected)
    {
        var record = new QueueRecord { Kind = MediaKind.Movie, Status = status, TrackedState = state, TrackedStatus = trackedStatus };
        Assert.Equal(expected, QueueAggregator.Classify(record));
    }

    [Fact]
    public void GroupPriority_DownloadingBeatsStalledBeatsQueuedBeatsImporting()
    {
        QueueRecord Ep(int n, string status, string? state = "downloading") => new()
        {
            Kind = MediaKind.Series, TvdbId = 1, SeasonNumber = 1, EpisodeNumber = n, RecordId = n,
            DownloadId = "d" + n, Size = 100, SizeLeft = 50, Status = status, TrackedState = state, TrackedStatus = "ok",
        };

        Assert.Equal(TrackedStatus.Downloading, QueueAggregator.Aggregate([Ep(1, "completed", "importing"), Ep(2, "warning"), Ep(3, "downloading")], Now).Single().Status);
        Assert.Equal(TrackedStatus.Stalled, QueueAggregator.Aggregate([Ep(1, "completed", "importing"), Ep(2, "warning"), Ep(3, "queued")], Now).Single().Status);
        Assert.Equal(TrackedStatus.Queued, QueueAggregator.Aggregate([Ep(1, "completed", "importing"), Ep(3, "queued")], Now).Single().Status);
        Assert.Equal(TrackedStatus.Importing, QueueAggregator.Aggregate([Ep(1, "completed", "importing"), Ep(2, "completed", "importpending")], Now).Single().Status);
    }

    [Fact]
    public void DifferentSeasons_AreSeparateGroups()
    {
        var records = new[]
        {
            new QueueRecord { Kind = MediaKind.Series, TvdbId = 5, SeasonNumber = 1, RecordId = 1, Size = 10, SizeLeft = 5, Status = "downloading" },
            new QueueRecord { Kind = MediaKind.Series, TvdbId = 5, SeasonNumber = 2, RecordId = 2, Size = 10, SizeLeft = 5, Status = "downloading" },
        };
        Assert.Equal(2, QueueAggregator.Aggregate(records, Now).Count);
    }

    [Fact]
    public void PausedOnly_SaysPaused()
    {
        var r = new QueueRecord { Kind = MediaKind.Movie, TmdbId = 1, Size = 10, SizeLeft = 5, Status = "paused", TrackedState = "downloading", TrackedStatus = "ok" };
        Assert.Equal("Paused", QueueAggregator.Aggregate([r], Now).Single().Detail);
    }

    [Fact]
    public void TimeLeft_FallsBackToEstimatedCompletion_AndUsesSlowestDownload()
    {
        var records = new[]
        {
            new QueueRecord { Kind = MediaKind.Series, TvdbId = 9, SeasonNumber = 1, RecordId = 1, DownloadId = "a", Size = 10, SizeLeft = 5, Status = "downloading", TimeLeft = TimeSpan.FromMinutes(10) },
            new QueueRecord { Kind = MediaKind.Series, TvdbId = 9, SeasonNumber = 1, RecordId = 2, DownloadId = "b", Size = 10, SizeLeft = 5, Status = "downloading", EstimatedCompletion = Now.AddHours(3) },
        };
        Assert.Equal(TimeSpan.FromHours(3), QueueAggregator.Aggregate(records, Now).Single().TimeLeft);
    }

    [Fact]
    public void FakeGrabForUnairedEpisode_IsWaitingForRelease_NotImporting()
    {
        var groups = QueueAggregator.Aggregate(Load("sonarr/queue_unaired_fakes.json"), Now);

        var ted = groups.Single(g => g.TvdbId == 383203);
        Assert.Equal(TrackedStatus.WaitingForRelease, ted.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 1, 0, 0, TimeSpan.Zero), ted.ReleaseDate);
        Assert.Null(ted.Percent);
    }

    [Fact]
    public void SeasonWithRealAndFakeGrabs_ProgressComesFromRealDownloadOnly()
    {
        var lanterns = QueueAggregator.Aggregate(Load("sonarr/queue_unaired_fakes.json"), Now).Single(g => g.TvdbId == 376098);

        Assert.Equal(TrackedStatus.Downloading, lanterns.Status); // E04 is real and downloading
        Assert.Equal(75.0, lanterns.Percent!.Value, 6);           // E05 (unaired fake, 0 bytes) ignored
        Assert.Equal(TimeSpan.FromMinutes(4), lanterns.TimeLeft);
    }

    [Fact]
    public void FakeGrab_EpisodeAirs_ThenItCountsNormally()
    {
        var afterAiring = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        var ted = QueueAggregator.Aggregate(Load("sonarr/queue_unaired_fakes.json"), afterAiring).Single(g => g.TvdbId == 383203);

        Assert.Equal(TrackedStatus.Stalled, ted.Status); // still stuck, now shown as a problem
        Assert.Equal("Invalid video file, unsupported extension: '.lnk'", ted.Detail);
    }

    [Fact]
    public void UnreleasedMovie_IsWaitingForRelease()
    {
        var r = new QueueRecord
        {
            Kind = MediaKind.Movie, TmdbId = 7, Size = 100, SizeLeft = 0, Status = "completed", TrackedState = "importpending", TrackedStatus = "ok",
            MovieAvailable = false, ReleaseDate = Now.AddDays(20),
        };
        var group = QueueAggregator.Aggregate([r], Now).Single();
        Assert.Equal(TrackedStatus.WaitingForRelease, group.Status);
        Assert.Equal(Now.AddDays(20), group.ReleaseDate);
    }

    [Theory]
    [InlineData("No files found are eligible for import in /data/torrents/tv/Show S01E04.exe", "No files found are eligible for import")]
    [InlineData("[/data/downloads/x] is not a valid local path. You may need a Remote Path Mapping.", null)]
    [InlineData("Import failed, path does not exist or is not accessible by Sonarr: /data/x", "Import failed, path does not exist or is not accessible by Sonarr")]
    [InlineData("The download is stalled with no connections", "The download is stalled with no connections")]
    [InlineData("Invalid video file, unsupported extension: '.lnk'", "Invalid video file, unsupported extension: '.lnk'")]
    [InlineData("Not found in C:\\Downloads\\thing", "Not found")]
    public void SanitizeMessage_RemovesPaths(string input, string? expected)
        => Assert.Equal(expected, QueueAggregator.SanitizeMessage(input));

    [Fact]
    public void RealStall_KeepsShowingStalled_WithoutPaths()
    {
        var r = new QueueRecord
        {
            Kind = MediaKind.Movie, TmdbId = 3, Size = 1000, SizeLeft = 0, Status = "completed", TrackedState = "importblocked", TrackedStatus = "warning",
            Message = "Import failed, path does not exist or is not accessible by Radarr: /data/movies/x.mkv",
        };
        Assert.False(QueueAggregator.IsDeadGrab(r));
        var group = QueueAggregator.Aggregate([r], Now).Single();
        Assert.Equal(TrackedStatus.Stalled, group.Status);
        Assert.Equal("Import failed, path does not exist or is not accessible by Radarr", group.Detail);
    }

    [Fact]
    public void ZeroSize_HasUnknownPercent()
    {
        var r = new QueueRecord { Kind = MediaKind.Movie, TmdbId = 1, Size = 0, SizeLeft = 0, Status = "downloading" };
        Assert.Null(QueueAggregator.Aggregate([r], Now).Single().Percent);
    }
}
