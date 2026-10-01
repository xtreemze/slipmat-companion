using System;
using System.Collections.Generic;
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

    public TrackPlaybackEventService(
        ILibraryManager library,
        JellyfinMetadataAdapter metadataAdapter,
        SidecarLoader sidecarLoader,
        JellyfinAnalysisSubjectFactory subjectFactory)
    {
        _library = library;
        _metadataAdapter = metadataAdapter;
        _sidecarLoader = sidecarLoader;
        _subjectFactory = subjectFactory;
    }

    public TrackPlaybackEvent? ComposeTrackEvent(
        Guid itemGuid,
        IncludeFlags include,
        int waveformPps,
        string storeRoot)
    {
        var item = _library.GetItemById(itemGuid);
        if (item is not Audio)
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
        var sidecar = _sidecarLoader.LoadSidecarFromStore(storeRoot, subjectStoreKey);

        return TrackPlaybackEventComposer.Compose(
            track,
            artifactItemId,
            sidecar,
            include,
            waveformPps);
    }

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
