using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Reports optional Audio Gateway acceleration/enrichment capabilities.
/// Slipmat remains fully functional when this plugin or any module is absent.
/// </summary>
[ApiController]
[Route("Plugins/AudioGateway")]
public class CapabilitiesController : ControllerBase
{
    public const string ProtocolVersion = "1.0.0";
    public const string ExtensionMode = "optional-acceleration";
    public const string SupportedJellyfinVersion = "12.1.0";

    private static readonly object CacheLock = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);
    private static CapabilitiesResponse? _cachedResponse;
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    private readonly ILogger<CapabilitiesController> _logger;

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public CapabilitiesController(ILogger<CapabilitiesController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// GET /Plugins/AudioGateway/diagnostics/capabilities
    /// </summary>
    [HttpGet("diagnostics/capabilities")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(CapabilitiesResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CapabilitiesResponse>> GetCapabilities()
    {
        var cached = TryGetCachedResponse();
        if (cached is not null)
        {
            return Ok(cached);
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var degradedReasons = new List<string>();

        var analyzerEndpoint = RuntimeSettings.ResolveAnalyzerEndpoint(config);
        var analyzerHealthy = await ProbeAnalyzerHealthAsync(
                analyzerEndpoint,
                degradedReasons)
            .ConfigureAwait(false);
        var storeRoot = RuntimeSettings.ResolveStoreRoot(config);
        var storeWritable = ProbeStoreWritable(storeRoot, degradedReasons);

        var response = new CapabilitiesResponse(
            ProtocolVersion: ProtocolVersion,
            Mode: ExtensionMode,
            Authoritative: false,
            SupportedJellyfinVersion: SupportedJellyfinVersion,
            Modules: new ExtensionModules(
                AnalysisArtifacts: storeWritable,
                TrackMetadataBatching: true,
                // The existing Atlas code still contains resolver and product-semantic
                // decisions. Keep it quarantined until it only caches client-owned
                // serialized projections.
                AtlasProjectionCache: false,
                // Keep provider search out of the negotiated extension contract
                // until Slipmat has a production client-owned equivalent path.
                AcquisitionSearch: false,
                // Podcast Index credentials stay server-side; this module only
                // advertises when both required values are configured.
                PodcastDirectorySearch: PodcastDirectoryController.IsPodcastIndexConfigured(),
                // Podcast subscription state is fully client-owned; the plugin only
                // hosts an optional authenticated replica for cross-device continuity.
                PodcastSubscriptions: storeWritable,
                // Authenticated podcast feed/chapter/transcript acquisition is now
                // available through the bounded SSRF-resistant resource fetch contract.
                // It remains an execution adapter; the client still owns feed parsing,
                // normalized snapshot semantics, and refresh generation authority.
                PodcastFeedRefresh: true,
                // The server still does not own normalized snapshot persistence.
                PodcastSnapshotCache: false),
            AnalyzerHealthy: analyzerHealthy,
            StoreWritable: storeWritable,
            DegradedReasons: degradedReasons);

        CacheResponse(response);
        return Ok(response);
    }

    private static CapabilitiesResponse? TryGetCachedResponse()
    {
        lock (CacheLock)
        {
            if (_cachedResponse is null)
            {
                return null;
            }

            return DateTimeOffset.UtcNow - _cachedAt <= CacheDuration
                ? _cachedResponse
                : null;
        }
    }

    private static void CacheResponse(CapabilitiesResponse response)
    {
        lock (CacheLock)
        {
            _cachedResponse = response;
            _cachedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task<bool> ProbeAnalyzerHealthAsync(
        AnalyzerEndpoint endpoint,
        List<string> degradedReasons)
    {
        if (!endpoint.ExplicitlyConfigured || string.IsNullOrWhiteSpace(endpoint.BaseUrl))
        {
            return false;
        }

        var url = endpoint.BaseUrl.TrimEnd('/') + "/health";
        try
        {
            var response = await _http.GetAsync(url).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            var reason = $"analyzer_unhealthy: HTTP {(int)response.StatusCode}";
            _logger.LogWarning("Analyzer health probe returned {StatusCode}", (int)response.StatusCode);
            degradedReasons.Add(reason);
            return false;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Analyzer health probe timed out ({Url})", url);
            degradedReasons.Add("analyzer_timeout");
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Analyzer health probe failed ({Url}): {Message}", url, ex.Message);
            degradedReasons.Add($"analyzer_unreachable: {ex.GetType().Name}");
            return false;
        }
    }

    private bool ProbeStoreWritable(string storeRoot, List<string> degradedReasons)
    {
        var probe = Path.Combine(storeRoot, ".write_probe");
        try
        {
            Directory.CreateDirectory(storeRoot);
            System.IO.File.WriteAllText(probe, "probe");
            System.IO.File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Store writability probe failed ({StoreRoot}): {Message}", storeRoot, ex.Message);
            degradedReasons.Add($"store_not_writable: {ex.GetType().Name}");
            return false;
        }
    }
}
