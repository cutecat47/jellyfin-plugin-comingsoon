using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComingSoon.Configuration;
using Jellyfin.Plugin.ComingSoon.Tracking;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComingSoon.Library;

/// <summary>
/// Turns tracker snapshots into stub folders, library items and metadata, and keeps the registry used
/// by the playback guard up to date. Driven by the single poll loop.
/// </summary>
public sealed class StubSync
{
    private static readonly TimeSpan LibraryRecheck = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PendingWarnAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PendingRescanAfter = TimeSpan.FromMinutes(2);

    private readonly IStubLibrary _library;
    private readonly StubRegistry _registry;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<byte[]> _video;

    private readonly Dictionary<string, StubRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedPending = new(StringComparer.Ordinal);

    private StubStore? _store;
    private bool _libraryReady;
    private DateTimeOffset _libraryCheckedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastScanAt = DateTimeOffset.MinValue;

    public StubSync(IStubLibrary library, StubRegistry registry, ILogger logger, TimeProvider time, Func<byte[]>? video = null)
    {
        _library = library;
        _registry = registry;
        _logger = logger;
        _time = time;
        _video = video ?? (() => StubVideo.Bytes);
    }

    public string? Root => _store?.Root;

    public bool LibraryReady => _libraryReady;

    public bool IsInitializedFor(string root) => _store is not null && string.Equals(_store.Root, Path.GetFullPath(root), StringComparison.Ordinal);

    /// <summary>Loads existing stubs (after a restart) and returns their entries for <see cref="ComingSoonTracker.Seed"/>.</summary>
    public IReadOnlyList<TrackedEntry> Initialize(PluginConfiguration config)
    {
        if (_store is not null)
        {
            _logger.LogWarning("[ComingSoon] Stub folder changed from {Old:l} to {New:l}; stubs in the old folder are left alone", _store.Root, Path.GetFullPath(config.StubFolderPath));
        }

        _store = new StubStore(config.StubFolderPath, _video);
        _registry.SetRoot(_store.Root);
        _records.Clear();
        _pending.Clear();
        _libraryReady = false;
        _libraryCheckedAt = DateTimeOffset.MinValue;

        var problems = new List<string>();
        var records = _store.LoadAll(problems);
        foreach (var problem in problems)
        {
            _logger.LogWarning("[ComingSoon] Ignoring unreadable stub {Problem:l}", problem);
        }

        var now = _time.GetUtcNow();
        foreach (var record in records)
        {
            _records[record.Entry.Key] = record;
            if (!record.MetadataApplied)
            {
                _pending[record.Entry.Key] = now;
            }

            UpdateRegistry(record.Entry, config, now);
        }

        _logger.LogInformation("[ComingSoon] Stub folder {Root:l}: {Count} existing stubs loaded", _store.Root, records.Count);
        return records.Select(r => r.Entry).ToList();
    }

    public async Task EnsureLibraryAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_store is null || now - _libraryCheckedAt < LibraryRecheck)
        {
            return;
        }

        _libraryCheckedAt = now;
        var wasReady = _libraryReady;
        var state = await _library.EnsureLibraryAsync(_store.Root, string.IsNullOrWhiteSpace(config.LibraryName) ? "Coming Soon" : config.LibraryName.Trim(), cancellationToken).ConfigureAwait(false);
        _libraryReady = state != LibraryState.Unavailable;

        if (_libraryReady && !wasReady)
        {
            // Pick up anything already on disk (cheap: one folder).
            await _library.ScanAsync(cancellationToken).ConfigureAwait(false);
        }

        if (state == LibraryState.Created)
        {
            // Brand-new library: its items have never had metadata written.
            foreach (var key in _records.Keys)
            {
                _pending.TryAdd(key, now);
            }
        }
    }

    public bool ExistsOutsideStubs(TrackedEntry entry) => _libraryReady && _library.ExistsOutsideStubs(entry);

    public async Task ApplyAsync(TrackerSnapshot snapshot, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var structureChanged = false;

        foreach (var removal in snapshot.Removals)
        {
            var key = removal.Entry.Key;
            try
            {
                if (_libraryReady)
                {
                    _library.RemoveItem(_store.MediaPathFor(key));
                }

                _store.Delete(key);
                _logger.LogInformation("[ComingSoon] Deleted stub '{Name:l}' ({Key:l})", removal.Entry.DisplayName, key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogError("[ComingSoon] Couldn't delete stub {Key:l}: {Error:l}", key, ex.Message);
            }

            _records.Remove(key);
            _pending.Remove(key);
            _warnedPending.Remove(key);
            _registry.Remove(key);
            structureChanged = true;
        }

        var updated = snapshot.Updates.ToDictionary(u => u.Entry.Key, StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries)
        {
            var key = entry.Key;
            var hasUpdate = updated.TryGetValue(key, out var update);
            var missing = !File.Exists(_store.MediaPathFor(key));
            if (!hasUpdate && !missing && _records.ContainsKey(key))
            {
                UpdateRegistry(entry, config, now);
                continue;
            }

            try
            {
                if (_store.EnsureStub(key))
                {
                    structureChanged = true;
                    _logger.LogInformation("[ComingSoon] Created stub '{Name:l}' ({Key:l})", entry.DisplayName, key);
                }

                _records.TryGetValue(key, out var previous);
                var state = StatusText.ToDisplayState(entry, config.PercentStep, now, _time.LocalTimeZone);
                var record = new StubRecord
                {
                    Entry = entry,
                    Overview = update?.Overview ?? previous?.Overview ?? StatusText.Overview(state),
                    MetadataApplied = false,
                    AppliedPosterUrl = previous?.AppliedPosterUrl,
                    AppliedBackdropUrl = previous?.AppliedBackdropUrl,
                };
                _store.Save(record);
                _records[key] = record;
                _pending.TryAdd(key, now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError("[ComingSoon] Couldn't write stub {Key:l} in {Root:l}: {Error:l}", key, _store.Root, ex.Message);
            }

            UpdateRegistry(entry, config, now);
        }

        if (!_libraryReady)
        {
            return;
        }

        // Rescan when stubs were added/removed, or periodically while some still aren't indexed.
        var stuck = _pending.Values.Any(since => now - since >= PendingRescanAfter);
        if (structureChanged || (stuck && now - _lastScanAt >= PendingRescanAfter))
        {
            _lastScanAt = now;
            await _library.ScanAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var (key, since) in _pending.ToList())
        {
            if (!_records.TryGetValue(key, out var record))
            {
                _pending.Remove(key);
                continue;
            }

            var state = StatusText.ToDisplayState(record.Entry, config.PercentStep, now, _time.LocalTimeZone);
            try
            {
                var applied = await _library.ApplyAsync(_store.MediaPathFor(key), record, state, cancellationToken).ConfigureAwait(false);
                if (applied is null)
                {
                    if (now - since >= PendingWarnAfter && _warnedPending.Add(key))
                    {
                        _logger.LogWarning("[ComingSoon] '{Name:l}' still isn't in the library after a scan; will keep retrying", record.Entry.DisplayName);
                    }

                    continue;
                }

                _records[key] = applied;
                _store.Save(applied);
                _pending.Remove(key);
                _warnedPending.Remove(key);
                if (config.VerboseLogging)
                {
                    _logger.LogInformation("[ComingSoon] Metadata written for '{Name:l}': {Overview:l}", record.Entry.DisplayName, applied.Overview);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[ComingSoon] Writing metadata for '{Name:l}' failed; will retry", record.Entry.DisplayName);
            }
        }
    }

    private void UpdateRegistry(TrackedEntry entry, PluginConfiguration config, DateTimeOffset now)
        => _registry.Upsert(new StubInfo(entry.Key, entry.DisplayName, StatusText.ToDisplayState(entry, config.PercentStep, now, _time.LocalTimeZone)));
}
