using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>Reports usage and policy for reproducible analysis artifacts.</summary>
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway")]
public sealed class ArtifactRetentionDiagnosticsController : ControllerBase
{
    private readonly AnalysisArtifactRetentionService _retention;

    /// <summary>Creates the retention diagnostics endpoint.</summary>
    public ArtifactRetentionDiagnosticsController(AnalysisArtifactRetentionService retention)
    {
        _retention = retention;
    }

    /// <summary>Returns usage and limits without exposing store paths or media identities.</summary>
    [HttpGet("diagnostics/artifact-retention")]
    [ProducesResponseType(typeof(AnalysisArtifactUsage), StatusCodes.Status200OK)]
    public ActionResult<AnalysisArtifactUsage> GetUsage()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var storeRoot = RuntimeSettings.ResolveStoreRoot(config);
        return Ok(_retention.GetUsage(storeRoot, config));
    }
}
