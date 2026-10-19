using System;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Models;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>Read-only remote-control session projection for the authenticated Jellyfin user.</summary>
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway/devices/sessions")]
public sealed class MyRemoteSessionsController : ControllerBase
{
    private const string JellyfinUserIdClaim = "Jellyfin-UserId";
    private readonly ISessionManager _sessions;

    /// <summary>Creates the current-user session projection endpoint.</summary>
    /// <param name="sessions">Jellyfin's live session authority.</param>
    public MyRemoteSessionsController(ISessionManager sessions)
    {
        _sessions = sessions;
    }

    /// <summary>Lists this user's fresh active Jellyfin sessions that accept remote control.</summary>
    [HttpGet("mine")]
    [ProducesResponseType(typeof(RemoteSessionProjection.Snapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<RemoteSessionProjection.Snapshot> GetMine()
    {
        var claim = User.Claims.FirstOrDefault(
            value => string.Equals(value.Type, JellyfinUserIdClaim, StringComparison.OrdinalIgnoreCase))?.Value;
        if (!Guid.TryParse(claim, out var userId) || userId == Guid.Empty)
            return Forbid();

        var now = DateTimeOffset.UtcNow;
        var observations = _sessions.Sessions
            .Where(session => session.UserId == userId)
            .Take(RemoteSessionProjection.MaximumSessions + 1)
            .Select(session => new RemoteSessionProjection.Observation(
                SessionId: session.Id,
                DeviceId: session.DeviceId,
                DisplayName: session.DeviceName ?? session.Client,
                Active: session.IsActive,
                SupportsRemoteControl: session.SupportsRemoteControl,
                ObservedAt: new DateTimeOffset(DateTime.SpecifyKind(session.LastActivityDate, DateTimeKind.Utc)),
                OwnerUserId: session.UserId));

        var result = RemoteSessionProjection.ProjectForUser(observations, userId, now);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(result);
    }
}
