using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Authenticated CORS fallback for a fixed set of public discovery directories.
/// It returns raw JSON evidence and owns no directory identity or search semantics.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway/directories/resources")]
public sealed class DirectoryResourceFetchController : ControllerBase
{
    private const int MaxRequestBodyBytes = 16 * 1024;

    /// <summary>POST /Plugins/AudioGateway/directories/resources/fetch</summary>
    [HttpPost("fetch")]
    [Consumes("application/json")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [ProducesResponseType(typeof(DirectoryResourceFetchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<DirectoryResourceFetchResponse>> Fetch(
        [FromBody] DirectoryResourceFetchRequest request)
    {
        try
        {
            var response = await DirectoryResourceFetchService.FetchAsync(
                    request,
                    HttpContext.RequestAborted)
                .ConfigureAwait(false);
            return Ok(response);
        }
        catch (DirectoryResourceFetchService.FetchException ex)
        {
            return Problem(
                statusCode: (int)ex.ResponseStatus,
                title: "Directory resource fetch failed",
                detail: ex.Code);
        }
    }
}
