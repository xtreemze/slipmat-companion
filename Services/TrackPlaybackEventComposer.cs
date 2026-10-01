using System;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Creates optional client-facing track metadata payloads from Jellyfin metadata and analyzer sidecars.
/// </summary>
public static class TrackPlaybackEventComposer
{
    private const string ArtifactBasePath = "/Plugins/AudioGateway/artifacts/waveform";

    public static TrackPlaybackEvent Compose(
        TrackRow track,
        string artifactItemId,
        AnalysisSidecar? sidecar,
        IncludeFlags include,
        int waveformPps)
    {
        var waveform = include.HasFlag(IncludeFlags.Waveform) && sidecar is not null
            ? BuildWaveformRef(artifactItemId, sidecar, waveformPps)
            : null;

        var sidecarSummary = include.HasFlag(IncludeFlags.Sidecar) && sidecar is not null
            ? new AnalysisSidecarSummary(
                ItemId: artifactItemId,
                SourceFingerprint: sidecar.SourceFingerprint,
                DurationMs: sidecar.DurationMs,
                GeneratedAt: sidecar.GeneratedAt)
            : null;

        var analysis = include.HasFlag(IncludeFlags.Analysis)
            ? sidecar?.Analysis
            : null;

        var availability = new TrackPlaybackAvailability(
            HasWaveform: sidecar?.WaveformRefs?.Count > 0,
            HasSidecar: sidecar is not null,
            HasDerivedAnalysis: sidecar?.Analysis is not null);

        return new TrackPlaybackEvent(
            Track: track,
            Waveform: waveform,
            Sidecar: sidecarSummary,
            Analysis: analysis,
            Availability: availability);
    }

    private static WaveformRef? BuildWaveformRef(
        string artifactItemId,
        AnalysisSidecar sidecar,
        int waveformPps)
    {
        var stored = sidecar.WaveformRefs.FirstOrDefault(reference => reference.Pps == waveformPps);
        if (stored is null)
        {
            return null;
        }

        var url = $"{ArtifactBasePath}/{artifactItemId}?variant={Uri.EscapeDataString(stored.Variant)}&pps={stored.Pps}";
        return new WaveformRef(
            ItemId: artifactItemId,
            Variant: stored.Variant,
            Pps: stored.Pps,
            Etag: stored.Etag,
            Path: stored.Path,
            Url: url,
            DurationSamples: stored.DurationSamples);
    }
}
