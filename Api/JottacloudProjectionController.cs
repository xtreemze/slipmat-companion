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
/// Elevated administration endpoints for an operator-installed/authenticated Jottacloud daemon.
/// No endpoint accepts provider credentials or performs login.
/// </summary>
[ApiController]
[Route("Plugins/AudioGateway/cloud/jottacloud")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class JottacloudProjectionController : ControllerBase
{
    private readonly JottacloudProjectionService _projectionService;

    public JottacloudProjectionController(
        JottacloudProjectionService projectionService)
    {
        _projectionService = projectionService;
    }

    /// <summary>
    /// Returns sanitized CLI/projection/library health.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(JottacloudProjectionStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<JottacloudProjectionStatusResponse>> GetStatus(
        CancellationToken cancellationToken)
    {
        var response = await _projectionService
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    /// <summary>
    /// Browses the remote Jottacloud namespace through read-only jotta-cli ls.
    /// Empty path lists the remote namespace roots.
    /// </summary>
    [HttpGet("browse")]
    [ProducesResponseType(typeof(JottacloudBrowseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<JottacloudBrowseResponse>> Browse(
        [FromQuery] string? path = null,
        [FromQuery] bool details = false,
        CancellationToken cancellationToken = default)
    {
        JottacloudCliDirectoryListing listing;
        try
        {
            listing = await _projectionService
                .BrowseAsync(path ?? string.Empty, details, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { code = "remote-path-invalid" });
        }

        if (!listing.Success)
        {
            return Conflict(new { code = listing.ErrorCode ?? "browse-failed" });
        }

        return Ok(
            new JottacloudBrowseResponse(
                Path: path?.Trim() ?? string.Empty,
                Lines: listing.Lines,
                Truncated: listing.OutputTruncated));
    }

    /// <summary>
    /// Runs one safe reconcile iteration. Transfers continue under jottad and a
    /// completed projection is only scanned by Jellyfin on a later idle iteration.
    /// </summary>
    [HttpPost("reconcile")]
    [ProducesResponseType(typeof(JottacloudProjectionReconcileResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<JottacloudProjectionReconcileResponse>> Reconcile(
        CancellationToken cancellationToken)
    {
        var response = await _projectionService
            .ReconcileAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }
}
