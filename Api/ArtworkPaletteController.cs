using System;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Authenticated presentation-neutral artwork palette evidence.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway/artwork")]
public sealed class ArtworkPaletteController : ControllerBase
{
    private readonly ArtworkPaletteService _artworkPaletteService;

    public ArtworkPaletteController(ArtworkPaletteService artworkPaletteService)
        => _artworkPaletteService = artworkPaletteService;

    [HttpGet("palette/{itemId}")]
    [ProducesResponseType(typeof(ArtworkPaletteV1), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetPalette(string itemId)
    {
        if (!Guid.TryParse(itemId, out var itemGuid))
        {
            return BadRequest($"Invalid itemId format: '{itemId}'. Expected a Guid.");
        }

        var palette = _artworkPaletteService.ResolveForItem(itemGuid);
        return palette is null ? NotFound() : Ok(palette);
    }
}
