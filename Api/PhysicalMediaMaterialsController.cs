using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Authenticated cache of client-generated Package Inspector surface maps.
/// Optional acceleration only: without it the client generates the maps as
/// before.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway/physical-media/materials")]
public sealed class PhysicalMediaMaterialsController : ControllerBase
{
    private readonly PhysicalMediaMaterialStore _store;

    /// <summary>Initializes a new instance of the <see cref="PhysicalMediaMaterialsController"/> class.</summary>
    public PhysicalMediaMaterialsController(PhysicalMediaMaterialStore store) => _store = store;

    /// <summary>Lists the maps cached under a key.</summary>
    [HttpGet("{cacheKey}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult List(string cacheKey)
    {
        if (!PhysicalMediaMaterialStore.IsValidCacheKey(cacheKey))
        {
            return BadRequest();
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(new { schemaVersion = 1, cacheKey, assets = _store.List(cacheKey) });
    }

    /// <summary>Serves one cached map. Immutable: a key never changes content.</summary>
    [HttpGet("{cacheKey}/{assetName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Get(string cacheKey, string assetName)
    {
        if (!PhysicalMediaMaterialStore.IsValidCacheKey(cacheKey)
            || !PhysicalMediaMaterialStore.IsValidAssetName(assetName))
        {
            return BadRequest();
        }

        var path = _store.Find(cacheKey, assetName);
        if (path is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return PhysicalFile(path, "image/png");
    }

    /// <summary>Stores one map the client generated. First write wins.</summary>
    [HttpPut("{cacheKey}/{assetName}")]
    [RequestSizeLimit(PhysicalMediaMaterialStore.MaxAssetBytes)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Put(string cacheKey, string assetName, CancellationToken cancellationToken)
    {
        if (!PhysicalMediaMaterialStore.IsValidCacheKey(cacheKey)
            || !PhysicalMediaMaterialStore.IsValidAssetName(assetName)
            || !string.Equals(Request.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest();
        }

        var result = await _store.PutAsync(cacheKey, assetName, Request.Body, cancellationToken).ConfigureAwait(false);
        return result == PhysicalMediaMaterialStore.PutResult.Rejected ? BadRequest() : NoContent();
    }
}
