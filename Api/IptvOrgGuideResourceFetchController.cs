using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Authenticated iptv-org XMLTV acquisition fallback. The request URL must be
/// present in the normalized iptv-org catalog before any outbound connection.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.LiveTvAccess)]
[Route("Plugins/AudioGateway/livetv/iptv-org/guide")]
public sealed class IptvOrgGuideResourceFetchController : ControllerBase
{
    private const int MaxRequestBodyBytes = 16 * 1024;
    private readonly IptvOrgCatalogService _catalog;

    public IptvOrgGuideResourceFetchController(IptvOrgCatalogService catalog)
    {
        _catalog = catalog;
    }

    [HttpPost("fetch")]
    [Consumes("application/json")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [ProducesResponseType(typeof(IptvOrgGuideResourceFetchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<IptvOrgGuideResourceFetchResponse>> Fetch(
        [FromBody] IptvOrgGuideResourceFetchRequest request)
    {
        try
        {
            var response = await IptvOrgGuideResourceFetchService.FetchAsync(
                    _catalog,
                    request,
                    HttpContext.RequestAborted)
                .ConfigureAwait(false);
            return Ok(response);
        }
        catch (IptvOrgGuideResourceFetchService.FetchException ex)
        {
            return Problem(
                statusCode: (int)ex.ResponseStatus,
                title: "iptv-org guide resource fetch failed",
                detail: ex.Code);
        }
    }
}
