using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Retrieves Jellyfin item metadata via <see cref="ILibraryManager"/> and converts it
/// to AudioGateway view-model types.
///
/// Static helpers (<see cref="BuildImageUrl"/>, <see cref="BuildCacheToken"/>,
/// <see cref="TicksToMs"/>) are pure and have no Jellyfin runtime dependency —
/// they can be unit-tested without mocking.
///
/// Non-static methods require an <see cref="ILibraryManager"/> instance and are
/// exercised via integration-level tests against a live or stubbed Jellyfin runtime.
/// </summary>
public class JellyfinMetadataAdapter
{
    private readonly ILibraryManager _library;

    /// <summary>
    /// Maximum number of artist backdrop images to include in <see cref="ArtworkSet.ArtistBackdrops"/>.
    /// </summary>
    private const int MaxArtistBackdrops = 3;

    public JellyfinMetadataAdapter(ILibraryManager library)
        => _library = library;

    // ── Static pure helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Builds a Jellyfin-native image URL with an optional cache-busting tag.
    ///
    /// Format (with tag):    /Items/{id}/Images/{type}?quality=90&amp;fillWidth={maxWidth}&amp;tag={imageTag}
    /// Format (without tag): /Items/{id}/Images/{type}?quality=90&amp;fillWidth={maxWidth}
    ///
    /// The <paramref name="imageTag"/> parameter is omitted entirely when null or empty,
    /// so consumers can safely pass the result of <c>item.GetImageTag(type)</c> directly.
    /// </summary>
    /// <param name="itemId">The Jellyfin item GUID.</param>
    /// <param name="imageType">The image type (Primary, Backdrop, etc.).</param>
    /// <param name="maxWidth">Desired max width in logical pixels (used as fillWidth).</param>
    /// <param name="imageTag">
    ///   Optional image tag returned by Jellyfin for cache-busting.
    ///   Pass null or empty string to omit the tag query param.
    /// </param>
    /// <returns>A server-relative URI reference string.</returns>
    public static string BuildImageUrl(
        Guid itemId,
        ImageType imageType,
        int maxWidth,
        string? imageTag = null)
    {
        var baseUrl = $"/Items/{itemId}/Images/{imageType}?quality=90&fillWidth={maxWidth}";
        return string.IsNullOrEmpty(imageTag) ? baseUrl : $"{baseUrl}&tag={imageTag}";
    }

    /// <summary>
    /// Builds a stable, opaque cache token from a Jellyfin item ID and its ticks value.
    ///
    /// The token is a URL-safe Base64 encoding of a compact JSON object:
    ///   <c>{"e":"&lt;itemId&gt;","t":&lt;ticks&gt;}</c>
    ///
    /// The same inputs always produce the same output (deterministic).
    /// Clients should treat this as an opaque string — do not parse it.
    /// </summary>
    /// <param name="itemId">String form of the item GUID.</param>
    /// <param name="ticks">Item DateLastSaved or DateModified in 100-nanosecond ticks.</param>
    /// <returns>URL-safe Base64 string.</returns>
    public static string BuildCacheToken(string itemId, long ticks)
    {
        // Compact JSON: {"e":"<itemId>","t":<ticks>}
        var json = $"{{\"e\":\"{itemId}\",\"t\":{ticks}}}";
        var bytes = Encoding.UTF8.GetBytes(json);
        // Use standard Base64 (not URL-safe) to match the spec; clients treat it as opaque.
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Converts Jellyfin ticks (100-nanosecond units) to milliseconds.
    /// </summary>
    /// <param name="ticks">Duration in 100-ns ticks.</param>
    /// <returns>Duration in whole milliseconds (sub-millisecond remainder is truncated, not rounded).</returns>
    public static long TicksToMs(long ticks) => ticks / 10_000L;

    // ── Non-static methods (require ILibraryManager) ──────────────────────────

    /// <summary>
    /// Retrieves a <see cref="TrackRow"/> for the given item ID.
    /// Returns null if the item does not exist or is not an Audio item.
    /// </summary>
    /// <param name="itemId">The Jellyfin item GUID for the audio track.</param>
    /// <returns>A populated <see cref="TrackRow"/>, or null.</returns>
    public TrackRow? GetTrackRow(Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        if (item is not Audio audio)
            return null;

        return new TrackRow(
            ItemId:      itemId.ToString(),
            Title:       audio.Name ?? string.Empty,
            DurationMs:  TicksToMs(audio.RunTimeTicks ?? 0L),
            TrackNumber: audio.IndexNumber,
            DiscNumber:  audio.ParentIndexNumber,
            Container:   audio.Container?.ToLowerInvariant()
        );
    }

    /// <summary>
    /// Retrieves an <see cref="AlbumCard"/> for the given album ID.
    /// Returns null if the item does not exist or is not a MusicAlbum.
    /// </summary>
    /// <param name="albumId">The Jellyfin item GUID for the album.</param>
    /// <returns>A populated <see cref="AlbumCard"/>, or null.</returns>
    public AlbumCard? GetAlbumCard(Guid albumId)
    {
        var item = _library.GetItemById(albumId);
        if (item is not MusicAlbum album)
            return null;

        // Note: artwork URLs for albums are surfaced via BuildArtworkSet / ArtworkSet.AlbumCoverFront.
        // AlbumCard does not carry an ArtworkUrl field in the current view-model contract.
        return new AlbumCard(
            Id:           album.Id.ToString(),
            Title:        album.Name ?? string.Empty,
            Year:         album.ProductionYear,
            ArtistNames:  album.AlbumArtists?.ToList()
        );
    }

    /// <summary>
    /// Retrieves <see cref="ArtistCard"/> records for each supplied artist ID.
    /// Items that are not found or are not MusicArtist entities are silently skipped.
    /// </summary>
    /// <param name="artistIds">Enumerable of Jellyfin item GUIDs for artists.</param>
    /// <returns>List of populated <see cref="ArtistCard"/> objects (may be empty).</returns>
    public List<ArtistCard> GetArtistCards(IEnumerable<Guid> artistIds)
    {
        var result = new List<ArtistCard>();
        foreach (var id in artistIds)
        {
            var item = _library.GetItemById(id);
            if (item is null)
                continue;

            string? avatarUrl = null;
            if (item.HasImage(ImageType.Primary))
            {
                avatarUrl = BuildImageUrl(item.Id, ImageType.Primary, 400, null);
            }

            result.Add(new ArtistCard(
                Id:        item.Id.ToString(),
                Name:      item.Name ?? string.Empty,
                AvatarUrl: avatarUrl
            ));
        }
        return result;
    }

    /// <summary>
    /// Resolves the canonical primary-artwork owner for a track: album first, then
    /// embedded track art. Palette extraction and URL projection share this rule so
    /// they cannot drift onto different artwork revisions.
    /// </summary>
    public BaseItem? ResolvePrimaryArtworkSource(BaseItem track)
    {
        var album = _library.GetItemById(track.ParentId) as MusicAlbum;
        if (album is not null && album.HasImage(ImageType.Primary))
        {
            return album;
        }

        return track.HasImage(ImageType.Primary) ? track : null;
    }

    /// <summary>
    /// Constructs an <see cref="ArtworkSet"/> from a track's album (resolved via the
    /// track's parent chain) and the supplied artist items.
    ///
    /// AlbumCoverBack, AlbumCoverInset, AlbumDisc, and AlbumBackdrops are always null/empty
    /// because Jellyfin does not natively store those image types.
    /// </summary>
    /// <param name="track">The audio track <see cref="BaseItem"/>.</param>
    /// <param name="artists">The artist <see cref="BaseItem"/> instances for this track.</param>
    /// <returns>A fully populated <see cref="ArtworkSet"/>.</returns>
    public ArtworkSet BuildArtworkSet(BaseItem track, IEnumerable<BaseItem> artists)
    {
        // ── Album cover front ────────────────────────────────────────────────
        string? albumCoverFront = null;

        var primaryArtworkSource = ResolvePrimaryArtworkSource(track);
        if (primaryArtworkSource is not null)
        {
            albumCoverFront = BuildImageUrl(
                primaryArtworkSource.Id,
                ImageType.Primary,
                800,
                null);
        }

        // ── Artist avatars ───────────────────────────────────────────────────
        Dictionary<string, string>? artistAvatars = null;
        Dictionary<string, List<string>>? artistBackdrops = null;

        var artistList = artists.ToList();
        if (artistList.Count > 0)
        {
            artistAvatars    = new Dictionary<string, string>(artistList.Count);
            artistBackdrops  = new Dictionary<string, List<string>>(artistList.Count);

            foreach (var artist in artistList)
            {
                var artistKey = artist.Id.ToString();

                // Avatar (Primary image)
                if (artist.HasImage(ImageType.Primary))
                {
                    artistAvatars[artistKey] = BuildImageUrl(artist.Id, ImageType.Primary, 400, null);
                }

                // Backdrops — up to MaxArtistBackdrops
                var backdropItems = artist.GetImages(ImageType.Backdrop).Take(MaxArtistBackdrops).ToList();
                if (backdropItems.Count > 0)
                {
                    var backdropUrls = new List<string>(backdropItems.Count);
                    for (int i = 0; i < backdropItems.Count; i++)
                    {
                        backdropUrls.Add(BuildImageUrl(artist.Id, ImageType.Backdrop, 1280, null));
                    }
                    artistBackdrops[artistKey] = backdropUrls;
                }
            }

            // Normalise empty dicts to null so JSON serialization omits them.
            if (artistAvatars.Count   == 0) artistAvatars   = null;
            if (artistBackdrops.Count == 0) artistBackdrops = null;
        }

        return new ArtworkSet(
            AlbumCoverFront:  albumCoverFront,
            AlbumCoverBack:   null,
            AlbumCoverInset:  null,
            AlbumDisc:        null,
            AlbumBackdrops:   new List<string>(),
            ArtistAvatars:    artistAvatars,
            ArtistBackdrops:  artistBackdrops
        );
    }
}
