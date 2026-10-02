using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>
/// Analyzer-owned reference to one stored waveform or spectral artifact.
/// Provider-local item identity is projected only by the host adapter when
/// building client-facing responses.
/// </summary>
public record StoredWaveformRef(
    [property: JsonPropertyName("variant")] string Variant,
    [property: JsonPropertyName("pps")] int Pps,
    [property: JsonPropertyName("etag")] string Etag,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("durationSamples")] long? DurationSamples = null
);

/// <summary>
/// Host-neutral V2-family sidecar projected from Slipmat's canonical Rust contract to
/// {store}/analysis/{subjectStoreKey}.json.
/// </summary>
public record AnalysisSidecar(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("subjectVersion")] int SubjectVersion,
    [property: JsonPropertyName("subjectStoreKey")] string SubjectStoreKey,
    [property: JsonPropertyName("sourceFingerprint")] string SourceFingerprint,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("waveformRefs")] List<StoredWaveformRef> WaveformRefs,
    [property: JsonPropertyName("producerVersion")] string ProducerVersion,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt,
    [property: JsonPropertyName("analysis")] AnalysisBlocks? Analysis = null,
    [property: JsonPropertyName("spectralRefs")] List<StoredWaveformRef>? SpectralRefs = null,
    [property: JsonPropertyName("spectralAnalysisVersion")] int SpectralAnalysisVersion = 0
)
{
    public const string CurrentSchemaVersion = "2.1.0";

    public bool IsCurrentFor(string subjectStoreKey)
        => SchemaVersion == CurrentSchemaVersion
            && SubjectVersion == AnalysisSubjectV1.SubjectVersion
            && AnalysisSubjectV1.IsStoreKey(SubjectStoreKey)
            && SubjectStoreKey == subjectStoreKey;
}
