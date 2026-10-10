using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>Elevated, read-only readiness checklist for the rclone VFS setup.</summary>
[ApiController]
[Route("Plugins/AudioGateway/cloud/rclone")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class CloudPreflightController : ControllerBase
{
    private readonly CloudPreflightService _preflight;

    public CloudPreflightController(CloudPreflightService preflight)
    {
        _preflight = preflight;
    }

    [HttpGet("preflight")]
    [ProducesResponseType(typeof(CloudPreflightResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CloudPreflightResponse>> Get(CancellationToken cancellationToken)
        => Ok(await _preflight.RunAsync(cancellationToken).ConfigureAwait(false));
}
