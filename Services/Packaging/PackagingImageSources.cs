using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AudioGateway.Services.Packaging;

/// <summary>
/// One packaging capture offered for an album: which Jellyfin image slot it
/// fills (the Package Inspector reads Primary = front, BoxRear = back cover,
/// Disc = disc face, Box/Menu/Art = inner inserts), where it lives, and which
/// source said so.
/// </summary>
public sealed record PackagingImageCandidate(
    ImageType Type,
    string Url,
    string? ThumbnailUrl,
    int? Width,
    int? Height,
    string Source,
    double Rank);

/// <summary>
/// Pure parsers from each source's JSON to packaging candidates. No HTTP, so
/// the mapping policy is tested with recorded payloads.
/// </summary>
public static class PackagingImageSources
{
    public const string CoverArtArchive = "Cover Art Archive";
    public const string AudioDb = "TheAudioDB";
    public const string FanartTv = "fanart.tv";
    public const string Discogs = "Discogs";

    // Real scans first: the Cover Art Archive labels every image by what it
    // is; TheAudioDB curates fewer; Discogs images are unlabelled.
    private const double CoverArtArchiveRank = 9;
    private const double AudioDbRank = 8;
    private const double DiscogsRank = 7;
    private const double FanartTvRank = 7.5;

    /// <summary>Cover Art Archive types → the slot each fills; first type wins.</summary>
    private static readonly (string CaaType, ImageType Slot)[] CaaSlots =
    {
        ("Front", ImageType.Primary),
        ("Back", ImageType.BoxRear),
        ("Medium", ImageType.Disc),
        ("Booklet", ImageType.Box),
        ("Tray", ImageType.Menu),
        ("Liner", ImageType.Art),
        ("Spine", ImageType.Art),
        ("Obi", ImageType.Art),
    };

    /// <summary>
    /// Parses a Cover Art Archive release (or release-group) listing. Only
    /// approved images are offered; unlabelled or unmapped types are skipped.
    /// </summary>
    public static IReadOnlyList<PackagingImageCandidate> ParseCoverArtArchive(string json)
    {
        var candidates = new List<PackagingImageCandidate>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("images", out var images)
            || images.ValueKind != JsonValueKind.Array)
        {
            return candidates;
        }

        foreach (var image in images.EnumerateArray())
        {
            if (image.TryGetProperty("approved", out var approved)
                && approved.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            var url = NormalizeRemoteImageUrl(ReadString(image, "image"), CoverArtArchive);
            if (url is null || !image.TryGetProperty("types", out var types))
            {
                continue;
            }

            var labels = types.EnumerateArray()
                .Select(type => type.GetString())
                .Where(type => type is not null)
                .ToArray();
            var slot = CaaSlots
                .Where(entry => labels.Contains(entry.CaaType, StringComparer.OrdinalIgnoreCase))
                .Select(entry => (ImageType?)entry.Slot)
                .FirstOrDefault();
            if (slot is null)
            {
                continue;
            }

            string? thumbnail = null;
            if (image.TryGetProperty("thumbnails", out var thumbnails))
            {
                thumbnail = NormalizeRemoteImageUrl(ReadString(thumbnails, "500"), CoverArtArchive)
                    ?? NormalizeRemoteImageUrl(ReadString(thumbnails, "large"), CoverArtArchive);
            }

            candidates.Add(new PackagingImageCandidate(
                slot.Value,
                url,
                thumbnail,
                null,
                null,
                CoverArtArchive,
                CoverArtArchiveRank));
        }

        return candidates;
    }

    /// <summary>
    /// Parses a TheAudioDB album lookup/search. Jellyfin's own AudioDB
    /// provider already supplies the front; this adds the back cover and the
    /// disc art it leaves out.
    /// </summary>
    public static IReadOnlyList<PackagingImageCandidate> ParseAudioDb(string json)
    {
        var candidates = new List<PackagingImageCandidate>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("album", out var albums)
            || albums.ValueKind != JsonValueKind.Array)
        {
            return candidates;
        }

        var album = albums.EnumerateArray().FirstOrDefault();
        if (album.ValueKind != JsonValueKind.Object)
        {
            return candidates;
        }

        // The first field present wins for each slot (field names verified
        // against the live v1 API, 2026-10-11; strAlbumThumbBack is legacy).
        void Add(ImageType slot, params string[] fields)
        {
            var url = fields.Select(field => NormalizeRemoteImageUrl(ReadString(album, field), AudioDb))
                .FirstOrDefault(value => value is not null);
            if (url is not null)
            {
                candidates.Add(new PackagingImageCandidate(
                    slot, url, $"{url}/preview", null, null, AudioDb, AudioDbRank));
            }
        }

        Add(ImageType.BoxRear, "strAlbumBack", "strAlbumThumbBack");
        Add(ImageType.Disc, "strAlbumCDart");
        Add(ImageType.Primary, "strAlbumThumbHQ", "strAlbumThumb");
        Add(ImageType.Art, "strAlbumSpine");
        return candidates;
    }


    /// <summary>
    /// fanart.tv music album assets are scoped to a MusicBrainz release group,
    /// not an exact physical edition. Only its albumcover is eligible as a
    /// generic front-cover fallback. cdart is deliberately not projected into
    /// a historical disc scan and the API has no named rear-cover image field.
    /// </summary>
    public static IReadOnlyList<PackagingImageCandidate> ParseFanartTvAlbum(string json, string releaseGroupId)
    {
        var candidates = new List<PackagingImageCandidate>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("albums", out var albums)
            || albums.ValueKind != JsonValueKind.Array)
        {
            return candidates;
        }

        foreach (var album in albums.EnumerateArray())
        {
            if (album.ValueKind != JsonValueKind.Object
                || !string.Equals(ReadString(album, "release_group_id"), releaseGroupId, StringComparison.OrdinalIgnoreCase)
                || !album.TryGetProperty("albumcover", out var covers)
                || covers.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var cover in covers.EnumerateArray())
            {
                if (cover.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var url = NormalizeRemoteImageUrl(ReadString(cover, "url"), FanartTv);
                if (url is null)
                {
                    continue;
                }

                candidates.Add(new PackagingImageCandidate(
                    ImageType.Primary, url, null, null, null, FanartTv, FanartTvRank));
            }
        }

        return candidates;
    }

    /// <summary>Reads the first release id from a Discogs database search.</summary>
    public static long? ParseDiscogsSearchReleaseId(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var result in results.EnumerateArray())
        {
            if (result.TryGetProperty("id", out var id) && id.TryGetInt64(out var value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses a Discogs release. Discogs does not label its images beyond
    /// primary/secondary, so they are offered below the labelled sources: the
    /// primary as the front. Secondary images have no reliable back-cover
    /// designation and may only be offered as unclassified inner inserts.
    /// </summary>
    public static IReadOnlyList<PackagingImageCandidate> ParseDiscogsRelease(string json)
    {
        var candidates = new List<PackagingImageCandidate>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("images", out var images)
            || images.ValueKind != JsonValueKind.Array)
        {
            return candidates;
        }

        var secondarySlots = new[] { ImageType.Box, ImageType.Menu, ImageType.Art };
        var secondary = 0;
        foreach (var image in images.EnumerateArray())
        {
            var url = NormalizeRemoteImageUrl(ReadString(image, "uri"), Discogs);
            if (url is null)
            {
                continue;
            }

            ImageType slot;
            if (string.Equals(ReadString(image, "type"), "primary", StringComparison.OrdinalIgnoreCase))
            {
                slot = ImageType.Primary;
            }
            else if (secondary < secondarySlots.Length)
            {
                slot = secondarySlots[secondary++];
            }
            else
            {
                continue;
            }

            candidates.Add(new PackagingImageCandidate(
                slot,
                url,
                NormalizeRemoteImageUrl(ReadString(image, "uri150"), Discogs),
                ReadInt(image, "width"),
                ReadInt(image, "height"),
                Discogs,
                DiscogsRank));
        }

        return candidates;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>
    /// Accept only HTTPS image delivery from known packaging CDNs. Some Cover Art
    /// Archive metadata still uses HTTP URLs; upgrade only known HTTP origins.
    /// This guards offered candidates and direct Jellyfin image download calls.
    /// It does not establish that the HTTP client's redirect handling is safe.
    /// </summary>
    public static string? NormalizeRemoteImageUrl(string? url, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.Fragment.Length > 0)
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        var allowed = source switch
        {
            CoverArtArchive => host is "coverartarchive.org" or "www.coverartarchive.org",
            AudioDb => host is "r2.theaudiodb.com" or "www.theaudiodb.com" or "theaudiodb.com",
            FanartTv => host is "assets.fanart.tv",
            Discogs => host is "i.discogs.com" or "img.discogs.com",
            null => host is "coverartarchive.org" or "www.coverartarchive.org"
                or "r2.theaudiodb.com" or "www.theaudiodb.com" or "theaudiodb.com"
                or "assets.fanart.tv" or "i.discogs.com" or "img.discogs.com",
            _ => false,
        };
        if (!allowed || (source == FanartTv && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return uri.AbsoluteUri;
        }

        // Normalize genuine legacy HTTP image links without allowing HTTP image fetches.
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri;
    }
}
