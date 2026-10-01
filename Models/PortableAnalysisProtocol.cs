using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>
/// C# projection of the portable host-neutral analysis protocol.
/// Canonical meaning is owned by the shared schema/examples and Rust service.
/// </summary>
public static class PortableAnalysisProtocolV1
{
    public const string Version = "1.0.0";
}

public record PortableAnalysisSubjectRefV1(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("storeKey")] string StoreKey
);

public record PortableAnalysisArtifactRefV1(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("variant")] string Variant,
    [property: JsonPropertyName("ratePerSecond")] int RatePerSecond,
    [property: JsonPropertyName("etag")] string Etag,
    [property: JsonPropertyName("analysisVersion")] int? AnalysisVersion = null
);

public record PortableAnalysisArtifactDescriptorV1(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("subject")] PortableAnalysisSubjectRefV1 Subject,
    [property: JsonPropertyName("sourceFingerprint")] string SourceFingerprint,
    [property: JsonPropertyName("sidecarSchemaVersion")] string SidecarSchemaVersion,
    [property: JsonPropertyName("producerVersion")] string ProducerVersion,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("artifacts")] List<PortableAnalysisArtifactRefV1> Artifacts
);

public record PortableAnalysisCoverageQueryV1(
    [property: JsonPropertyName("subject")] PortableAnalysisSubjectRefV1 Subject,
    [property: JsonPropertyName("expectedSourceFingerprint")] string? ExpectedSourceFingerprint = null,
    [property: JsonPropertyName("expectedSidecarSchemaVersion")] string? ExpectedSidecarSchemaVersion = null
);

public record PortableAnalysisCoverageRequestV1(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("queries")] List<PortableAnalysisCoverageQueryV1> Queries
);

public record PortableAnalysisCoverageItemV1(
    [property: JsonPropertyName("subject")] PortableAnalysisSubjectRefV1 Subject,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("observedSourceFingerprint")] string? ObservedSourceFingerprint = null,
    [property: JsonPropertyName("sidecarSchemaVersion")] string? SidecarSchemaVersion = null,
    [property: JsonPropertyName("producerVersion")] string? ProducerVersion = null,
    [property: JsonPropertyName("reason")] string? Reason = null
);

public record PortableAnalysisCoverageCountsV1(
    [property: JsonPropertyName("ready")] int Ready,
    [property: JsonPropertyName("missing")] int Missing,
    [property: JsonPropertyName("stale")] int Stale,
    [property: JsonPropertyName("unsupported")] int Unsupported,
    [property: JsonPropertyName("failed")] int Failed
);

public record PortableAnalysisCoverageResponseV1(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("items")] List<PortableAnalysisCoverageItemV1> Items,
    [property: JsonPropertyName("counts")] PortableAnalysisCoverageCountsV1 Counts
);
