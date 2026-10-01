using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComingSoon.Clients;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ComingSoon.Tests;

internal static class Fixtures
{
    public static string PathOf(string relative) => Path.Combine(AppContext.BaseDirectory, "Fixtures", relative);

    public static string Text(string relative) => File.ReadAllText(PathOf(relative));

    public static JsonDocument Json(string relative) => JsonDocument.Parse(Text(relative));
}

/// <summary>
/// Serves fixture files for matching request paths. Never touches the network; unmatched requests get 404.
/// </summary>
internal sealed class FixtureHandler : HttpMessageHandler
{
    private readonly List<(Func<Uri, bool> Match, Func<HttpResponseMessage> Respond)> _routes = [];

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Route a path (and optional query fragment) to a fixture file.</summary>
    public FixtureHandler On(string path, string fixture, string? queryContains = null)
        => On(path, () => Ok(Fixtures.Text(fixture), fixture.EndsWith(".html", StringComparison.Ordinal) ? "text/html" : "application/json"), queryContains);

    public FixtureHandler On(string path, Func<HttpResponseMessage> respond, string? queryContains = null)
    {
        // Newest routes win, so tests can override a default.
        _routes.Insert(0, (uri => uri.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase)
            && (queryContains is null || uri.Query.Contains(queryContains, StringComparison.OrdinalIgnoreCase)), respond));
        return this;
    }

    public FixtureHandler Fail(string path, HttpStatusCode status)
        => On(path, () => new HttpResponseMessage(status) { Content = new StringContent("error") });

    public FixtureHandler Throw(string path)
        => On(path, () => throw new HttpRequestException("Connection refused (sonarr:8989)"));

    public static HttpResponseMessage Ok(string body, string mediaType = "application/json")
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        var route = _routes.FirstOrDefault(r => r.Match(request.RequestUri!));
        return Task.FromResult(route.Respond is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") }
            : route.Respond());
    }
}

/// <summary>Builds real clients on top of fixture handlers.</summary>
internal sealed class FixtureClientFactory : IServiceClientFactory
{
    public FixtureHandler Radarr { get; } = new FixtureHandler()
        .On("/api/v3/queue", "radarr/queue.json")
        .On("/api/v3/movie", () => FixtureHandler.Ok("[]"))
        .On("/api/v3/movie", "radarr/movie_1003596.json", "tmdbId=1003596")
        .On("/api/v3/movie", "radarr/movie_550.json", "tmdbId=550")
        .On("/api/v3/system/status", "radarr/system_status.json");

    public FixtureHandler Sonarr { get; } = new FixtureHandler()
        .On("/api/v3/queue", "sonarr/queue.json")
        .On("/api/v3/series", () => FixtureHandler.Ok("[]"))
        .On("/api/v3/series", "sonarr/series_371980.json", "tvdbId=371980")
        .On("/api/v3/series", "sonarr/series_450123.json", "tvdbId=450123")
        .On("/api/v3/system/status", "sonarr/system_status.json");

    public FixtureHandler Seerr { get; } = new FixtureHandler()
        .On("/api/v1/request", "seerr/requests.json")
        .On("/api/v1/movie/687163", "seerr/movie_687163.json")
        .On("/api/v1/movie/1003596", "seerr/movie_1003596.json")
        .On("/api/v1/movie/550", "seerr/movie_550.json")
        .On("/api/v1/tv/95396", "seerr/tv_95396.json")
        .On("/api/v1/tv/250001", "seerr/tv_250001.json")
        .On("/api/v1/status", "seerr/status.json");

    public IRadarrClient CreateRadarr(ServiceEndpoint endpoint) => new RadarrClient(new HttpClient(Radarr), endpoint);

    public ISonarrClient CreateSonarr(ServiceEndpoint endpoint) => new SonarrClient(new HttpClient(Sonarr), endpoint);

    public ISeerrClient CreateSeerr(ServiceEndpoint endpoint) => new SeerrClient(new HttpClient(Seerr), endpoint);
}

internal sealed class ManualTimeProvider(DateTimeOffset start, TimeZoneInfo? zone = null) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override TimeZoneInfo LocalTimeZone { get; } = zone ?? TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class ListLogger : ILogger
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IEnumerable<string> Messages(LogLevel? level = null)
        => Entries.Where(e => level is null || e.Level == level).Select(e => e.Message);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Enqueue((logLevel, formatter(state, exception)));
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public ListLogger Inner { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Inner.Log(logLevel, eventId, state, exception, formatter);
}

internal static class TestEndpoints
{
    public static ServiceEndpoint Radarr => new("Radarr", new Uri("http://radarr:7878"), "radarr-test-key");

    public static ServiceEndpoint Sonarr => new("Sonarr", new Uri("http://sonarr:8989/sonarr/"), "sonarr-test-key");

    public static ServiceEndpoint Seerr => new("Seerr", new Uri("http://seerr:5055"), "seerr-test-key");
}
