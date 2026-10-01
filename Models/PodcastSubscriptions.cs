using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>Provider-local resource identity used by podcast feed refs.</summary>
public record PodcastProviderResourceRefV1(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("resourceId")] string ResourceId);

/// <summary>Provider-neutral feed identity mirrored from Slipmat's episodic contract.</summary>
public record PodcastSyndicationFeedRefV1(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("resource")] PodcastProviderResourceRefV1 Resource);

/// <summary>Logical replica revision for deterministic subscription merge.</summary>
public record PodcastSubscriptionRevisionV1(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("originId")] string OriginId);

/// <summary>
/// Portable subscription state. User identity is intentionally absent; the
/// authenticated Jellyfin request determines the per-user storage namespace.
/// </summary>
public record PodcastSubscriptionRecordV1(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("subscriptionId")] string SubscriptionId,
    [property: JsonPropertyName("feed")] PodcastSyndicationFeedRefV1 Feed,
    [property: JsonPropertyName("portableFeedLocator")] string? PortableFeedLocator,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdRevision")] PodcastSubscriptionRevisionV1 CreatedRevision,
    [property: JsonPropertyName("revision")] PodcastSubscriptionRevisionV1 Revision);

/// <summary>Persisted per-user subscription document.</summary>
public record PodcastSubscriptionDocumentV1(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("records")] List<PodcastSubscriptionRecordV1> Records);

/// <summary>Client-to-companion replica merge request.</summary>
public record PodcastSubscriptionSyncRequest(
    [property: JsonPropertyName("records")] List<PodcastSubscriptionRecordV1>? Records);

/// <summary>Current server replica plus merge accounting.</summary>
public record PodcastSubscriptionSyncResponse(
    [property: JsonPropertyName("records")] List<PodcastSubscriptionRecordV1> Records,
    [property: JsonPropertyName("applied")] int Applied,
    [property: JsonPropertyName("unchanged")] int Unchanged,
    [property: JsonPropertyName("rejected")] int Rejected,
    [property: JsonPropertyName("truncated")] bool Truncated);
