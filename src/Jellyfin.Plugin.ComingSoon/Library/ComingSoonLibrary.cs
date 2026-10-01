using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ComingSoon.Model;
using Jellyfin.Plugin.ComingSoon.Tracking;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using System.Net.Http;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComingSoon.Library;

public enum LibraryState
{
    Unavailable,
    Existing,
    Created
}

/// <summary>The Jellyfin side of the stubs; faked in tests.</summary>
public interface IStubLibrary
{
    /// <summary>Finds the library on <paramref name="root"/>, creating it if needed.</summary>
    Task<LibraryState> EnsureLibraryAsync(string root, string name, CancellationToken cancellationToken);

    /// <summary>Scans just the Coming Soon library so new/removed stubs show up.</summary>
    Task ScanAsync(CancellationToken cancellationToken);

    /// <summary>Writes metadata/artwork to the item at <paramref name="mediaPath"/>. Returns null if the item isn't in the library yet.</summary>
    Task<StubRecord?> ApplyAsync(string mediaPath, StubRecord record, DisplayState state, CancellationToken cancellationToken);

    void RemoveItem(string mediaPath);

    /// <summary>Whether the real movie / season exists in another library.</summary>
    bool ExistsOutsideStubs(TrackedEntry entry);
}

/// <summary>Jellyfin implementation of <see cref="IStubLibrary"/>.</summary>
public sealed class ComingSoonLibrary(
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IHttpClientFactory httpClientFactory,
    IFileSystem fileSystem,
    StubRegistry registry,
    ILogger<ComingSoonLibrary> logger) : IStubLibrary
{
    private static readonly TimeSpan ExistsCacheTtl = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (bool Exists, DateTimeOffset At)> _existsCache = new(StringComparer.Ordinal);
    private Guid _libraryId;
    private string? _warnedAbout;
    private bool _warnedRenderer;

    /// <summary>The original artwork, kept next to the stub (dot-file: Jellyfin ignores it) so re-renders don't re-download.</summary>
    public static string PosterSourcePath(string mediaPath) => Path.Combine(Path.GetDirectoryName(mediaPath)!, ".poster-source");

    /// <summary>
    /// Renders the status overlay onto the poster and saves it as the item's primary image. Falls back to
    /// the plain artwork if rendering isn't possible. Returns the artwork URL now cached locally.
    /// </summary>
    private async Task<string?> SavePosterAsync(BaseItem item, string mediaPath, StubRecord record, DisplayState state, CancellationToken cancellationToken)
    {
        var entry = record.Entry;
        var sourcePath = PosterSourcePath(mediaPath);
        var cachedUrl = record.AppliedPosterUrl;

        if (!string.IsNullOrEmpty(entry.PosterUrl) && (entry.PosterUrl != cachedUrl || !File.Exists(sourcePath)))
        {
            try
            {
                using var http = httpClientFactory.CreateClient(NamedClient.Default);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var bytes = await http.GetByteArrayAsync(new Uri(entry.PosterUrl), timeout.Token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(sourcePath, bytes, cancellationToken).ConfigureAwait(false);
                cachedUrl = entry.PosterUrl;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UriFormatException)
            {
                logger.LogWarning("[ComingSoon] Couldn't download poster for '{Name:l}': {Error:l}", entry.DisplayName, ex.Message);
            }
        }

        byte[]? source = File.Exists(sourcePath) ? await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false) : null;

        byte[] rendered;
        try
        {
            rendered = PosterRenderer.Render(source, entry.DisplayName, state);
        }
        catch (Exception ex) when (ex is TypeInitializationException or DllNotFoundException or TypeLoadException or FileNotFoundException or FileLoadException or InvalidOperationException)
        {
            if (!_warnedRenderer)
            {
                logger.LogWarning("[ComingSoon] Can't draw progress onto posters ({Error:l}); using plain artwork instead", ex.Message);
                _warnedRenderer = true;
            }

            return await SaveImageAsync(item, entry.PosterUrl, record.AppliedPosterUrl, ImageType.Primary, entry, cancellationToken).ConfigureAwait(false);
        }

        using var stream = new MemoryStream(rendered);
        await providerManager.SaveImage(item, stream, "image/jpeg", ImageType.Primary, null, cancellationToken).ConfigureAwait(false);
        return cachedUrl;
    }

    public async Task<LibraryState> EnsureLibraryAsync(string root, string name, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var folder = FindVirtualFolder(root);
        var created = false;

        if (folder is null)
        {
            logger.LogInformation("[ComingSoon] Creating library '{Name:l}' on {Root:l}", name, root);
            try
            {
                await libraryManager.AddVirtualFolder(name, CollectionTypeOptions.movies, NewLibraryOptions(root), refreshLibrary: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
            {
                logger.LogError("[ComingSoon] Couldn't create library '{Name:l}': {Error:l}. Create a Movies library on {Root:l} yourself, or change the library name in the plugin settings", name, ex.Message, root);
                return LibraryState.Unavailable;
            }

            folder = FindVirtualFolder(root);
            created = true;
            if (folder is null)
            {
                logger.LogError("[ComingSoon] Library was created but can't be found on {Root:l}", root);
                return LibraryState.Unavailable;
            }
        }

        _libraryId = Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty;
        WarnAboutSettings(folder);
        return _libraryId == Guid.Empty ? LibraryState.Unavailable : created ? LibraryState.Created : LibraryState.Existing;
    }

    /// <summary>
    /// Scans only the Coming Soon library. Mirrors Jellyfin's own per-library "Scan library"
    /// (ProviderManager.RefreshCollectionFolderChildren): a CollectionFolder's ValidateChildren is a
    /// deliberate no-op, the items live under its physical folder(s), so those are what get validated.
    /// </summary>
    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (libraryManager.GetItemById(_libraryId) is not CollectionFolder library)
        {
            logger.LogWarning("[ComingSoon] Coming Soon library not found; skipping scan");
            return;
        }

        var options = new MetadataRefreshOptions(new DirectoryService(fileSystem));

        // Refreshing the collection folder also re-links its physical folders (PhysicalFolderIds).
        await library.RefreshMetadata(options, cancellationToken).ConfigureAwait(false);
        var physical = library.GetPhysicalFolders().ToList();

        if (physical.Count == 0)
        {
            // Brand-new library: make sure the top-level folder items exist, then link again.
            await libraryManager.ValidateTopLibraryFolders(cancellationToken).ConfigureAwait(false);
            await library.RefreshMetadata(options, cancellationToken).ConfigureAwait(false);
            physical = library.GetPhysicalFolders().ToList();
        }

        if (physical.Count == 0)
        {
            physical = library.PhysicalLocations
                .Select(path => libraryManager.FindByPath(path, isFolder: true))
                .OfType<Folder>()
                .ToList();
        }

        if (physical.Count == 0)
        {
            logger.LogWarning("[ComingSoon] Library '{Name:l}' has no folder items yet; queueing a full library scan", library.Name);
            libraryManager.QueueLibraryScan();
            return;
        }

        foreach (var folder in physical)
        {
            await folder.RefreshMetadata(options, cancellationToken).ConfigureAwait(false);
            await folder.ValidateChildren(new Progress<double>(), options, recursive: true, allowRemoveRoot: false, cancellationToken).ConfigureAwait(false);
        }

        if (Plugin.Instance?.Configuration.VerboseLogging == true)
        {
            var count = physical.Sum(f => f.Children.Count());
            logger.LogInformation("[ComingSoon] Scanned {Folders} folder(s) of library '{Name:l}': {Count} items", physical.Count, library.Name, count);
        }
    }

    public async Task<StubRecord?> ApplyAsync(string mediaPath, StubRecord record, DisplayState state, CancellationToken cancellationToken)
    {
        var item = libraryManager.FindByPath(mediaPath, isFolder: false);
        if (item is null)
        {
            return null;
        }

        var entry = record.Entry;
        var poster = await SavePosterAsync(item, mediaPath, record, state, cancellationToken).ConfigureAwait(false);
        var backdrop = await SaveImageAsync(item, entry.BackdropUrl, record.AppliedBackdropUrl, ImageType.Backdrop, entry, cancellationToken).ConfigureAwait(false);

        StubMetadata.Apply(item, entry, state, record.Overview ?? StatusText.Overview(state));
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

        return record with { MetadataApplied = true, AppliedPosterUrl = poster, AppliedBackdropUrl = backdrop };
    }

    public void RemoveItem(string mediaPath)
    {
        var item = libraryManager.FindByPath(mediaPath, isFolder: false);
        if (item is not null)
        {
            libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, notifyParentItem: true);
        }
    }

    public bool ExistsOutsideStubs(TrackedEntry entry)
    {
        var now = DateTimeOffset.UtcNow;
        if (_existsCache.TryGetValue(entry.Key, out var cached) && now - cached.At < ExistsCacheTtl)
        {
            return cached.Exists;
        }

        bool exists;
        try
        {
            exists = entry.Kind == MediaKind.Movie ? MovieExists(entry) : SeasonExists(entry);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning("[ComingSoon] Library lookup for {Key:l} failed: {Error:l}", entry.Key, ex.Message);
            exists = false;
        }

        _existsCache[entry.Key] = (exists, now);
        return exists;
    }

    private static LibraryOptions NewLibraryOptions(string root) => new()
    {
        PathInfos = [new MediaPathInfo(root)],
        EnableRealtimeMonitor = false,
        EnablePhotos = false,
        EnableChapterImageExtraction = false,
        ExtractChapterImagesDuringLibraryScan = false,
        EnableTrickplayImageExtraction = false,
        ExtractTrickplayImagesDuringLibraryScan = false,
        EnableEmbeddedTitles = false,
        SaveLocalMetadata = false,
        AutomaticRefreshIntervalDays = 0,
        AutomaticallyAddToCollection = false,

        // No internet metadata or artwork: a provider matching "Severance - Season 2" by name would attach
        // real TMDB/IMDb ids, and Seerr's Jellyfin sync would then mark the request as available.
        TypeOptions =
        [
            new TypeOptions
            {
                Type = "Movie",
                MetadataFetchers = [],
                MetadataFetcherOrder = [],
                ImageFetchers = [],
                ImageFetcherOrder = [],
            },
        ],
        DisabledLocalMetadataReaders = ["Nfo"],
    };

    private VirtualFolderInfo? FindVirtualFolder(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd('/', '\\');
        return libraryManager.GetVirtualFolders().FirstOrDefault(f =>
            f.Locations.Any(l => string.Equals(Path.GetFullPath(l).TrimEnd('/', '\\'), full, StringComparison.Ordinal)));
    }

    private void WarnAboutSettings(VirtualFolderInfo folder)
    {
        var problems = new List<string>();
        if (folder.CollectionType is not CollectionTypeOptions.movies)
        {
            problems.Add($"content type is '{folder.CollectionType?.ToString() ?? "mixed"}' (Movies is expected)");
        }

        var movieOptions = folder.LibraryOptions?.TypeOptions?.FirstOrDefault(t => t.Type == "Movie");
        if (movieOptions is null || movieOptions.MetadataFetchers.Length > 0 || movieOptions.ImageFetchers.Length > 0)
        {
            problems.Add("internet metadata/image downloaders are enabled (untick them all in the library's settings, or Seerr may see stubs as available)");
        }

        if (folder.Locations.Length > 1)
        {
            problems.Add("it has more than one folder");
        }

        var summary = string.Join("; ", problems);
        if (problems.Count > 0 && summary != _warnedAbout)
        {
            logger.LogWarning("[ComingSoon] Library '{Name:l}' needs attention: {Problems:l}", folder.Name, summary);
        }

        _warnedAbout = summary;
    }

    private async Task<string?> SaveImageAsync(BaseItem item, string? url, string? applied, ImageType type, TrackedEntry entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(url))
        {
            return applied;
        }

        if (url == applied && item.HasImage(type, 0))
        {
            return applied;
        }

        try
        {
            await providerManager.SaveImage(item, url, type, null, cancellationToken).ConfigureAwait(false);
            return url;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidOperationException)
        {
            logger.LogWarning("[ComingSoon] Couldn't download {Type} image for '{Name:l}': {Error:l}", type.ToString(), entry.DisplayName, ex.Message);
            return applied;
        }
    }

    private bool MovieExists(TrackedEntry entry)
    {
        if (entry.TmdbId is not int tmdb)
        {
            return false;
        }

        var items = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            Recursive = true,
            IsVirtualItem = false,
        });

        return items.Any(i => !registry.IsStubPath(i.Path));
    }

    private bool SeasonExists(TrackedEntry entry)
    {
        if (entry.SeasonNumber is not int season)
        {
            return false;
        }

        var ids = new Dictionary<string, string>();
        if (entry.TvdbId is int tvdb)
        {
            ids[MetadataProvider.Tvdb.ToString()] = tvdb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (entry.TmdbId is int tmdb)
        {
            ids[MetadataProvider.Tmdb.ToString()] = tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (ids.Count == 0)
        {
            return false;
        }

        var series = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            HasAnyProviderId = ids,
            Recursive = true,
        }).Where(s => !registry.IsStubPath(s.Path)).ToList();

        if (series.Count == 0)
        {
            return false;
        }

        var episodes = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = series.Select(s => s.Id).ToArray(),
            ParentIndexNumber = season,
            IsVirtualItem = false,
            Recursive = true,
        });

        // Everything that has aired must be there (falls back to "at least one" when Sonarr didn't say).
        var have = episodes.Select(e => e.IndexNumber ?? -1).Where(n => n >= 0).Distinct().Count();
        var need = Math.Max(1, entry.AiredEpisodes ?? 1);
        return have >= need;
    }
}
