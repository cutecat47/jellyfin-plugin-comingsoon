using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ComingSoon.Clients;

/// <summary>Base URL + API key for one external service.</summary>
public sealed record ServiceEndpoint(string Name, Uri BaseUrl, string ApiKey)
{
    /// <summary>Builds an endpoint from config values; returns null when the service isn't configured.</summary>
    public static ServiceEndpoint? TryCreate(string name, string? url, string? apiKey, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(apiKey))
        {
            error = "Not configured";
            return null;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            error = "API key is empty";
            return null;
        }

        var trimmed = url?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = "URL is empty";
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = $"URL '{trimmed}' is not a valid http(s) address";
            return null;
        }

        return new ServiceEndpoint(name, uri, apiKey.Trim());
    }

    public Uri Resolve(string relative)
    {
        var baseText = BaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri(baseText + "/" + relative.TrimStart('/'));
    }
}

/// <summary>A failed call to an external service, with a message fit for the log and the config page.</summary>
public sealed class ServiceException : Exception
{
    public ServiceException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    public ServiceException()
    {
    }

    public ServiceException(string message)
        : base(message)
    {
    }
}

/// <summary>Shared GET-and-parse logic for the Seerr/Sonarr/Radarr clients.</summary>
internal static class ServiceHttp
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<JsonDocument> GetJsonAsync(HttpClient http, ServiceEndpoint endpoint, string relative, CancellationToken cancellationToken)
    {
        var uri = endpoint.Resolve(relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("X-Api-Key", endpoint.ApiKey);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServiceException($"{endpoint.Name}: no response from {Describe(uri)} within {Timeout.TotalSeconds:0}s");
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceException($"{endpoint.Name}: can't connect to {Describe(uri)} ({ex.Message})", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ServiceException($"{endpoint.Name}: API key rejected ({(int)response.StatusCode}) by {Describe(uri)}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ServiceException($"{endpoint.Name}: HTTP {(int)response.StatusCode} from {Describe(uri)}");
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                try
                {
                    return await JsonDocument.ParseAsync(stream, default, timeout.Token).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    throw new ServiceException($"{endpoint.Name}: response from {Describe(uri)} is not JSON - check the URL (and URL base) points at the service itself", ex);
                }
            }
        }
    }

    /// <summary>Scheme, host, port and path only - never the query string.</summary>
    private static string Describe(Uri uri) => uri.GetLeftPart(UriPartial.Path);
}
