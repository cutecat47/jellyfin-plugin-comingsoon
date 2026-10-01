using System;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ComingSoon.Model;
using Jellyfin.Plugin.ComingSoon.Parsing;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

public class ParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RadarrQueue_ParsesRecords_AndSkipsUnknownItem()
    {
        using var doc = Fixtures.Json("radarr/queue.json");
        var page = ArrParser.ParseRadarrQueue(doc.RootElement);

        Assert.Equal(6, page.TotalRecords);
        Assert.Equal(5, page.Records.Count);
        Assert.Equal(1, page.Skipped); // the record with no "movie"

        var phm = page.Records.Single(r => r.TmdbId == 687163);
        Assert.Equal(MediaKind.Movie, phm.Kind);
        Assert.Equal("Project Hail Mary", phm.Title);
        Assert.Equal(2026, phm.Year);
        Assert.Equal(8589934592d, phm.Size);
        Assert.Equal(3264175145d, phm.SizeLeft);
        Assert.Equal(new TimeSpan(2, 41, 7), phm.TimeLeft);
        Assert.Equal("downloading", phm.Status);
        Assert.Equal("downloading", phm.TrackedState);
        Assert.Equal("ok", phm.TrackedStatus);
        Assert.Equal("SABnzbd_nzo_8k2j4h1x", phm.DownloadId);
        Assert.Equal("https://image.tmdb.org/t/p/original/radarr-phm-poster.jpg", phm.PosterUrl);
        Assert.Equal("https://image.tmdb.org/t/p/original/radarr-phm-fanart.jpg", phm.BackdropUrl);
        Assert.False(phm.IsUpgrade);
        Assert.True(phm.MovieAvailable);
        Assert.Equal(new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero), phm.ReleaseDate); // digital release

        Assert.True(page.Records.Single(r => r.TmdbId == 693134).IsUpgrade);
        Assert.Equal("The download is stalled with no connections", page.Records.Single(r => r.TmdbId == 967941).Message);
        Assert.Equal("importpending", page.Records.Single(r => r.TmdbId == 1061474).TrackedState);
        Assert.Null(page.Records.Single(r => r.TmdbId == 798645).TimeLeft);
    }

    [Fact]
    public void SonarrQueue_ParsesEpisodeRecords()
    {
        using var doc = Fixtures.Json("sonarr/queue.json");
        var page = ArrParser.ParseSonarrQueue(doc.RootElement);

        Assert.Equal(7, page.Records.Count);
        Assert.Equal(0, page.Skipped);

        var e1 = page.Records.First();
        Assert.Equal(MediaKind.Series, e1.Kind);
        Assert.Equal(371980, e1.TvdbId);
        Assert.Equal(95396, e1.TmdbId);
        Assert.Equal(2, e1.SeasonNumber);
        Assert.Equal(1, e1.EpisodeNumber);
        Assert.Equal("Severance", e1.Title);
        Assert.Equal("SABnzbd_nzo_sev2pack", e1.DownloadKey);

        Assert.Equal(new DateTimeOffset(2025, 1, 17, 2, 0, 0, TimeSpan.Zero), e1.ReleaseDate);
        Assert.True(page.Records.Single(r => r.TvdbId == 359913).IsUpgrade);
        Assert.Equal("importblocked", page.Records.Single(r => r.TvdbId == 393189).TrackedState);
    }

    [Fact]
    public void RadarrQueue_Malformed_DoesNotThrow_AndKeepsWhatItCan()
    {
        using var doc = Fixtures.Json("bad/radarr_queue_malformed.json");
        var page = ArrParser.ParseRadarrQueue(doc.RootElement);

        // Kept: Bad Numbers (tmdbId as string), Negative Left, More Left Than Size, Brand New Status, Huge.
        Assert.Equal(new[] { 2001, 2002, 2003, 2004, 2005 }, page.Records.Select(r => r.TmdbId!.Value).Order());
        Assert.Equal(5, page.Skipped);

        var bad = page.Records.Single(r => r.TmdbId == 2001);
        Assert.Equal(0, bad.Size);
        Assert.Equal(0, bad.SizeLeft);
        Assert.Null(bad.TimeLeft);
        Assert.Equal("7", bad.Status);
        Assert.False(bad.IsUpgrade); // "false" as a string

        var negative = page.Records.Single(r => r.TmdbId == 2002);
        Assert.Equal(0, negative.SizeLeft);
        Assert.Null(negative.TimeLeft);
        Assert.Equal("downloading", negative.Status);
        Assert.Null(negative.EstimatedCompletion);

        Assert.Equal(100, page.Records.Single(r => r.TmdbId == 2003).SizeLeft); // clamped to size
        Assert.Null(page.Records.Single(r => r.TmdbId == 2004).Message);
        Assert.Equal(0, page.Records.Single(r => r.TmdbId == 2005).Size); // infinite -> ignored
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("{\"records\": null}")]
    [InlineData("{\"records\": {}}")]
    public void Queue_OddRoots_ReturnEmpty(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(ArrParser.ParseRadarrQueue(doc.RootElement).Records);
        Assert.Empty(ArrParser.ParseSonarrQueue(doc.RootElement).Records);
        Assert.Empty(SeerrParser.ParseRequests(doc.RootElement).Requests);
    }

    [Fact]
    public void EmptyQueue_IsEmpty()
    {
        using var doc = Fixtures.Json("bad/empty_queue.json");
        var page = ArrParser.ParseRadarrQueue(doc.RootElement);
        Assert.Empty(page.Records);
        Assert.Equal(0, page.TotalRecords);
    }

    [Fact]
    public void TimeLeft_WithDays_Parses()
    {
        using var doc = JsonDocument.Parse("{\"records\":[{\"movie\":{\"tmdbId\":1},\"timeleft\":\"1.02:03:04\"}]}");
        Assert.Equal(new TimeSpan(1, 2, 3, 4), ArrParser.ParseRadarrQueue(doc.RootElement).Records[0].TimeLeft);
    }

    [Fact]
    public void SeerrRequests_KeepsApprovedUnavailable()
    {
        using var doc = Fixtures.Json("seerr/requests.json");
        var page = SeerrParser.ParseRequests(doc.RootElement);

        Assert.Equal(new[] { 101, 102, 103, 104, 105 }, page.Requests.Select(r => r.RequestId));
        Assert.Equal(2, page.Skipped); // declined (106) and already available (107)
        Assert.Equal(1, page.Pages);

        var tv = page.Requests.Single(r => r.RequestId == 104);
        Assert.Equal(MediaKind.Series, tv.Kind);
        Assert.Equal(95396, tv.TmdbId);
        Assert.Equal(371980, tv.TvdbId);
        Assert.Equal(new[] { 2, 3 }, tv.Seasons);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 23, 5, 0, TimeSpan.Zero), tv.CreatedAt);

        Assert.Null(page.Requests.Single(r => r.RequestId == 105).TvdbId);
    }

    [Fact]
    public void SeerrRequests_Malformed_DoesNotThrow()
    {
        using var doc = Fixtures.Json("bad/seerr_requests_malformed.json");
        var page = SeerrParser.ParseRequests(doc.RootElement);

        Assert.Equal(new[] { 4, 5 }, page.Requests.Select(r => r.RequestId));
        Assert.Equal(1, page.Pages); // "pageInfo": "nope"

        var movie = page.Requests.Single(r => r.RequestId == 4);
        Assert.Equal(MediaKind.Movie, movie.Kind); // from media.mediaType
        Assert.Null(movie.CreatedAt);

        var tv = page.Requests.Single(r => r.RequestId == 5);
        Assert.Equal(404, tv.TvdbId);
        Assert.Equal(new[] { 1, 2 }, tv.Seasons);
    }

    [Fact]
    public void SeerrDetails_Movie()
    {
        using var doc = Fixtures.Json("seerr/movie_687163.json");
        var d = SeerrParser.ParseMovieDetails(doc.RootElement)!;

        Assert.Equal("Project Hail Mary", d.Title);
        Assert.Equal(2026, d.Year);
        Assert.Equal("/seerr-phm-poster.jpg", d.PosterPath);
        Assert.Equal(SeerrMediaStatus.Processing, d.MediaStatus);
        Assert.Equal("https://image.tmdb.org/t/p/w780/seerr-phm-poster.jpg", SeerrParser.TmdbImageUrl(d.PosterPath, "w780"));
    }

    [Fact]
    public void SeerrDetails_Tv()
    {
        using var doc = Fixtures.Json("seerr/tv_95396.json");
        var d = SeerrParser.ParseTvDetails(doc.RootElement)!;

        Assert.Equal("Severance", d.Title);
        Assert.Equal(371980, d.TvdbId);
        Assert.Equal(SeerrMediaStatus.Available, d.SeasonStatus[1]);
        Assert.Equal(SeerrMediaStatus.Processing, d.SeasonStatus[2]);
        Assert.Equal(new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero), d.SeasonAirDates[3]);

        using var ex = Fixtures.Json("seerr/tv_250001.json");
        Assert.Equal(450123, SeerrParser.ParseTvDetails(ex.RootElement)!.TvdbId); // from externalIds
    }

    [Fact]
    public void SeerrDetails_MissingId_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("{\"title\":\"x\"}");
        Assert.Null(SeerrParser.ParseMovieDetails(doc.RootElement));
        Assert.Null(SeerrParser.ParseTvDetails(doc.RootElement));
    }

    [Fact]
    public void MovieLookup_PrefersDigitalReleaseOverCinema()
    {
        using var doc = Fixtures.Json("radarr/movie_1003596.json");
        var info = ArrParser.ParseMovieLookup(doc.RootElement, Now);

        Assert.True(info.InRadarr);
        Assert.False(info.IsAvailable);
        Assert.Equal(new DateTimeOffset(2027, 3, 5, 0, 0, 0, TimeSpan.Zero), info.NextReleaseDate);

        using var empty = JsonDocument.Parse("[]");
        Assert.False(ArrParser.ParseMovieLookup(empty.RootElement, Now).InRadarr);
    }

    [Fact]
    public void SeriesLookup_ParsesSeasonStatistics()
    {
        using var doc = Fixtures.Json("sonarr/series_371980.json");
        var info = ArrParser.ParseSeriesLookup(doc.RootElement);

        Assert.True(info.InSonarr);
        Assert.Equal(3, info.Seasons.Count);
        Assert.Null(info.Seasons[3].PreviousAiring);
        Assert.Equal(new DateTimeOffset(2027, 1, 15, 2, 0, 0, TimeSpan.Zero), info.Seasons[3].NextAiring);
        Assert.Equal(10, info.Seasons[2].EpisodeCount);
    }

    [Fact]
    public void Versions()
    {
        using var r = Fixtures.Json("radarr/system_status.json");
        using var s = Fixtures.Json("seerr/status.json");
        Assert.Equal("5.28.0.10274", ArrParser.ParseVersion(r.RootElement));
        Assert.Equal("3.5.0", SeerrParser.ParseVersion(s.RootElement));
    }
}
