using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Experimental server-side Atlas request/catalog cache. This controller is
/// deliberately not routable while POST materialization performs Atlas resolver
/// decisions; the client request store remains authoritative.
/// </summary>
[NonController]
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway/atlas")]
public class AtlasRequestsController : ControllerBase
{
    [HttpGet("requests")]
    [ProducesResponseType(typeof(AtlasAcquisitionRequestListResponse), StatusCodes.Status200OK)]
    public ActionResult<AtlasAcquisitionRequestListResponse> GetRequests()
        => Ok(new AtlasAcquisitionRequestListResponse(AtlasRequestStoreService.LoadRequests()));

    [HttpGet("catalog")]
    [ProducesResponseType(typeof(AtlasCatalogDocument), StatusCodes.Status200OK)]
    public ActionResult<AtlasCatalogDocument> GetCatalog()
        => Ok(AtlasCatalogStoreService.LoadCatalog());

    [HttpPost("requests")]
    [ProducesResponseType(typeof(AtlasAcquisitionRequestRecord), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AtlasAcquisitionRequestRecord>> PostRequest([FromBody] AtlasAcquisitionRequestRecord request)
    {
        if (string.IsNullOrWhiteSpace(request.AlbumId) ||
            string.IsNullOrWhiteSpace(request.Title) ||
            string.IsNullOrWhiteSpace(request.ArtistName))
        {
            return BadRequest("albumId, title, and artistName are required.");
        }

        var stored = AtlasRequestStoreService.UpsertRequest(request);
        await AtlasCatalogStoreService.SyncRequestsAsync([stored]);
        return Ok(stored);
    }
}
