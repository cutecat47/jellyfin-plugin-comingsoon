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
    private string? _root;
    private string? _warnedAbout;
    private bool _warnedRenderer;

    /// <summary>Original artwork kept next to the stub (dot-files: Jellyfin ignores them) so re-renders don't re-download.</summary>
    public static string ArtworkSourcePath(string mediaPath, ImageType type)
        => Path.Combine(Path.GetDirectoryName(mediaPath)!, type == ImageType.Backdrop ? ".backdrop-source" : ".poster-source");

    /// <summary>
    /// Saves the poster (Primary) and the 16:9 thumbnail (Thumb, used by Moonfin's thumbnail rows) with the
    /// status drawn on, plus the plain backdrop for detail pages. Falls back to plain artwork if drawing
    /// isn't possible. Returns the artwork URLs now cached locally.
    /// </summary>
    private async Task<(string? Poster, string? Backdrop)> SaveArtworkAsync(BaseItem item, string mediaPath, StubRecord record, DisplayState state, CancellationToken cancellationToken)
    {
        var entry = record.Entry;
        var posterPath = ArtworkSourcePath(mediaPath, ImageType.Primary);
        var backdropPath = ArtworkSourcePath(mediaPath, ImageType.Backdrop);
        var posterUrl = await CacheSourceAsync(entry.PosterUrl, record.AppliedPosterUrl, posterPath, "poster", entry, cancellationToken).ConfigureAwait(false);
        var backdropUrl = await CacheSourceAsync(entry.BackdropUrl, record.AppliedBackdropUrl, backdropPath, "backdrop", entry, cancellationToken).ConfigureAwait(false);

        // Plain backdrop for the detail page background.
        await SaveImageAsync(item, entry.BackdropUrl, record.AppliedBackdropUrl, ImageType.Backdrop, entry, cancellationToken).ConfigureAwait(false);

        var poster = await ReadIfExistsAsync(posterPath, cancellationToken).ConfigureAwait(false);
        var backdrop = await ReadIfExistsAsync(backdropPath, cancellationToken).ConfigureAwait(false);

        byte[] posterImage;
        byte[] thumbImage;
        try
        {
            posterImage = PosterRenderer.Render(poster, entry.DisplayName, state);
            thumbImage = PosterRenderer.RenderThumb(backdrop ?? poster, entry.DisplayName, state);
        }
        catch (Exception ex) when (ex is TypeInitializationException or DllNotFoundException or TypeLoadException or FileNotFoundException or FileLoadException or InvalidOperationException)
        {
            if (!_warnedRenderer)
            {
                logger.LogWarning("[ComingSoon] Can't draw progress onto posters ({Error:l}); using plain artwork instead", ex.Message);
                _warnedRenderer = true;
            }

            await SaveImageAsync(item, entry.PosterUrl, record.AppliedPosterUrl, ImageType.Primary, entry, cancellationToken).ConfigureAwait(false);
            return (posterUrl, backdropUrl);
        }

        using (var stream = new MemoryStream(posterImage))
        {
            await providerManager.SaveImage(item, stream, "image/jpeg", ImageType.Primary, null, cancellationToken).ConfigureAwait(false);
        }

        using (var stream = new MemoryStream(thumbImage))
        {
            await providerManager.SaveImage(item, stream, "image/jpeg", ImageType.Thumb, null, cancellationToken).ConfigureAwait(false);
        }

        return (posterUrl, backdropUrl);
    }

    private static async Task<byte[]?> ReadIfExistsAsync(string path, CancellationToken cancellationToken)
        => File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;

    /// <summary>Downloads artwork to <paramref name="path"/> if the URL changed or the file is missing. Returns the URL now cached.</summary>
    private async Task<string?> CacheSourceAsync(string? url, string? cachedUrl, string path, string what, TrackedEntry entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(url) || (url == cachedUrl && File.Exists(path)))
        {
            return cachedUrl;
        }

        try
        {
            using var http = httpClientFactory.CreateClient(NamedClient.Default);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var bytes = await http.GetByteArrayAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return url;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UriFormatException)
        {
            logger.LogWarning("[ComingSoon] Couldn't download {What:l} for '{Name:l}': {Error:l}", what, entry.DisplayName, ex.Message);
            return cachedUrl;
        }
    }


    public async Task<LibraryState> EnsureLibraryAsync(string root, string name, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var onRoot = FindVirtualFolders(root);
        if (onRoot.Count > 1)
        {
            logger.LogWarning(
                "[ComingSoon] {Count} libraries use the stub folder {Root:l} ({Names:l}); items will show twice. Delete all but one in Dashboard -> Libraries",
                onRoot.Count,
                root,
                string.Join(", ", onRoot.Select(f => "'" + f.Name + "'")));
        }

        var folder = onRoot.FirstOrDefault();
        var created = false;

        if (folder is null)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is not null && string.Equals(config.LibraryCreatedFor, root, StringComparison.Ordinal))
            {
                // Created once already: never create duplicates. The library was deleted or moved by hand.
                if (_warnedAbout != "missing")
                {
                    logger.LogWarning(
                        "[ComingSoon] No library uses the stub folder {Root:l} any more (libraries: {Libraries:l}). Add {Root2:l} to a Movies library, or clear 'Library created' in the plugin settings to let the plugin create one again",
                        root,
                        DescribeLibraries(),
                        root);
                    _warnedAbout = "missing";
                }

                return LibraryState.Unavailable;
            }

            logger.LogInformation("[ComingSoon] Creating library '{Name:l}' on {Root:l} (existing libraries: {Libraries:l})", name, root, DescribeLibraries());
            try
            {
                await libraryManager.AddVirtualFolder(name, CollectionTypeOptions.movies, NewLibraryOptions(root), refreshLibrary: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
            {
                logger.LogError("[ComingSoon] Couldn't create library '{Name:l}': {Error:l}. Create a Movies library on {Root:l} yourself, or change the library name in the plugin settings", name, ex.Message, root);
                return LibraryState.Unavailable;
            }

            folder = FindVirtualFolders(root).FirstOrDefault();
            created = true;
            if (folder is null)
            {
                logger.LogError("[ComingSoon] Library was created but can't be found on {Root:l}", root);
                return LibraryState.Unavailable;
            }

            if (config is not null)
            {
                config.LibraryCreatedFor = root;
                Plugin.Instance!.SaveConfiguration();
            }
        }

        _root = root;
        _libraryId = Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty;
        EnableNfoReader(folder);
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
        // Look the library up by folder every time: renaming a library gives it a new item id, and refreshing
        // the old (orphaned) item would save it back into the database as a phantom library.
        var current = _root is null ? null : FindVirtualFolders(_root).FirstOrDefault();
        if (current is not null && Guid.TryParse(current.ItemId, out var currentId))
        {
            _libraryId = currentId;
        }

        if (current is null
            || libraryManager.GetItemById(_libraryId) is not CollectionFolder library
            || !Directory.Exists(library.Path))
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
        var found = libraryManager.FindByPath(mediaPath, isFolder: false);
        if (found is null)
        {
            return null;
        }

        // FindByPath returns a fresh copy from the database; edit the instance Jellyfin serves to clients.
        var item = libraryManager.GetItemById(found.Id) ?? found;
        var entry = record.Entry;
        var (poster, backdrop) = await SaveArtworkAsync(item, mediaPath, record, state, cancellationToken).ConfigureAwait(false);

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

        // Local NFO stays enabled: the plugin writes one per stub (no ids in it) so scans keep its title/status.
    };

    private List<VirtualFolderInfo> FindVirtualFolders(string root)
    {
        var full = Normalize(root);
        return libraryManager.GetVirtualFolders()
            .Where(f => (f.Locations ?? []).Any(l => string.Equals(Normalize(l), full, StringComparison.Ordinal)))
            .ToList();
    }

    private string DescribeLibraries()
        => string.Join("; ", libraryManager.GetVirtualFolders().Select(f => $"'{f.Name}' = {string.Join(", ", f.Locations ?? [])}"));

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('/', '\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd('/', '\\');
        }
    }

    /// <summary>Libraries created by 0.3.x-0.4.1 had NFO reading off; the stub NFOs need it on.</summary>
    private void EnableNfoReader(VirtualFolderInfo folder)
    {
        if (libraryManager.GetItemById(_libraryId) is not CollectionFolder collection)
        {
            return;
        }

        var options = collection.GetLibraryOptions();
        var disabled = options.DisabledLocalMetadataReaders ?? [];
        if (!disabled.Contains("Nfo", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        options.DisabledLocalMetadataReaders = disabled.Where(r => !string.Equals(r, "Nfo", StringComparison.OrdinalIgnoreCase)).ToArray();
        collection.UpdateLibraryOptions(options);
        logger.LogInformation("[ComingSoon] Turned on NFO metadata for library '{Name:l}' (placeholder titles are stored in NFO files)", folder.Name);
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
