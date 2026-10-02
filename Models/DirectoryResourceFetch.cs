using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>
/// Narrow request for fetching one allowlisted public directory JSON resource.
/// Provider semantics remain client-owned.
/// </summary>
public sealed record DirectoryResourceFetchRequest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("url")] string Url);

/// <summary>
/// Raw bounded directory evidence returned to the canonical client adapter.
/// </summary>
public sealed record DirectoryResourceFetchResponse(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("finalUrl")] string FinalUrl,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("bodyBase64")] string BodyBase64,
    [property: JsonPropertyName("lengthBytes")] long LengthBytes);
