using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>Provider-neutral podcast directory request owned by the Slipmat client.</summary>
public sealed record PodcastDirectorySearchRequest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("requestGeneration")] long RequestGeneration,
    [property: JsonPropertyName("limit")] int Limit);

/// <summary>Normalized directory evidence. The publisher feed remains canonical input.</summary>
public sealed record PodcastDirectorySearchResult(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("resourceId")] string ResourceId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("publisher")] string? Publisher,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("artworkUrl")] string? ArtworkUrl,
    [property: JsonPropertyName("directoryUrl")] string? DirectoryUrl,
    [property: JsonPropertyName("feedLocator")] string FeedLocator);

/// <summary>Bounded search result returned to an authenticated Slipmat client.</summary>
public sealed record PodcastDirectorySearchResponse(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("requestGeneration")] long RequestGeneration,
    [property: JsonPropertyName("observedAtMs")] long ObservedAtMs,
    [property: JsonPropertyName("results")] IReadOnlyList<PodcastDirectorySearchResult> Results);
