using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

// ── Shared building blocks ────────────────────────────────────────────────────

/// <summary>Minimal artist representation for card/chip display.</summary>
public record ArtistCard(
    [property: JsonPropertyName("id")]        string Id,
    [property: JsonPropertyName("name")]      string Name,
    [property: JsonPropertyName("avatarUrl")] string? AvatarUrl = null
);

/// <summary>Minimal album representation for card/grid display.</summary>
public record AlbumCard(
    [property: JsonPropertyName("id")]          string Id,
    [property: JsonPropertyName("title")]       string Title,
    [property: JsonPropertyName("year")]        int? Year = null,
    [property: JsonPropertyName("artistNames")] List<string>? ArtistNames = null
);

/// <summary>Minimal track representation for list/table row display.</summary>
public record TrackRow(
    [property: JsonPropertyName("itemId")]      string ItemId,
    [property: JsonPropertyName("title")]       string Title,
    [property: JsonPropertyName("durationMs")]  long DurationMs,
    [property: JsonPropertyName("trackNumber")] int? TrackNumber = null,
    [property: JsonPropertyName("discNumber")]  int? DiscNumber = null,
    [property: JsonPropertyName("container")]   string? Container = null
);

/// <summary>
/// All artwork URL references for a track/album/artist.
/// Values are server-relative URI references with width/height params capped at 4000px.
/// </summary>
public record ArtworkSet(
    [property: JsonPropertyName("albumCoverFront")]  string? AlbumCoverFront = null,
    [property: JsonPropertyName("albumCoverBack")]   string? AlbumCoverBack = null,
    [property: JsonPropertyName("albumCoverInset")]  string? AlbumCoverInset = null,
    [property: JsonPropertyName("albumDisc")]        string? AlbumDisc = null,
    [property: JsonPropertyName("albumBackdrops")]   List<string>? AlbumBackdrops = null,
    [property: JsonPropertyName("artistAvatars")]    Dictionary<string, string>? ArtistAvatars = null,
    [property: JsonPropertyName("artistBackdrops")]  Dictionary<string, List<string>>? ArtistBackdrops = null
);

// ── Top-level event view-models ───────────────────────────────────────────────

/// <summary>
/// Full view-model for a now-playing track.
/// Optional blocks (waveform, analysis, creditsPreview) are included when
/// the client sends the corresponding include flags.
/// </summary>
public record TrackEvent(
    [property: JsonPropertyName("track")]          TrackRow Track,
    [property: JsonPropertyName("album")]          AlbumCard Album,
    [property: JsonPropertyName("artists")]        List<ArtistCard> Artists,
    [property: JsonPropertyName("artwork")]        ArtworkSet Artwork,
    [property: JsonPropertyName("cacheToken")]        string CacheToken,
    [property: JsonPropertyName("analysisAvailable")] bool AnalysisAvailable = false,
    [property: JsonPropertyName("waveform")]       WaveformRef? Waveform = null,
    [property: JsonPropertyName("analysis")]       AnalysisBlocks? Analysis = null,
    [property: JsonPropertyName("creditsPreview")] CreditsPreview? CreditsPreview = null
);

/// <summary>Full view-model for an album detail page.</summary>
public record AlbumEvent(
    [property: JsonPropertyName("album")]      AlbumCard Album,
    [property: JsonPropertyName("artists")]    List<ArtistCard> Artists,
    [property: JsonPropertyName("artwork")]    ArtworkSet Artwork,
    [property: JsonPropertyName("cacheToken")] string CacheToken,
    [property: JsonPropertyName("tracks")]     List<TrackRow>? Tracks = null,
    [property: JsonPropertyName("credits")]    FullCredits? Credits = null
);

/// <summary>Full view-model for an artist detail page.</summary>
public record ArtistEvent(
    [property: JsonPropertyName("artist")]     ArtistCard Artist,
    [property: JsonPropertyName("artwork")]    ArtworkSet Artwork,
    [property: JsonPropertyName("cacheToken")] string CacheToken,
    [property: JsonPropertyName("bio")]        string? Bio = null,
    [property: JsonPropertyName("discography")]List<AlbumCard>? Discography = null,
    [property: JsonPropertyName("credits")]    FullCredits? Credits = null
);
