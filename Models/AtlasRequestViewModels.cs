using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

public record AtlasAcquisitionRequestRecord(
    [property: JsonPropertyName("albumId")] string AlbumId,
    [property: JsonPropertyName("conceptId")] string ConceptId,
    [property: JsonPropertyName("releaseId")] string? ReleaseId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artistName")] string ArtistName,
    [property: JsonPropertyName("requestedAt")] string RequestedAt,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("receivedAt")] string? ReceivedAt = null,
    [property: JsonPropertyName("providerSource")] string? ProviderSource = null,
    [property: JsonPropertyName("providerAlbumId")] string? ProviderAlbumId = null,
    [property: JsonPropertyName("providerDescription")] string? ProviderDescription = null
);

public record AtlasAcquisitionRequestListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<AtlasAcquisitionRequestRecord> Items
);
