using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>
/// Authenticated, bounded fetch request for podcast syndication companion resources.
/// This is intentionally not a generic HTTP proxy: callers must declare one of the
/// supported podcast resource kinds and cannot provide arbitrary target headers.
/// </summary>
public record PodcastResourceFetchRequest(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("etag")] string? ETag = null,
    [property: JsonPropertyName("lastModified")] string? LastModified = null
);

/// <summary>
/// Result of a bounded podcast resource fetch. The body is base64 so the client
/// receives the exact response bytes and can apply the resource's declared charset.
/// </summary>
public record PodcastResourceFetchResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("finalUrl")] string FinalUrl,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("bodyBase64")] string? BodyBase64,
    [property: JsonPropertyName("lengthBytes")] long LengthBytes,
    [property: JsonPropertyName("etag")] string? ETag,
    [property: JsonPropertyName("lastModified")] string? LastModified
);
