using System.Collections.Generic;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ComingSoon.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ComingSoon.Api;

/// <summary>
/// Admin-only endpoints used by the config page.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("ComingSoon")]
[Produces(MediaTypeNames.Application.Json)]
public class ComingSoonController(ConnectionTester tester) : ControllerBase
{
    /// <summary>
    /// Tests the Seerr/Sonarr/Radarr settings sent from the config page (unsaved values allowed).
    /// </summary>
    [HttpPost("TestConnections")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ConnectionTestResult>>> TestConnections(
        [FromBody] ConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        var results = await tester.TestAsync(request, cancellationToken).ConfigureAwait(false);
        return Ok(results);
    }
}

/// <summary>Connection settings to test.</summary>
public record ConnectionTestRequest(
    string? SeerrUrl,
    string? SeerrApiKey,
    string? SonarrUrl,
    string? SonarrApiKey,
    string? RadarrUrl,
    string? RadarrApiKey);

/// <summary>Result of testing one service.</summary>
public record ConnectionTestResult(string Service, bool Success, string Message);
