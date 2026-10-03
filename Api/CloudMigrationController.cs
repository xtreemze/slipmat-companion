using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

[ApiController]
[Route("Plugins/AudioGateway/cloud/migrations")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class CloudMigrationController : ControllerBase
{
    private readonly CloudMigrationCoordinator _coordinator;

    public CloudMigrationController(CloudMigrationCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    [HttpGet("candidates")]
    [ProducesResponseType(typeof(IReadOnlyList<CloudMigrationCandidate>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IReadOnlyList<CloudMigrationCandidate>> GetCandidates(
        [FromQuery] string direction)
    {
        try
        {
            return Ok(_coordinator.GetCandidates(direction));
        }
        catch (ArgumentException)
        {
            return BadRequest(new { code = "migration-direction-invalid" });
        }
    }

    [HttpGet]
    [ProducesResponseType(typeof(CloudMigrationListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CloudMigrationListResponse>> List(
        CancellationToken cancellationToken)
        => Ok(new CloudMigrationListResponse(
            await _coordinator.ListAsync(cancellationToken).ConfigureAwait(false)));

    [HttpPost]
    [ProducesResponseType(typeof(CloudMigrationJob), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CloudMigrationJob>> Start(
        [FromBody] CloudMigrationStartRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _coordinator
                .StartAsync(request, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return BadRequest(new { code = "migration-request-invalid" });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { code = ex.Message });
        }
    }

    [HttpPost("bulk")]
    [ProducesResponseType(typeof(CloudMigrationBulkStartResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CloudMigrationBulkStartResponse>> StartBulk(
        [FromBody] CloudMigrationBulkStartRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _coordinator
                .StartBulkAsync(request, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return BadRequest(new { code = "migration-direction-invalid" });
        }
    }

    [HttpPost("{jobId}/cutover")]
    [ProducesResponseType(typeof(CloudMigrationJob), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CloudMigrationJob>> Cutover(
        string jobId,
        CancellationToken cancellationToken)
        => await Transition(
            () => _coordinator.RequestCutoverAsync(jobId, cancellationToken))
            .ConfigureAwait(false);

    [HttpPost("{jobId}/rollback")]
    [ProducesResponseType(typeof(CloudMigrationJob), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CloudMigrationJob>> Rollback(
        string jobId,
        CancellationToken cancellationToken)
        => await Transition(
            () => _coordinator.RequestRollbackAsync(jobId, cancellationToken))
            .ConfigureAwait(false);

    [HttpPost("{jobId}/finalize")]
    [ProducesResponseType(typeof(CloudMigrationJob), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CloudMigrationJob>> FinalizeMigration(
        string jobId,
        [FromBody] CloudMigrationFinalizeRequest request,
        CancellationToken cancellationToken)
        => await Transition(
            () => _coordinator.RequestFinalizeAsync(jobId, request, cancellationToken))
            .ConfigureAwait(false);

    private static async Task<ActionResult<CloudMigrationJob>> Transition(
        Func<Task<CloudMigrationJob>> action)
    {
        try
        {
            return new OkObjectResult(await action().ConfigureAwait(false));
        }
        catch (InvalidOperationException ex)
        {
            return new ConflictObjectResult(new { code = ex.Message });
        }
    }
}
