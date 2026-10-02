using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <remarks>
/// BeatGrid fields are <c>double</c> because <c>intervalMs = 60000 / bpm</c> is generally non-integer.
/// Rounding to integer ms accumulates ~50ms drift over 100 beats — audible at mixing time.
/// All other time fields in AnalysisBlocks use <c>long</c> (integer milliseconds).
/// </remarks>
public record BeatGrid(
    [property: JsonPropertyName("startOffsetMs")] double StartOffsetMs,
    [property: JsonPropertyName("intervalMs")]    double IntervalMs
);

public record TimingAnalysis(
    [property: JsonPropertyName("bpm")]           double Bpm,
    [property: JsonPropertyName("bpmConfidence")] double BpmConfidence,
    /// <summary>Downbeat positions in integer milliseconds from track start.</summary>
    [property: JsonPropertyName("downbeats")]     List<long>? Downbeats = null,
    [property: JsonPropertyName("beatGrid")]      BeatGrid? BeatGrid = null
);

public record HarmonicAnalysis(
    [property: JsonPropertyName("key")]           string Key,
    [property: JsonPropertyName("camelotKey")]    string CamelotKey,
    [property: JsonPropertyName("keyConfidence")] double KeyConfidence
);

public record LoudnessAnalysis(
    [property: JsonPropertyName("integratedLufs")] double IntegratedLufs,
    [property: JsonPropertyName("truePeak")]        double TruePeak,
    [property: JsonPropertyName("dynamicRange")]    double DynamicRange
);

public record LoudnessAnalyzerIdentityV1(
    [property: JsonPropertyName("name")]                string Name,
    [property: JsonPropertyName("version")]             string Version,
    [property: JsonPropertyName("configurationDigest")] string? ConfigurationDigest = null
);

/// <summary>
/// Provider-neutral measured loudness facts. This is evidence only: no
/// normalization target, gain selection, headroom policy, or render state.
/// </summary>
public record LoudnessMeasurementEvidenceV1(
    [property: JsonPropertyName("version")]         int Version,
    [property: JsonPropertyName("scope")]           string Scope,
    [property: JsonPropertyName("authority")]       string Authority,
    [property: JsonPropertyName("semantics")]       string Semantics,
    [property: JsonPropertyName("analyzer")]        LoudnessAnalyzerIdentityV1 Analyzer,
    [property: JsonPropertyName("integratedLufs")]  double IntegratedLufs,
    [property: JsonPropertyName("truePeakDbtp")]    double? TruePeakDbtp = null,
    [property: JsonPropertyName("loudnessRangeLu")] double? LoudnessRangeLu = null
);

public record EnergyCurvePoint(
    /// <summary>Sample position in integer milliseconds from track start.</summary>
    [property: JsonPropertyName("timeMs")]  long TimeMs,
    [property: JsonPropertyName("energy")] double Energy
);

public record Section(
    /// <summary>Section start in integer milliseconds.</summary>
    [property: JsonPropertyName("startMs")] long StartMs,
    /// <summary>Section end in integer milliseconds.</summary>
    [property: JsonPropertyName("endMs")]   long EndMs,
    [property: JsonPropertyName("label")]   string Label
);

public record StructureAnalysis(
    [property: JsonPropertyName("energyCurve")] List<EnergyCurvePoint>? EnergyCurve = null,
    [property: JsonPropertyName("sections")]    List<Section>? Sections = null
);

public record TransitionCandidate(
    /// <summary>Incoming transition window start in integer milliseconds.</summary>
    [property: JsonPropertyName("inWindowMs")]  long InWindowMs,
    /// <summary>Outgoing transition window start in integer milliseconds.</summary>
    [property: JsonPropertyName("outWindowMs")] long OutWindowMs,
    [property: JsonPropertyName("score")]       double Score,
    [property: JsonPropertyName("reasonCodes")] List<string>? ReasonCodes = null
);

public record AnalysisBlocks(
    [property: JsonPropertyName("timing")]               TimingAnalysis Timing,
    [property: JsonPropertyName("harmonic")]             HarmonicAnalysis Harmonic,
    [property: JsonPropertyName("loudness")]             LoudnessAnalysis Loudness,
    [property: JsonPropertyName("structure")]            StructureAnalysis? Structure = null,
    [property: JsonPropertyName("transitions")]          List<TransitionCandidate>? Transitions = null,
    [property: JsonPropertyName("loudnessMeasurement")] LoudnessMeasurementEvidenceV1? LoudnessMeasurement = null
);
