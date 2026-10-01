using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>Reference to a waveform binary artifact.</summary>
/// <remarks>
/// In analyzer-written sidecars: <c>Path</c> is set, <c>Url</c> is null.<br/>
/// In gateway-served responses: <c>Url</c> is set (API URL); <c>Path</c> may also be present.
/// </remarks>
public record WaveformRef(
    [property: JsonPropertyName("itemId")]          string ItemId,
    [property: JsonPropertyName("variant")]         string Variant,
    [property: JsonPropertyName("pps")]             int Pps,
    [property: JsonPropertyName("etag")]            string Etag,
    [property: JsonPropertyName("path")]            string? Path = null,
    [property: JsonPropertyName("url")]             string? Url = null,
    [property: JsonPropertyName("durationSamples")] long? DurationSamples = null
);
