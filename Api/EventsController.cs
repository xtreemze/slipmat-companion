using System;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Authenticated optional track-metadata batching endpoints for Slipmat.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway/events")]
public class EventsController : ControllerBase
{
    private const int MaxBatchSize = 25;

    private readonly ILogger<EventsController> _logger;
    private readonly TrackPlaybackEventService _trackPlaybackEventService;

    public EventsController(
        TrackPlaybackEventService trackPlaybackEventService,
        ILogger<EventsController> logger)
    {
        _trackPlaybackEventService = trackPlaybackEventService;
        _logger = logger;
    }

    [HttpGet("track/{itemId}")]
    [ProducesResponseType(typeof(TrackPlaybackEvent), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetTrackEvent(
        string itemId,
        [FromQuery] string? include = null,
        [FromQuery] int waveformPps = 10)
    {
        if (!Guid.TryParse(itemId, out var itemGuid))
        {
            return BadRequest($"Invalid itemId format: '{itemId}'. Expected a Guid.");
        }

        if (waveformPps is not (1 or 10 or 100))
        {
            return BadRequest($"Invalid waveformPps={waveformPps}. Accepted: 1, 10, 100.");
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var trackEvent = _trackPlaybackEventService.ComposeTrackEvent(
            itemGuid,
            ParseInclude(include),
            waveformPps,
            config.StoreRoot);

        if (trackEvent is null)
        {
            _logger.LogDebug("Playback event track not found: {ItemId}", itemId);
            return NotFound();
        }

        return Ok(trackEvent);
    }

    [HttpPost("track/batch")]
    [ProducesResponseType(typeof(BatchTrackPlaybackEventResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult BatchTrackEvents([FromBody] BatchTrackPlaybackEventRequest request)
    {
        if (request.ItemIds is null || request.ItemIds.Length == 0)
        {
            return BadRequest("itemIds must not be empty.");
        }

        if (request.ItemIds.Length > MaxBatchSize)
        {
            return BadRequest($"Batch size {request.ItemIds.Length} exceeds maximum of {MaxBatchSize}.");
        }

        if (request.WaveformPps is not (1 or 10 or 100))
        {
            return BadRequest($"Invalid waveformPps={request.WaveformPps}. Accepted: 1, 10, 100.");
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var items = _trackPlaybackEventService.ComposeTrackEvents(
            request.ItemIds,
            ParseInclude(request.Include),
            request.WaveformPps,
            config.StoreRoot);

        return Ok(new BatchTrackPlaybackEventResponse(items));
    }

    public static IncludeFlags ParseInclude(string? include)
    {
        if (string.IsNullOrWhiteSpace(include))
        {
            return IncludeFlags.None;
        }

        return include
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Aggregate(IncludeFlags.None, (flags, token) => flags | token.ToLowerInvariant() switch
            {
                "waveform" => IncludeFlags.Waveform,
                "sidecar" => IncludeFlags.Sidecar,
                "analysis" => IncludeFlags.Analysis,
                _ => IncludeFlags.None,
            });
    }
}
