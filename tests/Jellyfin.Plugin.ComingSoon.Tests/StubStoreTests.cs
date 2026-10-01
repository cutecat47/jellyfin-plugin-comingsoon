using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ComingSoon.Library;
using Jellyfin.Plugin.ComingSoon.Model;
using Jellyfin.Plugin.ComingSoon.Tracking;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

public sealed class StubStoreTests : IDisposable
{
    private static readonly byte[] Video = Encoding.ASCII.GetBytes("fake-video");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Theory]
    [InlineData("movie-tmdb687163", "cs-movie-687163")]
    [InlineData("tv-tvdb371980-s02", "cs-tv-371980-s02")]
    [InlineData("tv-tmdb95396-s00", "cs-tvt-95396-s00")]
    [InlineData("Weird Key/../[x]", "cs-weird-key-x")]
    public void FolderName_IsSafe(string key, string expected)
        => Assert.Equal(expected, StubStore.FolderName(key));

    [Theory]
    [InlineData("movie-tmdb687163")]
    [InlineData("movie-tmdb1234567")]
    [InlineData("tv-tvdb371980-s02")]
    [InlineData("tv-tmdb95396-s10")]
    public void FolderName_NeverLooksLikeAProviderId(string key)
    {
        // Seerr's Jellyfin sync matches on provider ids; Jellyfin reads them from names like
        // "[tmdbid-1]", "{tvdb-1}" or "tt1234567". None of that may appear.
        var name = StubStore.FolderName(key);
        Assert.DoesNotMatch(new Regex("tmdb|tvdb|imdb|anidb|[\\[\\]{}=]|tt\\d{7}", RegexOptions.IgnoreCase), name);
    }

    [Fact]
    public void EnsureStub_CreatesOnce()
    {
        var store = new StubStore(_root, () => Video);

        Assert.True(store.EnsureStub("movie-tmdb1"));
        Assert.False(store.EnsureStub("movie-tmdb1"));
        Assert.Equal(Video, File.ReadAllBytes(store.MediaPathFor("movie-tmdb1")));
        Assert.EndsWith(Path.Combine("cs-movie-1", "cs-movie-1.mp4"), store.MediaPathFor("movie-tmdb1"), StringComparison.Ordinal);
    }

    [Fact]
    public void Records_RoundTrip()
    {
        var store = new StubStore(_root, () => Video);
        var entry = new TrackedEntry
        {
            Key = "tv-tvdb371980-s02",
            Kind = MediaKind.Series,
            TvdbId = 371980,
            TmdbId = 95396,
            SeasonNumber = 2,
            Title = "Severance",
            Year = 2022,
            Status = TrackedStatus.Downloading,
            Percent = 42.857,
            TimeLeft = TimeSpan.FromMinutes(42),
            RequestedAt = new DateTimeOffset(2026, 9, 30, 23, 5, 0, TimeSpan.Zero),
            PosterUrl = "https://image.tmdb.org/t/p/w780/x.jpg",
            InQueue = true,
            InSeerr = true,
            AiredEpisodes = 10,
        };
        store.EnsureStub(entry.Key);
        store.Save(new StubRecord { Entry = entry, Overview = "Downloading — 40%", MetadataApplied = true, AppliedPosterUrl = entry.PosterUrl });

        var loaded = Assert.Single(store.LoadAll());
        Assert.Equal(entry, loaded.Entry);
        Assert.Equal("Downloading — 40%", loaded.Overview);
        Assert.True(loaded.MetadataApplied);
        Assert.Contains("\"Status\": \"Downloading\"", File.ReadAllText(Path.Combine(store.FolderFor(entry.Key), StubStore.SidecarName)), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAll_SkipsBrokenSidecars_AndForeignFolders()
    {
        var store = new StubStore(_root, () => Video);
        Directory.CreateDirectory(Path.Combine(_root, "cs-broken"));
        File.WriteAllText(Path.Combine(_root, "cs-broken", StubStore.SidecarName), "{ not json");
        Directory.CreateDirectory(Path.Combine(_root, "cs-nosidecar"));
        Directory.CreateDirectory(Path.Combine(_root, "Someone Elses Movie (2020)"));
        store.Save(new StubRecord { Entry = new TrackedEntry { Key = "movie-tmdb5", Kind = MediaKind.Movie } });
        File.Move(Path.Combine(store.FolderFor("movie-tmdb5"), StubStore.SidecarName), Path.Combine(_root, "cs-nosidecar", StubStore.SidecarName));

        var problems = new List<string>();
        Assert.Empty(store.LoadAll(problems));
        Assert.Equal(3, problems.Count); // broken json, missing sidecar, sidecar in the wrong folder
    }

    [Fact]
    public void WriteNfo_NextToVideo_OnlyWhenChanged()
    {
        var store = new StubStore(_root, () => Video);
        store.EnsureStub("movie-tmdb3");

        Assert.True(store.WriteNfo("movie-tmdb3", "<movie>a</movie>"));
        Assert.False(store.WriteNfo("movie-tmdb3", "<movie>a</movie>"));
        Assert.True(store.WriteNfo("movie-tmdb3", "<movie>b</movie>"));
        Assert.Equal(Path.Combine(store.FolderFor("movie-tmdb3"), "cs-movie-3.nfo"), store.NfoPathFor("movie-tmdb3"));
        Assert.Equal("<movie>b</movie>", File.ReadAllText(store.NfoPathFor("movie-tmdb3")));
        Assert.False(File.Exists(Path.Combine(store.FolderFor("movie-tmdb3"), ".nfo.tmp")));
    }

    [Fact]
    public void Delete_RemovesFolder()
    {
        var store = new StubStore(_root, () => Video);
        store.EnsureStub("movie-tmdb9");
        store.Delete("movie-tmdb9");
        store.Delete("movie-tmdb9"); // idempotent
        Assert.False(Directory.Exists(store.FolderFor("movie-tmdb9")));
    }

    [Fact]
    public void BundledVideo_IsSmallMp4()
    {
        var bytes = StubVideo.Bytes;
        Assert.InRange(bytes.Length, 1000, 64 * 1024);
        Assert.Equal("ftyp", Encoding.ASCII.GetString(bytes, 4, 4));
    }

    [Fact]
    public void Registry_RecognisesOnlyPathsInsideTheStubFolder()
    {
        var registry = new StubRegistry();
        registry.SetRoot(_root);
        var store = new StubStore(_root, () => Video);
        registry.Upsert(new StubInfo("movie-tmdb7", "Seven", new DisplayState(TrackedStatus.Searching, null, EtaBucket.Unknown, null, null)));

        Assert.True(registry.IsStubPath(store.MediaPathFor("movie-tmdb7")));
        Assert.False(registry.IsStubPath(_root));
        Assert.False(registry.IsStubPath(_root + "-old" + Path.DirectorySeparatorChar + "x.mp4"));
        Assert.False(registry.IsStubPath(null));
        Assert.Equal("Seven", registry.Find(store.MediaPathFor("movie-tmdb7"))!.DisplayName);
        Assert.Null(registry.Find(store.MediaPathFor("movie-tmdb8")));
    }
}
