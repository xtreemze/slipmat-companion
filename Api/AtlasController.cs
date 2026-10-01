using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Experimental Atlas projection implementation. This controller is deliberately
/// not routable: the current implementation still contains resolver and product
/// semantic decisions that belong to Slipmat's client/shared Atlas domain.
/// </summary>
[NonController]
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway")]
public class AtlasController : ControllerBase
{
    private readonly ILogger<AtlasController> _logger;

    public AtlasController(ILogger<AtlasController> logger)
    {
        _logger = logger;
    }

    [HttpGet("atlas/concepts/{conceptId}")]
    [ProducesResponseType(typeof(AtlasConceptDetailResponse), StatusCodes.Status200OK)]
    public ActionResult<AtlasConceptDetailResponse> GetConceptDetail(
        string conceptId,
        [FromQuery] string? albumId = null,
        [FromQuery] string? releaseGroupId = null,
        [FromQuery] string? selectedReleaseId = null,
        [FromQuery] string? selectedSurface = null)
    {
        AtlasConceptDetailResponse response;

        if (string.IsNullOrWhiteSpace(albumId))
        {
            var catalog = AtlasCatalogStoreService.LoadCatalog();
            var catalogConcept = AtlasCatalogStoreService.FindConceptById(catalog, conceptId) ??
                (!string.IsNullOrWhiteSpace(releaseGroupId)
                    ? AtlasCatalogStoreService.FindConceptByReleaseGroupId(catalog, releaseGroupId)
                    : null) ??
                AtlasCatalogStoreService.FindConceptByReleaseGroupId(catalog, conceptId);

            if (catalogConcept is not null)
            {
                var projection = new AtlasCanonicalConceptProjection(
                    Concept: catalogConcept,
                    Releases: AtlasCatalogStoreService.FindReleasesByConceptId(catalog, catalogConcept.Id),
                    ProviderCandidates: catalogConcept.ProviderCandidates ?? Array.Empty<AtlasProviderCandidateRecord>(),
                    ResolutionRecords: AtlasCatalogStoreService.FindResolutionRecordsByConceptId(catalog, catalogConcept.Id),
                    SourceObservations: AtlasCatalogStoreService.FindSourceObservationsByConceptId(catalog, catalogConcept.Id));
                response = AtlasConceptPayloadFactory.CreateConceptDetail(
                    projection,
                    selectedReleaseId,
                    selectedSurface);
            }
            else
            {
                response = AtlasConceptPayloadFactory.CreateConceptDetail(
                    conceptId,
                    albumId,
                    releaseGroupId,
                    selectedReleaseId,
                    selectedSurface);
            }
        }
        else
        {
            response = AtlasConceptPayloadFactory.CreateConceptDetail(
                conceptId,
                albumId,
                releaseGroupId,
                selectedReleaseId,
                selectedSurface);
        }

        _logger.LogDebug(
            "Atlas concept payload served for conceptId={ConceptId} albumId={AlbumId} releaseGroupId={ReleaseGroupId}",
            conceptId,
            albumId,
            releaseGroupId);

        return Ok(response);
    }
}
