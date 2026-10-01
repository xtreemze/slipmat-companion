using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Authenticated CORS-fallback acquisition for bounded podcast feed, chapter,
/// and transcript resources. This controller exposes no arbitrary method/header
/// forwarding and delegates all outbound address/redirect policy to the hardened
/// fetch service.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway/podcasts/resources")]
public class PodcastResourceFetchController : ControllerBase
{
    private const int MaxRequestBodyBytes = 16 * 1024;

    /// <summary>
    /// POST /Plugins/AudioGateway/podcasts/resources/fetch
    /// </summary>
    [HttpPost("fetch")]
    [Consumes("application/json")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [ProducesResponseType(typeof(PodcastResourceFetchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<PodcastResourceFetchResponse>> Fetch(
        [FromBody] PodcastResourceFetchRequest request)
    {
        try
        {
            var response = await PodcastResourceFetchService.FetchAsync(
                    request,
                    HttpContext.RequestAborted)
                .ConfigureAwait(false);
            return Ok(response);
        }
        catch (PodcastResourceFetchService.FetchException ex)
        {
            return Problem(
                statusCode: (int)ex.ResponseStatus,
                title: "Podcast resource fetch failed",
                detail: ex.Code);
        }
    }
}
