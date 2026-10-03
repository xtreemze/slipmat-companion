using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Elevated administration endpoints for operator-configured rclone cloud remotes.
/// No endpoint accepts provider credentials or exposes rclone configuration values.
/// </summary>
[ApiController]
[Route("Plugins/AudioGateway/cloud/rclone")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class CloudProjectionController : ControllerBase
{
    private readonly CloudProjectionService _projectionService;

    public CloudProjectionController(CloudProjectionService projectionService)
    {
        _projectionService = projectionService;
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(CloudProjectionStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CloudProjectionStatusResponse>> GetStatus(
        [FromQuery] string? projectionId = null,
        CancellationToken cancellationToken = default)
        => Ok(await _projectionService
            .GetStatusAsync(projectionId, cancellationToken)
            .ConfigureAwait(false));

    [HttpGet("remotes")]
    [ProducesResponseType(typeof(CloudRemoteListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CloudRemoteListResponse>> GetRemotes(
        CancellationToken cancellationToken)
        => Ok(await _projectionService
            .ListRemotesAsync(cancellationToken)
            .ConfigureAwait(false));

    [HttpGet("browse")]
    [ProducesResponseType(typeof(CloudBrowseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CloudBrowseResponse>> Browse(
        [FromQuery] string remote,
        [FromQuery] string? path = null,
        CancellationToken cancellationToken = default)
    {
        RcloneDirectoryListing listing;
        string normalizedRemote;
        string normalizedPath;
        try
        {
            normalizedRemote = RcloneCliHost.NormalizeRemoteName(remote);
            normalizedPath = RcloneCliHost.NormalizeRemotePath(path);
            listing = await _projectionService
                .BrowseAsync(normalizedRemote, normalizedPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { code = "remote-invalid" });
        }

        if (!listing.Success)
        {
            return Conflict(new { code = listing.ErrorCode ?? "browse-failed" });
        }

        return Ok(new CloudBrowseResponse(
            normalizedRemote,
            normalizedPath,
            listing.Entries,
            listing.OutputTruncated));
    }

    [HttpPost("mkdir")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> CreateDirectory(
        [FromBody] CloudCreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        string code;
        try
        {
            code = await _projectionService
                .CreateDirectoryAsync(
                    request.Remote,
                    request.Path,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { code = "remote-path-invalid" });
        }

        return code == "created"
            ? Ok(new { code })
            : Conflict(new { code });
    }

    [HttpPost("reconcile")]
    [ProducesResponseType(typeof(CloudProjectionReconcileResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CloudProjectionReconcileResponse>> Reconcile(
        [FromQuery] string? projectionId = null,
        CancellationToken cancellationToken = default)
        => Ok(await _projectionService
            .ReconcileAsync(projectionId, cancellationToken)
            .ConfigureAwait(false));
}
