using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Resolves Jellyfin audio items into playback event payloads for the web client.
/// </summary>
public class TrackPlaybackEventService
{
    private readonly ILibraryManager _library;
    private readonly JellyfinMetadataAdapter _metadataAdapter;
    private readonly SidecarLoader _sidecarLoader;
    private readonly JellyfinAnalysisSubjectFactory _subjectFactory;
    private readonly IntegratedAnalysisWorker _analysisWorker;
    private readonly ArtworkPaletteService _artworkPaletteService;
    private readonly AnalysisArtifactRetentionService _artifactRetention;

    /// <summary>Creates playback event projection with leased access to cached analysis artifacts.</summary>
    public TrackPlaybackEventService(
        ILibraryManager library,
        JellyfinMetadataAdapter metadataAdapter,
        SidecarLoader sidecarLoader,
        JellyfinAnalysisSubjectFactory subjectFactory,
        IntegratedAnalysisWorker analysisWorker,
        ArtworkPaletteService artworkPaletteService,
        AnalysisArtifactRetentionService artifactRetention)
    {
        _library = library;
        _metadataAdapter = metadataAdapter;
        _sidecarLoader = sidecarLoader;
        _subjectFactory = subjectFactory;
        _analysisWorker = analysisWorker;
        _artworkPaletteService = artworkPaletteService;
        _artifactRetention = artifactRetention;
    }

    /// <summary>Composes one optional playback event from current Jellyfin-owned metadata.</summary>
    public TrackPlaybackEvent? ComposeTrackEvent(
        Guid itemGuid,
        IncludeFlags include,
        int waveformPps,
        string storeRoot)
    {
        var item = _library.GetItemById(itemGuid);
        if (item is not Audio audio)
        {
            return null;
        }

        var track = _metadataAdapter.GetTrackRow(itemGuid);
        if (track is null)
        {
            return null;
        }

        var artifactItemId = itemGuid.ToString("N");
        var subjectStoreKey = _subjectFactory.ForItem(itemGuid).StoreKey();
        AnalysisSidecar? sidecar;
        using (var lease = _artifactRetention.TryAcquire(storeRoot, subjectStoreKey))
        {
            sidecar = lease is null
                ? null
                : _sidecarLoader.LoadSidecarFromStore(storeRoot, subjectStoreKey);
        }
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (sidecar is not null
            && RuntimeSettings.IsCloudProjectionPath(config, audio.Path)
            && !AnalysisSourceStamp.IsCurrent(storeRoot, subjectStoreKey, audio.Path))
        {
            sidecar = null;
        }

        if (sidecar is null)
        {
            _analysisWorker.TryEnqueue(itemGuid);
        }

        var artworkPalette = _artworkPaletteService.ResolveForTrack(audio);

        return TrackPlaybackEventComposer.Compose(
            track,
            artifactItemId,
            sidecar,
            include,
            waveformPps,
            artworkPalette);
    }

    /// <summary>Composes events for the valid Jellyfin items in the supplied ID list.</summary>
    public IReadOnlyList<TrackPlaybackEvent> ComposeTrackEvents(
        IEnumerable<string> itemIds,
        IncludeFlags include,
        int waveformPps,
        string storeRoot)
    {
        var results = new List<TrackPlaybackEvent>();

        foreach (var itemId in itemIds)
        {
            if (!Guid.TryParse(itemId, out var itemGuid))
            {
                continue;
            }

            var trackEvent = ComposeTrackEvent(itemGuid, include, waveformPps, storeRoot);
            if (trackEvent is not null)
            {
                results.Add(trackEvent);
            }
        }

        return results;
    }
}
