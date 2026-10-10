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
    public const string Discogs = "Discogs";

    // Real scans first: the Cover Art Archive labels every image by what it
    // is; TheAudioDB curates fewer; Discogs images are unlabelled.
    private const double CoverArtArchiveRank = 9;
    private const double AudioDbRank = 8;
    private const double DiscogsRank = 7;

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

            var url = ReadString(image, "image");
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
                thumbnail = ReadString(thumbnails, "500") ?? ReadString(thumbnails, "large");
            }

            candidates.Add(new PackagingImageCandidate(
                slot.Value,
                Https(url),
                thumbnail is null ? null : Https(thumbnail),
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
            var url = fields.Select(field => ReadString(album, field)).FirstOrDefault(value => value is not null);
            if (url is not null)
            {
                candidates.Add(new PackagingImageCandidate(
                    slot, Https(url), $"{Https(url)}/preview", null, null, AudioDb, AudioDbRank));
            }
        }

        Add(ImageType.BoxRear, "strAlbumBack", "strAlbumThumbBack");
        Add(ImageType.Disc, "strAlbumCDart");
        Add(ImageType.Primary, "strAlbumThumbHQ", "strAlbumThumb");
        Add(ImageType.Art, "strAlbumSpine");
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
    /// primary as the front, and the secondaries in their conventional upload
    /// order (back cover first, then inner material).
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

        var secondarySlots = new[] { ImageType.BoxRear, ImageType.Box, ImageType.Menu, ImageType.Art };
        var secondary = 0;
        foreach (var image in images.EnumerateArray())
        {
            var url = ReadString(image, "uri");
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
                ReadString(image, "uri150"),
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

    /// <summary>The archive still hands out http:// links; Jellyfin fetches over https.</summary>
    private static string Https(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url["http://".Length..]
            : url;
}
