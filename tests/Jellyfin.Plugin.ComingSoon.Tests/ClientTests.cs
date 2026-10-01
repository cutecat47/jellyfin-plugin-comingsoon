using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComingSoon.Clients;
using Jellyfin.Plugin.ComingSoon.Model;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

public class ClientTests
{
    [Fact]
    public async Task RadarrQueue_SendsApiKey_AndRequestsMovies()
    {
        var handler = new FixtureHandler().On("/api/v3/queue", "radarr/queue.json");
        var client = new RadarrClient(new HttpClient(handler), TestEndpoints.Radarr);

        var records = await client.GetQueueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, records.Count);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("radarr-test-key", request.Headers.GetValues("X-Api-Key").Single());
        Assert.Contains("includeMovie=true", request.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("pageSize=200", request.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queue_PagesUntilTotalRecords()
    {
        var handler = new FixtureHandler()
            .On("/api/v3/queue", "radarr/queue_page1.json", "page=1&")
            .On("/api/v3/queue", "radarr/queue_page2.json", "page=2&");
        var client = new RadarrClient(new HttpClient(handler), TestEndpoints.Radarr);

        var records = await client.GetQueueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 1001, 1002, 1003 }, records.Select(r => r.TmdbId!.Value));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Sonarr_KeepsUrlBase()
    {
        var handler = new FixtureHandler().On("/api/v3/queue", "sonarr/queue.json");
        var client = new SonarrClient(new HttpClient(handler), TestEndpoints.Sonarr);

        await client.GetQueueAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("http://sonarr:8989/sonarr/api/v3/queue?", handler.Requests.Single().RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("includeEpisode=true", handler.Requests.Single().RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seerr_RequestsProcessingFilter()
    {
        var handler = new FixtureHandler().On("/api/v1/request", "seerr/requests.json");
        var client = new SeerrClient(new HttpClient(handler), TestEndpoints.Seerr);

        var requests = await client.GetActiveRequestsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, requests.Count);
        Assert.Contains("filter=processing", handler.Requests.Single().RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal("seerr-test-key", handler.Requests.Single().Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task Seerr_Details_ByKind()
    {
        var handler = new FixtureHandler().On("/api/v1/tv/95396", "seerr/tv_95396.json");
        var client = new SeerrClient(new HttpClient(handler), TestEndpoints.Seerr);

        var details = await client.GetDetailsAsync(MediaKind.Series, 95396, TestContext.Current.CancellationToken);

        Assert.Equal("Severance", details!.Title);
    }

    [Fact]
    public async Task Unauthorized_GivesFriendlyMessage()
    {
        var handler = new FixtureHandler().Fail("/api/v3/queue", HttpStatusCode.Unauthorized);
        var client = new SonarrClient(new HttpClient(handler), TestEndpoints.Sonarr);

        var ex = await Assert.ThrowsAsync<ServiceException>(() => client.GetQueueAsync(TestContext.Current.CancellationToken));
        Assert.Contains("API key rejected (401)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sonarr-test-key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HtmlResponse_SaysCheckUrl()
    {
        var handler = new FixtureHandler().On("/api/v3/queue", "bad/login_page.html");
        var client = new SonarrClient(new HttpClient(handler), TestEndpoints.Sonarr);

        var ex = await Assert.ThrowsAsync<ServiceException>(() => client.GetQueueAsync(TestContext.Current.CancellationToken));
        Assert.Contains("not JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedJson_IsServiceException()
    {
        var handler = new FixtureHandler().On("/api/v3/queue", "bad/truncated.json");
        var client = new RadarrClient(new HttpClient(handler), TestEndpoints.Radarr);

        await Assert.ThrowsAsync<ServiceException>(() => client.GetQueueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectionRefused_SaysCantConnect()
    {
        var handler = new FixtureHandler().Throw("/api/v3/system/status");
        var client = new SonarrClient(new HttpClient(handler), TestEndpoints.Sonarr);

        var ex = await Assert.ThrowsAsync<ServiceException>(() => client.GetVersionAsync(TestContext.Current.CancellationToken));
        Assert.StartsWith("Sonarr: can't connect to http://sonarr:8989/sonarr/api/v3/system/status", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "", "Not configured")]
    [InlineData("http://radarr:7878", "", "API key is empty")]
    [InlineData("radarr:7878", "key", "is not a valid http(s) address")]
    [InlineData("ftp://radarr", "key", "is not a valid http(s) address")]
    public void Endpoint_Validation(string url, string key, string expected)
    {
        Assert.Null(ServiceEndpoint.TryCreate("Radarr", url, key, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://host:7878", "http://host:7878/api/v3/queue")]
    [InlineData("http://host:7878/", "http://host:7878/api/v3/queue")]
    [InlineData("http://host/radarr", "http://host/radarr/api/v3/queue")]
    [InlineData("https://host/radarr/", "https://host/radarr/api/v3/queue")]
    public void Endpoint_Resolve(string baseUrl, string expected)
    {
        var endpoint = ServiceEndpoint.TryCreate("Radarr", baseUrl, "k", out _)!;
        Assert.Equal(expected, endpoint.Resolve("api/v3/queue").ToString());
    }
}
