using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Optional authenticated replica endpoint for Slipmat podcast subscription
/// intent. Jellyfin user identity comes only from the authenticated principal;
/// requests cannot select an arbitrary user namespace.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway/podcasts/subscriptions")]
public class PodcastSubscriptionsController : ControllerBase
{
    private const string JellyfinUserIdClaim = "Jellyfin-UserId";

    /// <summary>Returns the current authenticated user's server replica.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<PodcastSubscriptionRecordV1>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<PodcastSubscriptionRecordV1>>> GetSubscriptions()
    {
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Forbid();
        }

        var records = await PodcastSubscriptionStoreService.LoadAsync(
                ResolveStoreRoot(),
                userId,
                HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(records);
    }

    /// <summary>
    /// Merges a client replica into the authenticated user's server replica and
    /// returns the complete merged state so both sides can converge.
    /// </summary>
    [HttpPost("sync")]
    [ProducesResponseType(typeof(PodcastSubscriptionSyncResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PodcastSubscriptionSyncResponse>> Sync(
        [FromBody] PodcastSubscriptionSyncRequest request)
    {
        if (!TryGetAuthenticatedUserId(out var userId))
        {
            return Forbid();
        }

        var response = await PodcastSubscriptionStoreService.MergeAsync(
                ResolveStoreRoot(),
                userId,
                request.Records,
                HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(response);
    }

    private bool TryGetAuthenticatedUserId(out Guid userId)
    {
        var value = User.Claims.FirstOrDefault(
            claim => string.Equals(claim.Type, JellyfinUserIdClaim, StringComparison.OrdinalIgnoreCase))?.Value;
        return Guid.TryParse(value, out userId) && userId != Guid.Empty;
    }

    private static string ResolveStoreRoot()
        => Plugin.Instance?.Configuration.StoreRoot ?? new PluginConfiguration().StoreRoot;
}
