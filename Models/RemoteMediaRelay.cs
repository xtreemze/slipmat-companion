using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>
/// Narrow request for preparing an opaque server-side relay to one public media URL.
/// The request accepts no caller headers, cookies, methods, credentials, or local paths.
/// </summary>
public record RemoteMediaRelayPrepareRequest(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("kind")] string Kind);

/// <summary>
/// Prepared relay capability. The browser-facing path contains only an opaque handle;
/// the upstream locator remains server-private.
/// </summary>
public record RemoteMediaRelayPrepareResponse(
    [property: JsonPropertyName("relayId")] string RelayId,
    [property: JsonPropertyName("mediaPath")] string MediaPath,
    [property: JsonPropertyName("expiresAtMs")] long ExpiresAtMs,
    [property: JsonPropertyName("sampleAccess")] string SampleAccess);
