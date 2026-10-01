using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

public record CreditEntry(
    [property: JsonPropertyName("role")]  string Role,
    [property: JsonPropertyName("names")] List<string> Names
);

public record StudioEntry(
    [property: JsonPropertyName("name")]     string Name,
    [property: JsonPropertyName("location")] string? Location = null
);

public record LinerNotes(
    [property: JsonPropertyName("text")]     string Text,
    [property: JsonPropertyName("language")] string? Language = null
);

/// <summary>Truncated credits (max 3 entries) for inline display in TrackEvent.</summary>
public record CreditsPreview(
    [property: JsonPropertyName("entries")]   List<CreditEntry>? Entries = null,
    [property: JsonPropertyName("studios")]   List<StudioEntry>? Studios = null,
    [property: JsonPropertyName("truncated")] bool Truncated = false
);

/// <summary>Full credits with liner notes and MusicBrainz linkage. Used in AlbumEvent and ArtistEvent.</summary>
public record FullCredits(
    [property: JsonPropertyName("entries")]     List<CreditEntry>? Entries = null,
    [property: JsonPropertyName("studios")]     List<StudioEntry>? Studios = null,
    [property: JsonPropertyName("linerNotes")]  LinerNotes? LinerNotes = null,
    [property: JsonPropertyName("mbReleaseId")] string? MbReleaseId = null
);
