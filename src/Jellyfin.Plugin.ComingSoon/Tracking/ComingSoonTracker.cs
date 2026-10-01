using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComingSoon.Clients;
using Jellyfin.Plugin.ComingSoon.Configuration;
using Jellyfin.Plugin.ComingSoon.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComingSoon.Tracking;

/// <summary>An entry whose visible state changed and should be written to the library now.</summary>
public sealed record EntryUpdate(TrackedEntry Entry, DisplayState State, string Overview, bool IsNew);

/// <summary>An entry whose stub should be deleted.</summary>
public sealed record EntryRemoval(TrackedEntry Entry, RemovalDecision Reason);

/// <summary>Result of one poll.</summary>
public sealed record TrackerSnapshot(
    IReadOnlyList<TrackedEntry> Entries,
    IReadOnlyList<EntryUpdate> Updates,
    IReadOnlyList<EntryRemoval> Removals,
    bool AllSourcesHealthy);

/// <summary>
/// Polls Radarr, Sonarr and Seerr, merges the results and works out which items to add,
/// update or remove. Holds last-known state so a source outage doesn't empty the library.
/// Not thread-safe: driven by a single poll loop.
/// </summary>
public sealed class ComingSoonTracker
{
    /// <summary>Seerr's request list fans out to every *arr server, so it's polled less often.</summary>
    public static readonly TimeSpan SeerrMinInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan DetailsTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan ReleaseTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AvailabilityTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LookupFailureBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NotFoundRecheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SummaryHeartbeat = TimeSpan.FromMinutes(5);
    private const int MaxLookupsPerPoll = 10;

    private readonly IServiceClientFactory _clients;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly UpdateThrottle _throttle = new();

    private readonly SourceState<QueueRecord> _radarr = new("Radarr");
    private readonly SourceState<QueueRecord> _sonarr = new("Sonarr");
    private readonly SourceState<SeerrRequest> _seerr = new("Seerr");

    private readonly Dictionary<(MediaKind, int), Cached<SeerrDetails?>> _details = [];
    private readonly Dictionary<int, Cached<MovieReleaseInfo?>> _movies = [];
    private readonly Dictionary<int, Cached<SeriesReleaseInfo?>> _series = [];
    private readonly Dictionary<string, DateTimeOffset> _missingSince = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedDeadGrabs = new(StringComparer.Ordinal);

    private Dictionary<string, TrackedEntry> _entries = new(StringComparer.Ordinal);
    private DateTimeOffset _lastSeerrPoll = DateTimeOffset.MinValue;
    private string? _lastSummary;
    private DateTimeOffset _lastSummaryAt = DateTimeOffset.MinValue;
    private int _lookupsThisPoll;

    public ComingSoonTracker(IServiceClientFactory clients, ILogger logger, TimeProvider time)
    {
        _clients = clients;
        _logger = logger;
        _time = time;
    }

    /// <summary>Gets the entries from the latest poll (last-known state included).</summary>
    public IReadOnlyDictionary<string, TrackedEntry> Current => _entries;

    /// <summary>
    /// Pre-loads entries (e.g. read back from existing stubs after a restart) so they are matched
    /// instead of duplicated, and are only removed by the normal rules.
    /// </summary>
    public void Seed(IEnumerable<TrackedEntry> entries, TimeZoneInfo zone, int percentStep)
    {
        var now = _time.GetUtcNow();
        foreach (var e in entries)
        {
            _entries[e.Key] = e;
            _throttle.MarkPushed(e.Key, StatusText.ToDisplayState(e, percentStep, now, zone), now - UpdateThrottle.MinInterval);
        }
    }

    public async Task<TrackerSnapshot> PollAsync(
        PluginConfiguration config,
        Func<TrackedEntry, bool>? existsInJellyfin,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var verbose = config.VerboseLogging;
        _lookupsThisPoll = 0;

        var radarr = Endpoint(_radarr, "Radarr", config.RadarrUrl, config.RadarrApiKey);
        var sonarr = Endpoint(_sonarr, "Sonarr", config.SonarrUrl, config.SonarrApiKey);
        var seerr = Endpoint(_seerr, "Seerr", config.SeerrUrl, config.SeerrApiKey);

        var radarrClient = radarr is null ? null : _clients.CreateRadarr(radarr);
        var sonarrClient = sonarr is null ? null : _clients.CreateSonarr(sonarr);
        var seerrClient = seerr is null ? null : _clients.CreateSeerr(seerr);

        if (radarrClient is not null)
        {
            await RefreshAsync(_radarr, ct => radarrClient.GetQueueAsync(ct), cancellationToken).ConfigureAwait(false);
        }

        if (sonarrClient is not null)
        {
            await RefreshAsync(_sonarr, ct => sonarrClient.GetQueueAsync(ct), cancellationToken).ConfigureAwait(false);
        }

        var seerrInterval = TimeSpan.FromSeconds(Math.Max(SeerrMinInterval.TotalSeconds, config.PollIntervalSeconds));
        if (seerrClient is not null && (now - _lastSeerrPoll >= seerrInterval || !_seerr.Healthy))
        {
            _lastSeerrPoll = now;
            await RefreshAsync(_seerr, ct => seerrClient.GetActiveRequestsAsync(ct), cancellationToken).ConfigureAwait(false);
        }

        if (verbose)
        {
            LogRawRecords(_radarr);
            LogRawRecords(_sonarr);
        }

        var queue = _radarr.Data.Concat(_sonarr.Data).ToList();
        var requests = _seerr.Data;
        WarnAboutDeadGrabs(queue, now);

        // Seerr details: titles/artwork (and tvdbIds) for requests.
        if (seerrClient is not null)
        {
            foreach (var r in requests)
            {
                await GetDetailsAsync(seerrClient, r.Kind, r.TmdbId, DetailsTtl, cancellationToken).ConfigureAwait(false);
            }
        }

        // Release info, only for requests that aren't downloading (Searching vs Waiting for release).
        var queuedMovies = queue.Where(q => q.Kind == MediaKind.Movie).Select(q => q.TmdbId).ToHashSet();
        foreach (var r in requests)
        {
            if (r.Kind == MediaKind.Movie && radarrClient is not null && !queuedMovies.Contains(r.TmdbId))
            {
                await GetCachedAsync(_movies, r.TmdbId, ct => radarrClient.GetMovieAsync(r.TmdbId, now, ct)!, "Radarr movie lookup", cancellationToken).ConfigureAwait(false);
            }
            else if (r.Kind == MediaKind.Series && sonarrClient is not null
                && (r.TvdbId ?? CachedDetails(MediaKind.Series, r.TmdbId)?.TvdbId) is int tvdb)
            {
                await GetCachedAsync(_series, tvdb, ct => sonarrClient.GetSeriesAsync(tvdb, ct)!, "Sonarr series lookup", cancellationToken).ConfigureAwait(false);
            }
        }

        var built = EntryBuilder.Build(new MergeInput
        {
            QueueRecords = queue,
            SeerrRequests = requests,
            Details = Values(_details),
            Movies = Values(_movies),
            Series = Values(_series),
            Now = now,
        });

        var allHealthy = _radarr.Healthy && _sonarr.Healthy && _seerr.Healthy;
        var current = new Dictionary<string, TrackedEntry>(StringComparer.Ordinal);
        var removals = new List<EntryRemoval>();

        foreach (var entry in built)
        {
            // Requested in Seerr, but Sonarr/Radarr dropped or unmonitored it: nothing will ever download it.
            if (entry.NotInArr && !entry.InQueue)
            {
                if (!_entries.ContainsKey(entry.Key))
                {
                    continue; // never shown; don't start showing it
                }

                if (!_missingSince.TryGetValue(entry.Key, out var since))
                {
                    since = now;
                    _missingSince[entry.Key] = since;
                    _logger.LogInformation(
                        "[ComingSoon] '{Name:l}' is still requested in Seerr but {App:l} isn't downloading it (removed or unmonitored); its stub goes in {Minutes} minutes unless that changes",
                        entry.DisplayName,
                        entry.Kind == MediaKind.Series ? "Sonarr" : "Radarr",
                        (int)RemovalPolicy.AbandonGrace.TotalMinutes);
                }

                var unmanaged = RemovalPolicy.Decide(
                    new RemovalFacts
                    {
                        InJellyfinLibrary = existsInJellyfin?.Invoke(entry) == true,
                        AllSourcesHealthy = allHealthy,
                        MissingSince = since,
                    },
                    now);

                if (unmanaged == RemovalDecision.Keep)
                {
                    current[entry.Key] = entry;
                }
                else
                {
                    removals.Add(new EntryRemoval(entry, unmanaged));
                }

                continue;
            }

            _missingSince.Remove(entry.Key);
            var decision = RemovalPolicy.Decide(
                new RemovalFacts
                {
                    InQueue = entry.InQueue,
                    InSeerr = entry.InSeerr,
                    InJellyfinLibrary = !entry.InQueue && existsInJellyfin?.Invoke(entry) == true,
                    AllSourcesHealthy = allHealthy,
                },
                now);

            if (decision == RemovalDecision.Keep)
            {
                current[entry.Key] = entry;
            }
            else if (_entries.ContainsKey(entry.Key))
            {
                removals.Add(new EntryRemoval(entry, decision));
            }
        }

        // Entries that left every source: keep last-known state until the removal rules say otherwise.
        foreach (var previous in _entries.Values.Where(e => !current.ContainsKey(e.Key) && removals.All(r => r.Entry.Key != e.Key)))
        {
            if (!_missingSince.TryGetValue(previous.Key, out var since))
            {
                since = now;
                _missingSince[previous.Key] = since;
            }

            bool? available = null;
            if (seerrClient is not null && previous.TmdbId is int tmdb)
            {
                var details = await GetDetailsAsync(seerrClient, previous.Kind, tmdb, AvailabilityTtl, cancellationToken).ConfigureAwait(false);
                available = IsAvailable(details, previous);
            }

            var decision = RemovalPolicy.Decide(
                new RemovalFacts
                {
                    SeerrAvailable = available,
                    InJellyfinLibrary = existsInJellyfin?.Invoke(previous) == true,
                    AllSourcesHealthy = allHealthy,
                    MissingSince = since,
                },
                now);

            if (decision == RemovalDecision.Keep)
            {
                current[previous.Key] = previous with { InQueue = false, InSeerr = false };
            }
            else
            {
                removals.Add(new EntryRemoval(previous, decision));
            }
        }

        foreach (var removal in removals)
        {
            _throttle.Forget(removal.Entry.Key);
            _missingSince.Remove(removal.Entry.Key);
            _logger.LogInformation(
                "[ComingSoon] Removing '{Name:l}' ({Key:l}): {Reason:l}",
                removal.Entry.DisplayName,
                removal.Entry.Key,
                removal.Reason == RemovalDecision.RemoveArrived ? "now available" : "no longer requested, queued or monitored");
        }

        var updates = new List<EntryUpdate>();
        foreach (var entry in current.Values)
        {
            var state = StatusText.ToDisplayState(entry, config.PercentStep, now, zone);
            if (!_throttle.ShouldPush(entry.Key, state, now, TimeSpan.FromSeconds(Math.Clamp(config.MinUpdateIntervalSeconds, 10, 3600))))
            {
                continue;
            }

            var isNew = !_throttle.TryGetLast(entry.Key, out _);
            _throttle.MarkPushed(entry.Key, state, now);
            var overview = StatusText.Overview(state);
            updates.Add(new EntryUpdate(entry, state, overview, isNew));
            _logger.LogInformation("[ComingSoon] {Change:l} '{Name:l}' ({Key:l}): {Overview:l}", isNew ? "Tracking" : "Update", entry.DisplayName, entry.Key, overview);
        }

        _entries = current;
        var ordered = current.Values
            .OrderByDescending(e => e.RequestedAt ?? DateTimeOffset.MinValue)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToList();

        var summary = string.Create(CultureInfo.InvariantCulture, $"{_radarr.Data.Count}|{_sonarr.Data.Count}|{_seerr.Data.Count}|{Summarise(ordered)}|{updates.Count}|{removals.Count}");
        if (verbose && (summary != _lastSummary || now - _lastSummaryAt >= SummaryHeartbeat))
        {
            _lastSummary = summary;
            _lastSummaryAt = now;
            _logger.LogInformation(
                "[ComingSoon] Poll: {Radarr} Radarr records, {Sonarr} Sonarr records, {Seerr} Seerr requests -> {Count} items ({Summary:l}); {Updates} updates, {Removals} removals in {Ms} ms",
                _radarr.Data.Count,
                _sonarr.Data.Count,
                _seerr.Data.Count,
                ordered.Count,
                Summarise(ordered),
                updates.Count,
                removals.Count,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return new TrackerSnapshot(ordered, updates, removals, allHealthy);
    }

    /// <summary>
    /// One line per raw queue record, so a pasted log shows exactly what Radarr/Sonarr reported.
    /// Only logged when something visible changes, not on every poll.
    /// </summary>
    internal static string DescribeRecord(QueueRecord r)
    {
        var name = r.Kind == MediaKind.Series
            ? string.Create(CultureInfo.InvariantCulture, $"{r.Title} S{r.SeasonNumber:00}E{r.EpisodeNumber:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{r.Title} ({r.Year})");
        var percent = r.Size > 0 ? ((r.Size - r.SizeLeft) / r.Size * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "?%";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name} | status={r.Status} state={r.TrackedState ?? "-"} tracked={r.TrackedStatus ?? "-"}{(r.IsUpgrade ? " UPGRADE(ignored)" : string.Empty)} | released={r.ReleaseDate?.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture) ?? "-"}{(r.MovieAvailable == false ? " (not available yet)" : string.Empty)} | {percent} of {r.Size / 1073741824:0.0} GiB, timeleft={r.TimeLeft?.ToString() ?? "-"} | download={r.DownloadId ?? "-"} | {r.Message ?? "no message"}");
    }

    /// <summary>Tells the admin (once per download) about grabs that will never import.</summary>
    private void WarnAboutDeadGrabs(List<QueueRecord> queue, DateTimeOffset now)
    {
        var dead = queue.Where(r => !r.IsUnreleased(now) && QueueAggregator.IsDeadGrab(r)).ToList();
        _warnedDeadGrabs.IntersectWith(dead.Select(r => r.DownloadKey));
        foreach (var r in dead.Where(r => _warnedDeadGrabs.Add(r.DownloadKey)))
        {
            var app = r.Kind == MediaKind.Series ? "Sonarr" : "Radarr";
            var name = r.Kind == MediaKind.Series
                ? string.Create(CultureInfo.InvariantCulture, $"{r.Title} S{r.SeasonNumber:00}E{r.EpisodeNumber:00}")
                : r.Title ?? "Unknown";
            _logger.LogWarning(
                "[ComingSoon] {Name:l}: {App:l} grabbed a release with no usable files ({Message:l}). Showing it as Searching - remove or blocklist it in {App2:l}'s queue so a real release can be grabbed",
                name,
                app,
                r.Message ?? "0 bytes",
                app);
        }
    }

    private void LogRawRecords(SourceState<QueueRecord> state)
    {
        var signature = string.Join(
            '\n',
            state.Data.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.RecordId}|{r.Status}|{r.TrackedState}|{r.TrackedStatus}|{StatusText.StepPercent(r.Size > 0 ? (r.Size - r.SizeLeft) / r.Size * 100 : null, 10)}|{r.Message}")));
        if (signature == state.LoggedSignature)
        {
            return;
        }

        state.LoggedSignature = signature;
        _logger.LogInformation("[ComingSoon] {Service:l} queue now has {Count} records:", state.Name, state.Data.Count);
        foreach (var r in state.Data)
        {
            _logger.LogInformation("[ComingSoon]   {Service:l} record: {Line:l}", state.Name, DescribeRecord(r));
        }
    }

    private static bool? IsAvailable(SeerrDetails? details, TrackedEntry entry)
    {
        if (details is null)
        {
            return null;
        }

        if (details.MediaStatus == SeerrMediaStatus.Available)
        {
            return true;
        }

        if (entry.Kind == MediaKind.Series && entry.SeasonNumber is int s)
        {
            return details.SeasonStatus.GetValueOrDefault(s) == SeerrMediaStatus.Available
                || details.SeasonStatus4k.GetValueOrDefault(s) == SeerrMediaStatus.Available;
        }

        return details.MediaStatus4k == SeerrMediaStatus.Available;
    }

    private static string Summarise(IEnumerable<TrackedEntry> entries)
    {
        var parts = entries.GroupBy(e => e.Status).OrderBy(g => g.Key).Select(g => $"{g.Count()} {StatusText.StatusLabel(g.Key).ToLowerInvariant()}");
        var text = string.Join(", ", parts);
        return text.Length == 0 ? "none" : text;
    }

    private static Dictionary<TKey, TValue> Values<TKey, TValue>(Dictionary<TKey, Cached<TValue?>> cache)
        where TKey : notnull
        where TValue : class
        => cache.Where(kv => kv.Value.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Value!);

    private ServiceEndpoint? Endpoint<T>(SourceState<T> state, string name, string url, string key)
    {
        var endpoint = ServiceEndpoint.TryCreate(name, url, key, out var error);
        if (endpoint is null)
        {
            if (error != "Not configured" && state.LastError != error)
            {
                _logger.LogWarning("[ComingSoon] {Service:l} settings invalid: {Error:l}", name, error);
            }

            state.Configured = false;
            state.Healthy = error == "Not configured";
            state.LastError = error;
            state.Data = [];
        }
        else
        {
            state.Configured = true;
        }

        return endpoint;
    }

    private async Task RefreshAsync<T>(SourceState<T> state, Func<CancellationToken, Task<IReadOnlyList<T>>> fetch, CancellationToken cancellationToken)
    {
        try
        {
            state.Data = await fetch(cancellationToken).ConfigureAwait(false);
            if (!state.Healthy && state.LastError is not null)
            {
                _logger.LogInformation("[ComingSoon] {Service:l} is reachable again ({Count} items)", state.Name, state.Data.Count);
            }

            state.Healthy = true;
            state.LastError = null;
        }
        catch (ServiceException ex)
        {
            if (state.LastError != ex.Message)
            {
                _logger.LogError("[ComingSoon] {Error:l} - keeping last known state ({Count} items)", ex.Message, state.Data.Count);
            }

            state.Healthy = false;
            state.LastError = ex.Message;
        }
    }

    private SeerrDetails? CachedDetails(MediaKind kind, int tmdb)
        => _details.TryGetValue((kind, tmdb), out var c) ? c.Value : null;

    private Task<SeerrDetails?> GetDetailsAsync(ISeerrClient client, MediaKind kind, int tmdb, TimeSpan maxAge, CancellationToken cancellationToken)
        => GetCachedAsync(_details, (kind, tmdb), ct => client.GetDetailsAsync(kind, tmdb, ct), "Seerr details lookup", cancellationToken, maxAge);

    private async Task<TValue?> GetCachedAsync<TKey, TValue>(
        Dictionary<TKey, Cached<TValue?>> cache,
        TKey key,
        Func<CancellationToken, Task<TValue?>> fetch,
        string what,
        CancellationToken cancellationToken,
        TimeSpan? maxAge = null)
        where TKey : notnull
        where TValue : class
    {
        var now = _time.GetUtcNow();
        var ttl = maxAge ?? ReleaseTtl;
        if (cache.TryGetValue(key, out var cached))
        {
            var fresh = cached.Failed ? now - cached.At < LookupFailureBackoff : now - cached.At < ttl;
            if (fresh || _lookupsThisPoll >= MaxLookupsPerPoll)
            {
                return cached.Value;
            }
        }
        else if (_lookupsThisPoll >= MaxLookupsPerPoll)
        {
            return null; // Picked up on a later poll.
        }

        _lookupsThisPoll++;
        try
        {
            var value = await fetch(cancellationToken).ConfigureAwait(false);
            // "Not in Radarr/Sonarr" is re-checked after a minute (Seerr may be handing it over right now).
            var at = value is MovieReleaseInfo { InRadarr: false } or SeriesReleaseInfo { InSonarr: false } ? now - ttl + NotFoundRecheck : now;
            cache[key] = new Cached<TValue?>(value, at, false);
            return value;
        }
        catch (ServiceException ex)
        {
            _logger.LogWarning("[ComingSoon] {What:l} for {Key:l} failed: {Error:l}", what, key.ToString(), ex.Message);
            var stale = cached?.Value;
            cache[key] = new Cached<TValue?>(stale, now, true);
            return stale;
        }
    }

    private sealed record Cached<T>(T Value, DateTimeOffset At, bool Failed);

    private sealed class SourceState<T>(string name)
    {
        public string Name { get; } = name;

        public bool Configured { get; set; }

        /// <summary>Gets or sets a value indicating whether the last fetch worked (or the source isn't configured).</summary>
        public bool Healthy { get; set; } = true;

        public string? LastError { get; set; }

        public IReadOnlyList<T> Data { get; set; } = [];

        public string? LoggedSignature { get; set; }
    }
}
