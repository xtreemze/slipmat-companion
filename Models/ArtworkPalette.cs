using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>
/// One representative artwork colour. Weight is normalized across the emitted
/// palette and describes sampling evidence only; presentation policy remains client-owned.
/// </summary>
public record ArtworkPaletteSwatchV1(
    [property: JsonPropertyName("red")] byte Red,
    [property: JsonPropertyName("green")] byte Green,
    [property: JsonPropertyName("blue")] byte Blue,
    [property: JsonPropertyName("weight")] double Weight
);

/// <summary>
/// Non-authoritative, artwork-derived colour evidence for immediate client presentation.
/// The companion exposes raw swatches and provenance, never UI-specific CSS/token policy.
/// </summary>
public record ArtworkPaletteV1(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("sourceKind")] string SourceKind,
    [property: JsonPropertyName("sourceItemId")] string SourceItemId,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("swatches")] IReadOnlyList<ArtworkPaletteSwatchV1> Swatches
)
{
    public const int CurrentVersion = 1;
}
