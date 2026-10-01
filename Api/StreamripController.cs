using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Experimental provider helper. It remains non-routable until Slipmat has a
/// production client-owned acquisition/provider adapter with equivalent semantics.
/// </summary>
[NonController]
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway")]
public class StreamripController : ControllerBase
{
    [HttpGet("streamrip/status")]
    [ProducesResponseType(typeof(StreamripServiceStatus), StatusCodes.Status200OK)]
    public ActionResult<StreamripServiceStatus> GetStatus()
        => Ok(StreamripCliService.GetStatus());

    [HttpPost("streamrip/search")]
    [ProducesResponseType(typeof(StreamripSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<StreamripSearchResponse> Search([FromBody] StreamripSearchRequest request)
    {
        try
        {
            return Ok(StreamripCliService.Search(request));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
