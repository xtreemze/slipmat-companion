using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
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
    private readonly IntegratedAudioAnalyzer _analyzer;

    public CapabilitiesController(
        ILogger<CapabilitiesController> logger,
        IntegratedAudioAnalyzer analyzer)
    {
        _logger = logger;
        _analyzer = analyzer;
    }

    [HttpGet("diagnostics/capabilities")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(CapabilitiesResponse), StatusCodes.Status200OK)]
    public ActionResult<CapabilitiesResponse> GetCapabilities()
    {
        var cached = TryGetCachedResponse();
        if (cached is not null)
        {
            return Ok(cached);
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var degradedReasons = new List<string>();
        var analyzerHealthy = _analyzer.IsAvailable;
        if (!analyzerHealthy)
        {
            degradedReasons.Add("integrated_analyzer_unavailable");
        }

        var storeRoot = RuntimeSettings.ResolveStoreRoot(config);
        var storeWritable = ProbeStoreWritable(storeRoot, degradedReasons);

        var response = new CapabilitiesResponse(
            ProtocolVersion: ProtocolVersion,
            Mode: ExtensionMode,
            Authoritative: false,
            SupportedJellyfinVersion: SupportedJellyfinVersion,
            Modules: new ExtensionModules(
                AnalysisArtifacts: analyzerHealthy && storeWritable,
                TrackMetadataBatching: true,
                AtlasProjectionCache: false,
                AcquisitionSearch: false,
                DirectoryResourceFetch: true,
                LiveGuideResourceFetch: true,
                PodcastDirectorySearch: PodcastDirectoryController.IsPodcastIndexConfigured(),
                PodcastSubscriptions: storeWritable,
                PodcastFeedRefresh: true,
                PodcastSnapshotCache: false,
                RemoteMediaRelay: true),
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
            _logger.LogWarning(
                "Store writability probe failed ({StoreRoot}): {Message}",
                storeRoot,
                ex.Message);
            degradedReasons.Add($"store_not_writable: {ex.GetType().Name}");
            return false;
        }
    }
}
