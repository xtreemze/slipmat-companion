using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>
/// Compact summary of a generated sidecar used by the playback client.
/// </summary>
public record AnalysisSidecarSummary(
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("sourceFingerprint")] string SourceFingerprint,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt
);

/// <summary>
/// Availability booleans for optional playback event fields.
/// </summary>
public record TrackPlaybackAvailability(
    [property: JsonPropertyName("hasWaveform")] bool HasWaveform,
    [property: JsonPropertyName("hasSidecar")] bool HasSidecar,
    [property: JsonPropertyName("hasDerivedAnalysis")] bool HasDerivedAnalysis,
    [property: JsonPropertyName("hasArtworkPalette")] bool HasArtworkPalette = false
);

/// <summary>
/// Client-facing playback event response for a single Jellyfin track.
/// </summary>
public record TrackPlaybackEvent(
    [property: JsonPropertyName("track")] TrackRow Track,
    [property: JsonPropertyName("waveform")] WaveformRef? Waveform,
    [property: JsonPropertyName("sidecar")] AnalysisSidecarSummary? Sidecar,
    [property: JsonPropertyName("analysis")] AnalysisBlocks? Analysis,
    [property: JsonPropertyName("availability")] TrackPlaybackAvailability Availability,
    [property: JsonPropertyName("artworkPalette")] ArtworkPaletteV1? ArtworkPalette = null
);

/// <summary>
/// Batch request for playback event responses.
/// </summary>
public record BatchTrackPlaybackEventRequest(
    [property: JsonPropertyName("itemIds")] string[] ItemIds,
    [property: JsonPropertyName("include")] string? Include = null,
    [property: JsonPropertyName("waveformPps")] int WaveformPps = 10
);

/// <summary>
/// Batch response for playback event lookups.
/// </summary>
public record BatchTrackPlaybackEventResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<TrackPlaybackEvent> Items
);
