using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Read-only operator diagnostics for Jellyfin sessions. This does not discover
/// UPnP devices or create a second device or playback authority.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/AudioGateway/devices")]
public sealed class RemoteSessionsController : ControllerBase
{
    private const int MaxSessions = 100;
    private readonly ISessionManager _sessions;

    public RemoteSessionsController(ISessionManager sessions)
    {
        _sessions = sessions;
    }

    [HttpGet("sessions")]
    [ProducesResponseType(typeof(RemoteSessionInventory), StatusCodes.Status200OK)]
    public ActionResult<RemoteSessionInventory> GetSessions()
    {
        // Jellyfin owns this live state. Do not cache sessions, device secrets,
        // control URLs or command authority in the companion.
        var eligible = _sessions.Sessions
            .Where(session => session.IsActive && session.SupportsRemoteControl &&
                !string.IsNullOrWhiteSpace(session.Id))
            .OrderBy(session => session.Id, StringComparer.Ordinal)
            .Take(MaxSessions + 1)
            .ToArray();

        var results = eligible.Take(MaxSessions)
            .Select(session => new RemoteSessionEntry(
                Id: $"jellyfin-session:{session.Id}",
                DisplayName: Bounded(session.DeviceName ?? session.Client, 128),
                Source: "jellyfin-session",
                SupportsRemoteControl: true))
            .ToArray();

        Response.Headers.CacheControl = "private, no-store";
        return Ok(new RemoteSessionInventory(
            Version: 1,
            Authoritative: false,
            Truncated: eligible.Length > MaxSessions,
            Sessions: results));
    }

    private static string Bounded(string? value, int maxLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

public sealed record RemoteSessionInventory(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("authoritative")] bool Authoritative,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("sessions")] IReadOnlyList<RemoteSessionEntry> Sessions);

public sealed record RemoteSessionEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("supportsRemoteControl")] bool SupportsRemoteControl);
