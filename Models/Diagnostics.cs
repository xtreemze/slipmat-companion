using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>
/// Optional extension modules. Every module accelerates or enriches a capability
/// that Slipmat can still execute without the plugin.
/// </summary>
public record ExtensionModules(
    [property: JsonPropertyName("analysisArtifacts")] bool AnalysisArtifacts,
    [property: JsonPropertyName("trackMetadataBatching")] bool TrackMetadataBatching,
    [property: JsonPropertyName("atlasProjectionCache")] bool AtlasProjectionCache,
    [property: JsonPropertyName("acquisitionSearch")] bool AcquisitionSearch,
    [property: JsonPropertyName("directoryResourceFetch")] bool DirectoryResourceFetch,
    [property: JsonPropertyName("liveGuideResourceFetch")] bool LiveGuideResourceFetch,
    [property: JsonPropertyName("podcastDirectorySearch")] bool PodcastDirectorySearch,
    [property: JsonPropertyName("podcastSubscriptions")] bool PodcastSubscriptions,
    [property: JsonPropertyName("podcastFeedRefresh")] bool PodcastFeedRefresh,
    [property: JsonPropertyName("podcastSnapshotCache")] bool PodcastSnapshotCache,
    [property: JsonPropertyName("remoteMediaRelay")] bool RemoteMediaRelay
);

/// <summary>
/// Response for GET /Plugins/AudioGateway/diagnostics/capabilities.
/// The plugin is an optional acceleration/enrichment adapter and must never be
/// authoritative for a Slipmat product capability.
/// </summary>
public record CapabilitiesResponse(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("authoritative")] bool Authoritative,
    [property: JsonPropertyName("supportedJellyfinVersion")] string SupportedJellyfinVersion,
    [property: JsonPropertyName("modules")] ExtensionModules Modules,
    [property: JsonPropertyName("analyzerHealthy")] bool AnalyzerHealthy,
    [property: JsonPropertyName("storeWritable")] bool StoreWritable,
    [property: JsonPropertyName("degradedReasons")] List<string> DegradedReasons
);
