using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ComingSoon.Library;
using Jellyfin.Plugin.ComingSoon.Model;
using Jellyfin.Plugin.ComingSoon.Tracking;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

public class StubMetadataTests
{
    private static readonly DisplayState Downloading = new(TrackedStatus.Downloading, 60, EtaBucket.FewHours, null, null);

    private static TrackedEntry Entry(string title, DateTimeOffset requested) => new()
    {
        Key = "movie-tmdb1",
        Kind = MediaKind.Movie,
        TmdbId = 1,
        Title = title,
        Year = 2026,
        RequestedAt = requested,
    };

    [Fact]
    public void Apply_WritesStatusAndLocksEverything()
    {
        var item = new Movie
        {
            Name = "cs-movie-1",
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1", ["Imdb"] = "tt1234567" },
            Genres = ["Drama"],
        };
        var requested = new DateTimeOffset(2026, 9, 28, 10, 13, 55, TimeSpan.Zero);

        StubMetadata.Apply(item, Entry("Project Hail Mary", requested), Downloading, "Downloading — 60% — a few hours left");

        Assert.Equal("Project Hail Mary", item.Name);
        Assert.Equal("Downloading — 60% — a few hours left", item.Overview);
        Assert.Equal("Downloading 60%", item.Tagline);
        Assert.Equal(requested.UtcDateTime, item.DateCreated);
        Assert.Equal(2026, item.ProductionYear);
        Assert.Empty(item.ProviderIds);  // never let Seerr match a stub
        Assert.Empty(item.Genres);
        Assert.Equal(new[] { "Coming Soon" }, item.Tags);
        Assert.True(item.IsLocked);
        Assert.Equal(Enum.GetValues<MetadataField>().OrderBy(f => f), item.LockedFields.OrderBy(f => f));
    }

    [Fact]
    public void SortName_PutsNewestRequestFirst_UnderNameSort()
    {
        var older = StubMetadata.SortName(Entry("Aardvark", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));
        var newer = StubMetadata.SortName(Entry("Zebra", new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero)));
        var noDate = StubMetadata.SortName(Entry("Unknown", default) with { RequestedAt = null });

        var sorted = new[] { older, noDate, newer }.Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { newer, older, noDate }, sorted);
        Assert.EndsWith(" Zebra", newer, StringComparison.Ordinal);
    }
}
