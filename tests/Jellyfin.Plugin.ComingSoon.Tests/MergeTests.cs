using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ComingSoon.Model;
using Jellyfin.Plugin.ComingSoon.Parsing;
using Jellyfin.Plugin.ComingSoon.Tracking;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

public class MergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    internal static MergeInput FullInput()
    {
        using var radarr = Fixtures.Json("radarr/queue.json");
        using var sonarr = Fixtures.Json("sonarr/queue.json");
        using var seerr = Fixtures.Json("seerr/requests.json");

        var details = new Dictionary<(MediaKind, int), SeerrDetails>();
        foreach (var (kind, id) in new[] { (MediaKind.Movie, 687163), (MediaKind.Movie, 1003596), (MediaKind.Movie, 550), (MediaKind.Series, 95396), (MediaKind.Series, 250001) })
        {
            using var d = Fixtures.Json($"seerr/{(kind == MediaKind.Movie ? "movie" : "tv")}_{id}.json");
            details[(kind, id)] = kind == MediaKind.Movie ? SeerrParser.ParseMovieDetails(d.RootElement)! : SeerrParser.ParseTvDetails(d.RootElement)!;
        }

        using var m1 = Fixtures.Json("radarr/movie_1003596.json");
        using var m2 = Fixtures.Json("radarr/movie_550.json");
        using var s1 = Fixtures.Json("sonarr/series_371980.json");
        using var s2 = Fixtures.Json("sonarr/series_450123.json");

        return new MergeInput
        {
            QueueRecords = ArrParser.ParseRadarrQueue(radarr.RootElement).Records.Concat(ArrParser.ParseSonarrQueue(sonarr.RootElement).Records).ToList(),
            SeerrRequests = SeerrParser.ParseRequests(seerr.RootElement).Requests,
            Details = details,
            Movies = new Dictionary<int, MovieReleaseInfo>
            {
                [1003596] = ArrParser.ParseMovieLookup(m1.RootElement, Now),
                [550] = ArrParser.ParseMovieLookup(m2.RootElement, Now),
            },
            Series = new Dictionary<int, SeriesReleaseInfo>
            {
                [371980] = ArrParser.ParseSeriesLookup(s1.RootElement),
                [450123] = ArrParser.ParseSeriesLookup(s2.RootElement),
            },
            Now = Now,
        };
    }

    [Fact]
    public void FullFixtureSet_ProducesExpectedEntries_NewestFirst()
    {
        var entries = EntryBuilder.Build(FullInput());

        Assert.Equal(
            new[]
            {
                "tv-tvdb371980-s02",   // Severance S2, requested 30 Sep 23:05
                "tv-tvdb371980-s03",   // Severance S3, same request
                "movie-tmdb798645",    // The Running Man, added in Radarr 30 Sep 21:02
                "movie-tmdb1003596",   // Avengers: Doomsday, requested 30 Sep 19:02
                "tv-tvdb392256-s02",   // The Last of Us S2, added 29 Sep 12:00
                "movie-tmdb550",       // Fight Club, requested 29 Sep 08:00
                "movie-tmdb687163",    // Project Hail Mary, requested 28 Sep
                "movie-tmdb1061474",   // Superman
                "tv-tvdb450123-s01",   // The Example Files S1 (tvdbId only known from Seerr details)
                "tv-tvdb393189-s02",   // Andor S2
                "movie-tmdb967941",    // Wicked: For Good
            },
            entries.Select(e => e.Key));
    }

    [Fact]
    public void QueueAndSeerr_AreMerged()
    {
        var phm = EntryBuilder.Build(FullInput()).Single(e => e.Key == "movie-tmdb687163");

        Assert.Equal(TrackedStatus.Downloading, phm.Status);
        Assert.True(phm.InQueue);
        Assert.True(phm.InSeerr);
        Assert.Equal("Project Hail Mary", phm.Title);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 10, 13, 55, TimeSpan.Zero), phm.RequestedAt); // the request, not the grab
        Assert.Equal("https://image.tmdb.org/t/p/w780/seerr-phm-poster.jpg", phm.PosterUrl);       // Seerr/TMDB art preferred
        Assert.Equal("https://image.tmdb.org/t/p/w1280/seerr-phm-backdrop.jpg", phm.BackdropUrl);
    }

    [Fact]
    public void SeerrOnly_SearchingVsWaiting()
    {
        var entries = EntryBuilder.Build(FullInput()).ToDictionary(e => e.Key);

        Assert.Equal(TrackedStatus.WaitingForRelease, entries["movie-tmdb1003596"].Status);
        Assert.Equal(new DateTimeOffset(2027, 3, 5, 0, 0, 0, TimeSpan.Zero), entries["movie-tmdb1003596"].ReleaseDate);

        Assert.Equal(TrackedStatus.Searching, entries["movie-tmdb550"].Status);

        var s3 = entries["tv-tvdb371980-s03"];
        Assert.Equal(TrackedStatus.WaitingForRelease, s3.Status);
        Assert.Equal(new DateTimeOffset(2027, 1, 15, 2, 0, 0, TimeSpan.Zero), s3.ReleaseDate);
        Assert.Equal("Severance - Season 3", s3.DisplayName);

        var example = entries["tv-tvdb450123-s01"];
        Assert.Equal(TrackedStatus.Searching, example.Status);
        Assert.Equal("3 of 8 episodes", example.Detail);
        Assert.Equal(450123, example.TvdbId);
    }

    [Fact]
    public void QueueOnly_EntriesAreIncluded_WithArrArtwork()
    {
        var running = EntryBuilder.Build(FullInput()).Single(e => e.Key == "movie-tmdb798645");

        Assert.False(running.InSeerr);
        Assert.Equal("The Running Man", running.Title);
        Assert.Equal("https://image.tmdb.org/t/p/original/radarr-trm-poster.jpg", running.PosterUrl);
    }

    [Fact]
    public void AvailableSeason_IsSkipped()
    {
        var input = FullInput() with
        {
            SeerrRequests = [new SeerrRequest { RequestId = 1, Kind = MediaKind.Series, TmdbId = 95396, TvdbId = 371980, Seasons = [1] }],
            QueueRecords = [],
        };
        Assert.Empty(EntryBuilder.Build(input)); // Season 1 is status 5 in the Seerr details
    }

    [Fact]
    public void NoDetailsOrLookups_StillWorks()
    {
        var input = new MergeInput
        {
            SeerrRequests = [new SeerrRequest { RequestId = 1, Kind = MediaKind.Movie, TmdbId = 42 }],
            Now = Now,
        };
        var entry = Assert.Single(EntryBuilder.Build(input));
        Assert.Equal("Unknown", entry.Title);
        Assert.Equal(TrackedStatus.Searching, entry.Status);
    }

    [Fact]
    public void DuplicateRequests_HdAnd4k_BecomeOneEntry()
    {
        var input = new MergeInput
        {
            SeerrRequests =
            [
                new SeerrRequest { RequestId = 1, Kind = MediaKind.Movie, TmdbId = 42, CreatedAt = Now.AddDays(-1) },
                new SeerrRequest { RequestId = 2, Kind = MediaKind.Movie, TmdbId = 42, Is4k = true, CreatedAt = Now.AddDays(-3) },
            ],
            Now = Now,
        };
        var entry = Assert.Single(EntryBuilder.Build(input));
        Assert.Equal(Now.AddDays(-3), entry.RequestedAt);
    }

    [Fact]
    public void SeriesMatchedByTmdb_WhenSeerrHasNoTvdb()
    {
        var input = new MergeInput
        {
            QueueRecords = [new QueueRecord { Kind = MediaKind.Series, TvdbId = 777, TmdbId = 555, SeasonNumber = 1, RecordId = 1, Size = 10, SizeLeft = 5, Status = "downloading" }],
            SeerrRequests = [new SeerrRequest { RequestId = 1, Kind = MediaKind.Series, TmdbId = 555, Seasons = [1] }],
            Now = Now,
        };
        var entry = Assert.Single(EntryBuilder.Build(input));
        Assert.True(entry.InQueue && entry.InSeerr);
        Assert.Equal("tv-tvdb777-s01", entry.Key);
    }

    [Fact]
    public void MovieAlreadyOnDisk_ButNotYetAvailableInSeerr_ShowsImporting()
    {
        var input = new MergeInput
        {
            SeerrRequests = [new SeerrRequest { RequestId = 1, Kind = MediaKind.Movie, TmdbId = 42 }],
            Movies = new Dictionary<int, MovieReleaseInfo> { [42] = new(true, true, true, null, true, "/data/media/movies/Backrooms (2026)") },
            Now = Now,
        };
        var entry = EntryBuilder.Build(input).Single();
        Assert.Equal(TrackedStatus.Importing, entry.Status);
        Assert.True(entry.OnDisk);
        Assert.Equal("/data/media/movies/Backrooms (2026)", entry.ArrPath);
    }

    [Fact]
    public void StillDownloading_IsNotOnDisk()
    {
        var phm = EntryBuilder.Build(FullInput()).Single(e => e.Key == "movie-tmdb687163");
        Assert.False(phm.OnDisk);
    }
}
