using System;
using System.IO;
using System.Net.Mime;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Serves optional precomputed analysis artifacts from the shared store.
///
///   GET /Plugins/AudioGateway/artifacts/analysis/{itemId}
///       → sidecar JSON with ETag and 304 support
///
///   GET /Plugins/AudioGateway/artifacts/waveform/{itemId}?variant=&amp;pps=
///       → binary artifact with ETag and 304 support
///
/// These endpoints are accelerators only. Slipmat must remain capable of
/// producing equivalent analysis locally when they are absent.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway")]
public class ArtifactsController : ControllerBase
{
    private readonly ILogger<ArtifactsController> _logger;
    private readonly SidecarLoader _sidecarLoader;
    private readonly JellyfinAnalysisSubjectFactory _subjectFactory;
    private readonly IntegratedAnalysisWorker _analysisWorker;
    private readonly ILibraryManager _library;

    public ArtifactsController(
        ILogger<ArtifactsController> logger,
        SidecarLoader sidecarLoader,
        JellyfinAnalysisSubjectFactory subjectFactory,
        IntegratedAnalysisWorker analysisWorker,
        ILibraryManager library)
    {
        _logger = logger;
        _sidecarLoader = sidecarLoader;
        _subjectFactory = subjectFactory;
        _analysisWorker = analysisWorker;
        _library = library;
    }

    /// <summary>
    /// GET /Plugins/AudioGateway/artifacts/analysis/{itemId}
    /// </summary>
    [HttpGet("artifacts/analysis/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetAnalysisArtifact(string itemId)
    {
        string subjectStoreKey;
        try
        {
            subjectStoreKey = _subjectFactory.ForItem(itemId).StoreKey();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var storeRoot = RuntimeSettings.ResolveStoreRoot(config);
        var sidecarPath = StorePaths.SidecarPath(storeRoot, subjectStoreKey);
        if (!StorePaths.IsWithinRoot(storeRoot, sidecarPath))
        {
            return BadRequest("Resolved sidecar path escaped the configured store root.");
        }

        var sidecar = _sidecarLoader.LoadSidecarFromStore(storeRoot, subjectStoreKey);
        var parsedItemId = Guid.TryParse(itemId, out var itemGuid) ? itemGuid : (Guid?)null;
        if (sidecar is not null
            && parsedItemId is Guid currentItemId
            && IsStaleCloudArtifact(config, storeRoot, subjectStoreKey, currentItemId))
        {
            sidecar = null;
        }

        if (sidecar is null)
        {
            _logger.LogDebug(
                "Compatible current sidecar not found for itemId={ItemId} subject={SubjectStoreKey}",
                itemId,
                subjectStoreKey);
            if (parsedItemId is Guid missingItemId)
            {
                _analysisWorker.TryEnqueue(missingItemId);
            }

            return NotFound();
        }

        var etagValue = BuildETagForAnalysis(sidecar.SourceFingerprint);
        var etag = new EntityTagHeaderValue(etagValue);

        if (Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var clientEtag) &&
            EntityTagHeaderValue.TryParse(clientEtag.ToString(), out var parsed) &&
            etag.Compare(parsed, useStrongComparison: false))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers[HeaderNames.ETag] = etag.ToString();
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        var stream = System.IO.File.OpenRead(sidecarPath);
        return File(stream, MediaTypeNames.Application.Json, enableRangeProcessing: true);
    }

    /// <summary>
    /// GET /Plugins/AudioGateway/artifacts/waveform/{itemId}?variant=awf_v1_riff_mono_u8_peak&amp;pps=10
    /// </summary>
    [HttpGet("artifacts/waveform/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetWaveformArtifact(
        string itemId,
        [FromQuery] string variant = "awf_v1_riff_mono_u8_peak",
        [FromQuery] int pps = 10)
    {
        string subjectStoreKey;
        try
        {
            subjectStoreKey = _subjectFactory.ForItem(itemId).StoreKey();
            StorePaths.ValidatePathSegment(variant);
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }

        if (pps is not (1 or 10 or 100))
            return BadRequest($"Invalid pps={pps}. Accepted: 1, 10, 100.");

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var storeRoot = RuntimeSettings.ResolveStoreRoot(config);
        var datPath = StorePaths.WaveformDatPath(storeRoot, subjectStoreKey, variant, pps);
        if (!StorePaths.IsWithinRoot(storeRoot, datPath))
        {
            return BadRequest("Resolved waveform path escaped the configured store root.");
        }

        if (!System.IO.File.Exists(datPath))
        {
            _logger.LogDebug("Waveform not found: itemId={ItemId} variant={Variant} pps={Pps}", itemId, variant, pps);
            if (string.Equals(
                    variant,
                    IntegratedAudioAnalyzer.AmplitudeVariant,
                    StringComparison.Ordinal) &&
                Guid.TryParse(itemId, out var missingItemId))
            {
                _analysisWorker.TryEnqueue(missingItemId);
            }

            return NotFound();
        }

        var sidecar = _sidecarLoader.LoadSidecarFromStore(storeRoot, subjectStoreKey);
        if (sidecar is null
            || (Guid.TryParse(itemId, out var currentItemId)
                && IsStaleCloudArtifact(
                    config,
                    storeRoot,
                    subjectStoreKey,
                    currentItemId)))
        {
            _logger.LogDebug(
                "Waveform sidecar missing, mismatched, or stale for itemId={ItemId} subject={SubjectStoreKey}",
                itemId,
                subjectStoreKey);
            if (Guid.TryParse(itemId, out var missingItemId))
            {
                _analysisWorker.TryEnqueue(missingItemId);
            }

            return NotFound();
        }

        var etagValue = BuildETagForWaveform(sidecar.SourceFingerprint, variant, pps);

        var etag = new EntityTagHeaderValue(etagValue);

        if (Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var clientEtag) &&
            EntityTagHeaderValue.TryParse(clientEtag.ToString(), out var parsed) &&
            etag.Compare(parsed, useStrongComparison: false))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers[HeaderNames.ETag] = etag.ToString();
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        var stream = System.IO.File.OpenRead(datPath);
        return File(stream, MediaTypeNames.Application.Octet, enableRangeProcessing: true);
    }


    private bool IsStaleCloudArtifact(
        PluginConfiguration config,
        string storeRoot,
        string subjectStoreKey,
        Guid itemId)
    {
        var audio = _library.GetItemById(itemId) as Audio;
        return audio is not null
            && RuntimeSettings.IsCloudProjectionPath(config, audio.Path)
            && !AnalysisSourceStamp.IsCurrent(storeRoot, subjectStoreKey, audio.Path);
    }

    public static string BuildETagForAnalysis(string sourceFingerprint)
        => $"\"{sourceFingerprint}:sidecar:{AnalysisSidecar.CurrentSchemaVersion}\"";

    public static string BuildETagForWaveform(string sourceFingerprint, string variant, int pps)
        => $"\"{sourceFingerprint}:{variant}:{pps}\"";
}
